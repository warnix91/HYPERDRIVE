using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;

namespace Hyperdrive.Patching
{
    /// <summary>
    /// Other optimization mods: Hyperdrive keeps their effects and leaves them every job they also do.
    /// - KSPCommunityFixes, always-on loader (FastLoader, GameDatabasePerf): matched by assembly (IncompatibleAssemblies).
    /// - KSPCommunityFixes, optional patches: matched by name (Subsystem.KspcfEquivalents); the better of the two is kept.
    ///   By default KSPCF's (keepOurs lists the exceptions, where Hyperdrive's does measurably more). KSPCF reads its
    ///   settings after ModuleManager, then applies its patches one by one through BasePatch.Patch(Type). On its first
    ///   call (every setting known, nothing applied yet), a prefix takes the keepOurs patches out of its enabled set, as a
    ///   player setting them false would, then turns off each Hyperdrive subsystem whose KSPCF equivalent stays enabled
    ///   and turns on those that stood down on a guess but whose equivalent is off. Before that, the guess comes from
    ///   KSPCF's own cfg files (its folder only); anything unclear counts as enabled. After KSPCF has applied its patches,
    ///   a Hyperdrive subsystem whose equivalent it applied anyway stands down.
    /// - Any other mod (yieldToAllMods, default; else only the optimization mods listed in optimizerAssemblies): once
    ///   KSPCF has applied its patches, at the main menu, and again at the first space centre and the first flight (mods
    ///   that patch late), a Hyperdrive subsystem with YieldOnSharedTargets stands down if another mod changes what one of
    ///   its methods does (a transpiler, or a prefix that can skip the original): the other mod's version is kept as it
    ///   wrote it. Additions (postfixes, void prefixes, finalizers) are another job: both run.
    /// If KSPCF changes so that the hooks cannot be found, every subsystem with a KSPCF equivalent stands down.
    /// </summary>
    internal static class OtherOptimizers
    {
        internal const string KspcfAssembly = "KSPCommunityFixes";
        private const string HarmonyId = SubsystemHost.HarmonyIdPrefix + "Compatibility";

        private static SubsystemHost s_host;
        private static Harmony s_harmony;
        private static Assembly s_kspcf;
        private static HashSet<string> s_kspcfEnabled;       // KSPCF's own set, once it has read its settings
        private static Dictionary<string, bool> s_guess;     // from its cfg files, until then
        private static bool s_hooked, s_settingsSeen;
        private static bool s_yieldToAll = true;
        private static FieldInfo s_instancesField;
        // KSPCF patches whose Hyperdrive counterpart does more: KSPCF's is turned off (as by its own setting) while ours runs.
        private static readonly HashSet<string> s_keepOurs = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_optimizers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            KspcfAssembly, "SmoothRunning", "KSPTextureLoader", "MemGraph", "HeapPadder", "DDSLoader", "LoadingScreenManager",
            "WarmStart", "WarmStartHarmony", "Keystone", "KeystoneHarmony"
        };

        internal static bool KspcfLoaded => s_kspcf != null;

        // Compatibility { PREFER { Assembly:PatchClass = ours } }: patches of other mods (not KSPCF) that do a job a Hyperdrive
        // subsystem does better on this install (measured): their patch is removed from that method instead of Hyperdrive's
        // subsystem standing down. Nothing is listed by default: without a measure, the other mod keeps its job.
        private static readonly HashSet<string> s_oursOver = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static void Init(SubsystemHost host, SettingsSection settings)
        {
            s_host = host;
            var legacyKeepOurs = new List<string>();
            foreach (string name in settings.GetString("keepOurs", "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                legacyKeepOurs.Add(name.Trim());
            s_yieldToAll = settings.GetBool("yieldToAllMods", true);
            ConfigNode[] prefer = settings.GetNodes("PREFER");   // the player's first, then the shipped defaults
            for (int i = prefer.Length - 1; i >= 0; i--)
                foreach (ConfigNode.Value v in prefer[i].values)
                    if (v.name.IndexOf(':') > 0)
                    {
                        if (string.Equals(v.value.Trim(), "ours", StringComparison.OrdinalIgnoreCase))
                            s_oursOver.Add(v.name.Trim());
                        else
                            s_oursOver.Remove(v.name.Trim());
                    }
            string extra = settings.GetString("optimizerAssemblies", "");
            foreach (string name in extra.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                s_optimizers.Add(name.Trim());

            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
                if (loaded?.assembly != null && string.Equals(loaded.name, KspcfAssembly, StringComparison.OrdinalIgnoreCase))
                    s_kspcf = loaded.assembly;
            if (s_kspcf == null)
                return;
            // Who keeps each job both do: the player's choice, what this install learned, or the measured default.
            try
            {
                foreach (string name in Overlaps.Decide(settings, legacyKeepOurs, Guess, host.IsConfiguredOn("FrameTimeline"), Core.HyperdriveRuntime.ModDirectory))
                    s_keepOurs.Add(name);
            }
            catch (Exception e)
            {
                Log.Exception("Compatibility: overlap decisions failed, KSPCommunityFixes keeps every job it also does", e);
                s_keepOurs.Clear();
            }

            try
            {
                Type main = s_kspcf.GetType("KSPCommunityFixes.KSPCommunityFixes");
                Type basePatch = s_kspcf.GetType("KSPCommunityFixes.BasePatch");
                MethodInfo patch = basePatch != null ? AccessTools.Method(basePatch, "Patch", new[] { typeof(Type) }) : null;
                MethodInfo postLoad = main != null ? AccessTools.Method(main, "MMPostLoadCallback") : null;
                FieldInfo enabled = main != null ? AccessTools.Field(main, "enabledPatches") : null;
                s_instancesField = main != null ? AccessTools.Field(main, "patchInstances") : null;
                if (patch != null && enabled != null && enabled.IsStatic && enabled.FieldType == typeof(HashSet<string>))
                {
                    s_enabledField = enabled;
                    s_harmony = new Harmony(HarmonyId);
                    s_harmony.Patch(patch, prefix: new HarmonyMethod(AccessTools.Method(typeof(OtherOptimizers), nameof(KspcfPatchPrefix))));
                    if (postLoad != null)
                        s_harmony.Patch(postLoad, postfix: new HarmonyMethod(AccessTools.Method(typeof(OtherOptimizers), nameof(KspcfPostLoadPostfix))));
                    s_hooked = true;
                }
            }
            catch (Exception e)
            {
                Log.Warn("Compatibility: KSPCommunityFixes hooks failed (" + e.Message + "), its optional features are all left to it");
                try { s_harmony?.UnpatchAll(HarmonyId); } catch (Exception) { }
                s_hooked = false;
            }
            Log.Info("Compatibility: KSPCommunityFixes " + s_kspcf.GetName().Version + " found; "
                     + (s_hooked ? "Hyperdrive features it also has are decided by its settings" : "Hyperdrive leaves it every feature it also has"));
        }

        private static FieldInfo s_enabledField;

        internal static void Shutdown()
        {
            try { s_harmony?.UnpatchAll(HarmonyId); } catch (Exception) { }
            s_harmony = null;
        }

        /// <summary>The first KSPCF patch among <paramref name="names"/> that is (or, before its settings, may be) enabled.</summary>
        internal static string ActiveKspcfEquivalent(string[] names)
        {
            if (s_kspcf == null || names == null || names.Length == 0)
                return null;
            if (!s_hooked)
                return names[0];
            for (int i = 0; i < names.Length; i++)
            {
                if (s_keepOurs.Contains(names[i]))
                    continue;   // turned off in KSPCF when its settings are read
                if (s_kspcfEnabled != null)
                {
                    if (s_kspcfEnabled.Contains(names[i]))
                        return names[i];
                }
                else if (Guess(names[i]))
                    return names[i];
            }
            return null;
        }

        internal static bool KspcfSettingsKnown => s_kspcfEnabled != null || !s_hooked;

        // KSP_COMMUNITY_FIXES values in KSPCF's folder: its Settings.cfg, then the user files that edit it (a value that
        // is not plainly "false" counts as enabled, as KSPCF does). A patch not mentioned is enabled.
        private static bool Guess(string name)
        {
            if (s_guess == null)
            {
                s_guess = new Dictionary<string, bool>(StringComparer.Ordinal);
                try
                {
                    string folder = Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "KSPCommunityFixes");
                    string settings = Path.Combine(folder, "Settings.cfg");
                    var files = new List<string>();
                    if (File.Exists(settings))
                        files.Add(settings);
                    foreach (string file in Directory.GetFiles(folder, "*.cfg", SearchOption.AllDirectories))
                        if (!string.Equals(file, settings, StringComparison.OrdinalIgnoreCase))
                            files.Add(file);
                    foreach (string file in files)
                        ReadGuesses(file);
                }
                catch (Exception e)
                {
                    Log.Info("Compatibility: KSPCommunityFixes settings not readable before ModuleManager (" + e.Message + ")");
                }
            }
            return !s_guess.TryGetValue(name, out bool on) || on;
        }

        private static void ReadGuesses(string file)
        {
            bool inNode = false;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw;
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0)
                    line = line.Substring(0, comment);
                line = line.Trim();
                if (line.Length == 0)
                    continue;
                if (line.IndexOf("KSP_COMMUNITY_FIXES", StringComparison.Ordinal) >= 0)
                {
                    inNode = true;
                    continue;
                }
                if (!inNode)
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = line.Substring(0, eq).Trim().TrimStart('@', '%', '&');
                int colon = key.IndexOf(':');
                if (colon >= 0)
                    key = key.Substring(0, colon);
                string value = line.Substring(eq + 1).Trim();
                if (key.Length > 0)
                    s_guess[key] = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            }
        }

        // First call with its settings read (KSPCF's loader applies a few patches earlier, before the settings exist):
        // nothing optional is applied yet.
        private static void KspcfPatchPrefix()
        {
            if (s_settingsSeen)
                return;
            try
            {
                if (!(s_enabledField.GetValue(null) is HashSet<string> enabled) || enabled.Count == 0)
                    return;
                s_settingsSeen = true;
                s_kspcfEnabled = enabled;
                foreach (string name in new List<string>(s_keepOurs))
                {
                    string ours = s_host?.SubsystemCovering(name);
                    if (ours == null)
                    {
                        s_keepOurs.Remove(name);   // nothing of ours does it here: KSPCF's stays as set
                        continue;
                    }
                    if (s_kspcfEnabled.Remove(name))
                        Log.Info("Compatibility: KSPCommunityFixes " + name + " turned off, Hyperdrive " + ours + " does more (Compatibility keepOurs)");
                }
                try { Overlaps.ConfirmTrial(n => s_kspcfEnabled.Contains(n), n => s_host?.SubsystemCovering(n)); }
                catch (Exception e) { Log.Exception("Compatibility: overlap trial check failed", e); }
                s_host?.ReviewKspcfEquivalents();
            }
            catch (Exception e)
            {
                Log.Exception("Compatibility: review of KSPCommunityFixes settings failed", e);
            }
        }

        /// <summary>Type names of the patches KSPCF actually applied, or null if not readable.</summary>
        internal static HashSet<string> KspcfApplied()
        {
            if (s_instancesField == null || !(s_instancesField.GetValue(null) is System.Collections.IDictionary instances))
                return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (object key in instances.Keys)
                if (key is Type t)
                    names.Add(t.Name);
            return names;
        }

        private static void KspcfPostLoadPostfix()
        {
            try { s_host?.ReviewKspcfApplied(KspcfApplied()); }
            catch (Exception e) { Log.Exception("Compatibility: review of applied KSPCommunityFixes patches failed", e); }
            try { s_host?.ReviewSharedTargets("after KSPCommunityFixes applied its patches"); }
            catch (Exception e) { Log.Exception("Compatibility: shared method review failed", e); }
        }

        /// <summary>
        /// The first patch of another mod (assembly and class; with yieldToAllMods off, only optimization mods) that
        /// changes what <paramref name="method"/> does: a transpiler, or a prefix returning bool (it can skip the original).
        /// Null if none.
        /// </summary>
        internal static string OptimizerReplacing(MethodBase method)
        {
            Patch p = ReplacingPatch(method);
            return p != null ? p.PatchMethod.DeclaringType?.Assembly.GetName().Name + " (" + p.PatchMethod.DeclaringType?.Name + ")" : null;
        }

        /// <summary>The other mod's patch that replaces what <paramref name="method"/> does (see OptimizerReplacing), or null.</summary>
        internal static Patch ReplacingPatch(MethodBase method)
        {
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null)
                return null;
            return Find(info.Transpilers, false) ?? Find(info.Prefixes, true);
        }

        /// <summary>
        /// When the player (or a measure) prefers Hyperdrive's version over this patch: removes it from the method and returns
        /// true; the other mod's other patches stay. False: the other mod keeps the job.
        /// </summary>
        internal static bool TakeOver(MethodBase method, Patch patch, string subsystem)
        {
            MethodInfo m = patch?.PatchMethod;
            string key = m?.DeclaringType != null ? m.DeclaringType.Assembly.GetName().Name + ":" + m.DeclaringType.Name : null;
            if (key == null || !s_oursOver.Contains(key))
                return false;
            try
            {
                (s_harmony ?? (s_harmony = new Harmony(HarmonyId))).Unpatch(method, m);
                Log.Info("Compatibility: " + key + "." + m.Name + " removed from " + method.DeclaringType?.Name + "." + method.Name + ", Hyperdrive "
                         + subsystem + " keeps this job (Compatibility PREFER " + key + " = ours)");
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("Compatibility: could not remove " + key + " from " + method.Name + " (" + e.Message + "), it keeps the job");
                return false;
            }
        }

        private static Patch Find(IList<Patch> patches, bool skippingPrefixesOnly)
        {
            if (patches == null)
                return null;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch p = patches[i];
                if (p?.owner == null || p.owner.StartsWith(SubsystemHost.HarmonyIdPrefix, StringComparison.Ordinal))
                    continue;
                MethodInfo patch = p.PatchMethod;
                if (patch == null || skippingPrefixesOnly && patch.ReturnType != typeof(bool))
                    continue;
                string assembly = patch.DeclaringType?.Assembly.GetName().Name;
                if (assembly != null && (s_yieldToAll || s_optimizers.Contains(assembly)))
                    return p;
            }
            return null;
        }
    }
}
