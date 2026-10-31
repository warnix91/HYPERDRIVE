using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;

namespace Hyperdrive.Patching
{
    /// <summary>
    /// Owns every subsystem: reads the config switch, checks incompatible mods, enables each one in
    /// isolation, reports Harmony conflicts, and disables (with full rollback) any subsystem that throws.
    /// </summary>
    internal sealed class SubsystemHost
    {
        public const string HarmonyIdPrefix = "Hyperdrive.";

        private readonly List<Subsystem> _all = new List<Subsystem>();
        private readonly List<Subsystem> _active = new List<Subsystem>();
        private readonly HyperdriveSettings _settings;

        public SubsystemHost(HyperdriveSettings settings)
        {
            _settings = settings;
        }

        public IReadOnlyList<Subsystem> All => _all;

        public void Register(Subsystem subsystem) => _all.Add(subsystem);

        /// <summary>The game's version, e.g. 1.12.5.</summary>
        internal static string GameVersion => Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision;

        /// <summary>
        /// Every patch was written and checked against KSP 1.12 (1.12.5 measured; the transpilers check their call sites).
        /// On another version only read-only subsystems run, unless Compatibility { allowOtherGameVersions = true }.
        /// </summary>
        internal static bool GameVersionSupported => s_allowOtherVersions || (Versioning.version_major == 1 && Versioning.version_minor == 12);

        internal static bool s_allowOtherVersions;

        /// <summary>Whether a subsystem is switched on in the settings (whether or not it runs).</summary>
        internal bool IsConfiguredOn(string name)
        {
            for (int i = 0; i < _all.Count; i++)
                if (string.Equals(_all[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return _settings.IsSubsystemEnabled(_all[i].Name, _all[i].EnabledByDefault);
            return false;
        }

        public bool IsActive(string name)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                if (string.Equals(_active[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>The active instance of a subsystem type, or null if it is not running.</summary>
        public T GetActive<T>() where T : Subsystem
        {
            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i] is T match)
                    return match;
            }
            return null;
        }

        public void EnableAll()
        {
            for (int i = 0; i < _all.Count; i++)
                Enable(_all[i]);

            Log.Info("Subsystems: " + Summary());
        }

        private void Enable(Subsystem subsystem)
        {
            if (!_settings.IsSubsystemEnabled(subsystem.Name, subsystem.EnabledByDefault))
            {
                SetState(subsystem, SubsystemState.DisabledByConfig, "disabled in config");
                return;
            }

            if (!subsystem.ReadOnly && !GameVersionSupported)
            {
                SetState(subsystem, SubsystemState.StoodDown, "made for KSP 1.12, this game is " + GameVersion);
                return;
            }

            if (!subsystem.ReadOnly && Core.PrudentStart.Skips(subsystem.Name))
            {
                SetState(subsystem, SubsystemState.StoodDown, "prudent start: the game stopped abruptly in " + Core.PrudentStart.StoppedIn + " last time (back at the next start)");
                return;
            }

            string blocker = FindLoadedAssembly(subsystem.IncompatibleAssemblies);
            if (blocker != null)
            {
                SetState(subsystem, SubsystemState.StoodDown, "standing down because '" + blocker + "' is installed");
                Log.Info(subsystem.Name + ": " + subsystem.StateReason);
                return;
            }

            subsystem.WaitingForKspcf = false;
            string equivalent = OtherOptimizers.ActiveKspcfEquivalent(subsystem.KspcfEquivalents);
            if (equivalent != null)
            {
                subsystem.WaitingForKspcf = !OtherOptimizers.KspcfSettingsKnown;
                SetState(subsystem, SubsystemState.StoodDown, "KSPCommunityFixes " + equivalent + " does this"
                         + (subsystem.WaitingForKspcf ? " (if enabled: decided again once its settings are read)" : ""));
                Log.Info(subsystem.Name + ": " + subsystem.StateReason);
                return;
            }

            try
            {
                subsystem.AttachHarmony();
                subsystem.LoadSettings(_settings.Section(subsystem.Name));
                subsystem.OnEnable();
                SetState(subsystem, SubsystemState.Active, null);
                _active.Add(subsystem);
                ReportForeignPatches(subsystem);
            }
            catch (SubsystemUnavailableException e)
            {
                Rollback(subsystem);
                SetState(subsystem, SubsystemState.StoodDown, e.Message);
                Log.Info(subsystem.Name + ": not available here, standing down (" + e.Message + ")");
            }
            catch (Exception e)
            {
                Log.Exception(subsystem.Name + " failed to start and was rolled back", e);
                Rollback(subsystem);
                SetState(subsystem, SubsystemState.Failed, e.GetType().Name + ": " + e.Message);
            }
        }

        public void Update()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Subsystem subsystem = _active[i];
                try
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    subsystem.OnUpdate();
                    long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    if (ticks > System.Diagnostics.Stopwatch.Frequency / 20)   // > 50 ms: rare, worth a line
                        Log.Info(subsystem.Name + ".OnUpdate took " + (ticks * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms in " + HighLogic.LoadedScene);
                }
                catch (Exception e)
                {
                    Fail(subsystem, "OnUpdate", e);
                }
            }
        }

        public void SceneReady(GameScenes scene)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Subsystem subsystem = _active[i];
                try
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    subsystem.OnSceneReady(scene);
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (ms > 20.0)
                        Log.Info(subsystem.Name + ".OnSceneReady(" + scene + ") took " + ms.ToString("F0") + " ms");
                }
                catch (Exception e)
                {
                    Fail(subsystem, "OnSceneReady", e);
                }
            }
        }

        /// <summary>
        /// KSPCommunityFixes has read its settings and applies nothing yet: subsystems whose KSPCF equivalent is enabled
        /// step aside now, and those that stood down on a guess start if their equivalent is off.
        /// </summary>
        public void ReviewKspcfEquivalents()
        {
            for (int i = 0; i < _all.Count; i++)
            {
                Subsystem subsystem = _all[i];
                if (subsystem.KspcfEquivalents.Length == 0)
                    continue;
                string equivalent = OtherOptimizers.ActiveKspcfEquivalent(subsystem.KspcfEquivalents);
                if (equivalent != null && subsystem.State == SubsystemState.Active)
                    StandAside(subsystem, "KSPCommunityFixes " + equivalent + " is enabled and does this");
                else if (equivalent == null && subsystem.WaitingForKspcf)
                {
                    Log.Info(subsystem.Name + ": KSPCommunityFixes " + subsystem.KspcfEquivalents[0] + " is off, starting");
                    Enable(subsystem);
                }
                else if (equivalent != null && subsystem.WaitingForKspcf)
                {
                    subsystem.WaitingForKspcf = false;
                    SetState(subsystem, SubsystemState.StoodDown, "KSPCommunityFixes " + equivalent + " is enabled and does this");
                }
            }
        }

        /// <summary>Name of a running (or waiting) subsystem that has <paramref name="kspcfPatch"/> as KSPCF equivalent.</summary>
        public string SubsystemCovering(string kspcfPatch)
        {
            for (int i = 0; i < _all.Count; i++)
            {
                Subsystem subsystem = _all[i];
                if ((subsystem.State == SubsystemState.Active || subsystem.WaitingForKspcf)
                    && Array.IndexOf(subsystem.KspcfEquivalents, kspcfPatch) >= 0)
                    return subsystem.Name;
            }
            return null;
        }

        /// <summary>After KSPCF applied its patches: a subsystem whose equivalent it applied anyway stands down.</summary>
        public void ReviewKspcfApplied(HashSet<string> applied)
        {
            if (applied == null)
                return;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Subsystem subsystem = _active[i];
                foreach (string name in subsystem.KspcfEquivalents)
                {
                    if (!applied.Contains(name))
                        continue;
                    StandAside(subsystem, "KSPCommunityFixes applied " + name);
                    break;
                }
            }
        }

        /// <summary>Active subsystems with YieldOnSharedTargets stand down if another optimization mod replaces one of their methods.</summary>
        public void ReviewSharedTargets(string when)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Subsystem subsystem = _active[i];
                if (!subsystem.YieldOnSharedTargets || subsystem.Harmony == null)
                    continue;
                foreach (MethodBase method in new List<MethodBase>(subsystem.Harmony.GetPatchedMethods()))
                {
                    HarmonyLib.Patch patch = OtherOptimizers.ReplacingPatch(method);
                    // Hyperdrive's version preferred over that patch (Compatibility PREFER): the other mod's patch goes.
                    for (int guard = 0; patch != null && guard < 8 && OtherOptimizers.TakeOver(method, patch, subsystem.Name); guard++)
                    {
                        HarmonyLib.Patch next = OtherOptimizers.ReplacingPatch(method);
                        if (next != null && next.PatchMethod == patch.PatchMethod)
                            break;   // not removed after all: the other mod keeps it, Hyperdrive stands down below
                        patch = next;
                    }
                    if (patch == null)
                        continue;
                    string other = OtherOptimizers.OptimizerReplacing(method);
                    StandAside(subsystem, other + " replaces " + method.DeclaringType?.Name + "." + method.Name + " (" + when + ")");
                    break;
                }
            }
        }

        private void StandAside(Subsystem subsystem, string reason)
        {
            _active.Remove(subsystem);
            Rollback(subsystem);
            subsystem.WaitingForKspcf = false;
            SetState(subsystem, SubsystemState.StoodDown, reason);
            Log.Info(subsystem.Name + ": standing down, " + reason);
        }

        public void DisableAll()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
                Rollback(_active[i]);
            _active.Clear();
        }

        private void Fail(Subsystem subsystem, string phase, Exception e)
        {
            Log.Exception(subsystem.Name + " threw in " + phase + " and was disabled", e);
            _active.Remove(subsystem);
            Rollback(subsystem);
            SetState(subsystem, SubsystemState.Failed, phase + ": " + e.GetType().Name + ": " + e.Message);
        }

        private static void Rollback(Subsystem subsystem)
        {
            try
            {
                subsystem.OnDisable();
            }
            catch (Exception e)
            {
                Log.Exception(subsystem.Name + " threw while disabling", e);
            }

            try
            {
                // Always pass our own id: UnpatchAll(null) would remove the patches of EVERY mod.
                subsystem.Harmony?.UnpatchAll(subsystem.HarmonyId);
            }
            catch (Exception e)
            {
                Log.Exception(subsystem.Name + " failed to remove its Harmony patches", e);
            }
        }

        private static void SetState(Subsystem subsystem, SubsystemState state, string reason)
        {
            subsystem.State = state;
            subsystem.StateReason = reason;
        }

        /// <summary>Warn when another mod patches a method that this subsystem also patches.</summary>
        private static void ReportForeignPatches(Subsystem subsystem)
        {
            foreach (MethodBase method in subsystem.Harmony.GetPatchedMethods())
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null)
                    continue;

                List<string> foreign = null;
                foreach (string owner in info.Owners)
                {
                    if (owner.StartsWith(HarmonyIdPrefix, StringComparison.Ordinal))
                        continue;
                    (foreign ??= new List<string>()).Add(owner);
                }

                if (foreign != null)
                    Log.Warn(subsystem.Name + ": " + method.DeclaringType?.FullName + "." + method.Name
                             + " is also patched by: " + string.Join(", ", foreign.ToArray())
                             + ". If you see issues, disable " + subsystem.Name + " in Hyperdrive.cfg.");
            }
        }

        private static string FindLoadedAssembly(string[] names)
        {
            if (names == null || names.Length == 0)
                return null;

            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(loaded.name, names[i], StringComparison.OrdinalIgnoreCase))
                        return loaded.name;
                }
            }
            return null;
        }

        public string Summary()
        {
            var parts = new string[_all.Count];
            for (int i = 0; i < _all.Count; i++)
            {
                Subsystem s = _all[i];
                parts[i] = s.Name + "=" + s.State + (s.StateReason != null ? " (" + s.StateReason + ")" : "");
            }
            return string.Join(", ", parts);
        }
    }
}
