using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// The frozen first frame of loading ends with the Start of every "Instantly" add-on, all in one frame: ~13 s in a
    /// heavily modded game (one mod reads every DLL with Cecil for 3.3 s, others check their install for 1.5 s each).
    /// Here the Instantly add-ons are held disabled right after their Awake, then enabled a few per frame, so Unity runs
    /// their Start over several frames and the loading screen keeps moving. The game database waits until every held
    /// add-on has started, so the order stays as in stock: all Instantly Starts run before loading goes on.
    /// Only mod add-ons that declare a Start and no OnEnable/OnDisable are held (disabling them calls nothing of theirs);
    /// the game's own and the excluded ones start as usual.
    /// </summary>
    internal sealed class StartPacing : Subsystem
    {
        private static readonly List<MonoBehaviour> s_held = new List<MonoBehaviour>();
        private static readonly List<string> s_notHeld = new List<string>();     // started as usual after a held add-on: order differs
        private static HashSet<string> s_exclude = new HashSet<string>(StringComparer.Ordinal);
        private static FieldInfo s_current;
        private static bool s_holding;          // add-ons created now are held, and the database waits
        private static int s_next;
        private static int s_holdFrame;         // the frozen frame itself runs as in stock
        private int _batch = 1, _maxPerFrame = 32, _lastRelease = -1, _frames;
        private float _budgetMs = 20f, _timeoutSeconds = 60f, _since;
        private long _startTicks;

        public override string Name => "StartPacing";

        // KSPCommunityFixes' always-on loader runs the texture loading in coroutines of its own, which holding the database
        // does not stop: measured, 681 more files loaded (ReStock's removals came after the texture list was taken).
        public override string[] IncompatibleAssemblies => new[] { "KSPCommunityFixes", "WarmStart" };

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _maxPerFrame = Mathf.Clamp(settings.GetInt("maxPerFrame", _maxPerFrame), 1, 1000);
            _budgetMs = Mathf.Clamp(settings.GetFloat("frameBudgetMs", _budgetMs), 5f, 200f);
            _timeoutSeconds = Mathf.Clamp(settings.GetFloat("timeoutSeconds", _timeoutSeconds), 5f, 600f);
            s_exclude = new HashSet<string>(settings.GetString("exclude", "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        }

        protected internal override void OnEnable()
        {
            if (HighLogic.LoadedScene != GameScenes.LOADING || GameDatabase.Instance == null || GameDatabase.Instance.IsReady())
                throw new SubsystemUnavailableException("only during the game's first load");
            // The loader to hold is whichever coroutine is starting the Instantly add-ons right now: the stock
            // GameDatabase.LoadObjects, or a mod's replacement of it (KSPCommunityFixes has its own).
            MethodInfo moveNext = FindRunningLoader();
            s_current = moveNext?.DeclaringType?.GetField("<>2__current", BindingFlags.Instance | BindingFlags.NonPublic);
            if (s_current == null)
                throw new SubsystemUnavailableException("the coroutine starting the Instantly add-ons was not found");
            // Another mod that patches the loader may run part of the loading in coroutines of its own (KSPCommunityFixes
            // loads the textures that way): holding the loader would no longer keep every Start before that work.
            foreach (MethodBase patched in Harmony.GetAllPatchedMethods())
            {
                Type type = patched.DeclaringType;
                if (type != moveNext.DeclaringType && type != typeof(GameDatabase) && type?.DeclaringType != typeof(GameDatabase))
                    continue;
                Patches info = Harmony.GetPatchInfo(patched);
                if (info != null)
                    foreach (string owner in info.Owners)
                        if (!owner.StartsWith("Hyperdrive.", StringComparison.Ordinal))
                            throw new SubsystemUnavailableException("the game database loading is also patched by '" + owner + "' (" + type.Name + "." + patched.Name + "): stock order could not be kept");
            }
            Patch(moveNext, prefix: nameof(HoldDatabase));
            Log.Info("StartPacing: holding " + moveNext.DeclaringType.FullName + " (" + moveNext.DeclaringType.Assembly.GetName().Name + ") while add-ons start");
            Patch(AccessTools.Method(typeof(AddonLoader), "StartAddon"), transpiler: nameof(StartAddonTranspiler));

            s_held.Clear();
            s_notHeld.Clear();
            s_next = 0;
            s_holding = true;
            s_holdFrame = Time.frameCount;
            _since = Time.realtimeSinceStartup;
            _startTicks = Stopwatch.GetTimestamp();
            // Instantly add-ons created before Hyperdrive in this same frame: their Start has not run yet either. Unity
            // starts them in creation order, which is AddonLoader.StartAddons' order: assemblies, then their types.
            var order = new Dictionary<Type, int>();
            int rank = 0;
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                Type[] types;
                try { types = loaded.assembly.GetTypes(); }
                catch (Exception) { continue; }
                foreach (Type type in types)
                    if (!order.ContainsKey(type))
                        order[type] = rank++;
            }
            var earlier = new List<MonoBehaviour>();
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (IsInstantlyAddon(behaviour.GetType()) && order.ContainsKey(behaviour.GetType()))
                    earlier.Add(behaviour);
            earlier.Sort((a, b) => order[a.GetType()].CompareTo(order[b.GetType()]));
            foreach (MonoBehaviour behaviour in earlier)
                Hold(behaviour);
            Log.Info("StartPacing: " + s_held.Count + " Instantly add-ons created before Hyperdrive held; their Start runs over the next frames");
        }

        protected internal override void OnDisable() => ReleaseAll();

        // Walks up from AddonLoader.StartAddons to the first compiler-made iterator: the coroutine that called it.
        private static MethodInfo FindRunningLoader()
        {
            StackFrame[] frames = new StackTrace(false).GetFrames();
            if (frames == null)
                return null;
            bool belowStartAddons = true;
            foreach (StackFrame frame in frames)
            {
                MethodBase method;
                try { method = Harmony.GetMethodFromStackframe(frame) ?? frame.GetMethod(); }
                catch (Exception) { method = frame.GetMethod(); }
                if (method == null)
                    continue;
                if (belowStartAddons)
                {
                    if (method.DeclaringType == typeof(AddonLoader) && method.Name == nameof(AddonLoader.StartAddons))
                        belowStartAddons = false;
                    continue;
                }
                if (method.Name == "MoveNext" && method is MethodInfo info && method.DeclaringType != null
                    && method.DeclaringType.GetField("<>2__current", BindingFlags.Instance | BindingFlags.NonPublic) != null)
                    return info;
            }
            return null;
        }

        // As AddonLoader decides: the first KSPAddon attribute.
        private static bool IsInstantlyAddon(Type type)
        {
            object[] attributes = type.GetCustomAttributes(typeof(KSPAddon), true);
            return attributes.Length > 0 && ((KSPAddon)attributes[0]).startup == KSPAddon.Startup.Instantly;
        }

        // AddonLoader.StartAddon: "new GameObject(type.Name).AddComponent(type)" -> AddAndHold(new GameObject(type.Name), type).
        private static IEnumerable<CodeInstruction> StartAddonTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo add = AccessTools.Method(typeof(GameObject), nameof(GameObject.AddComponent), new[] { typeof(Type) });
            MethodInfo hold = AccessTools.Method(typeof(StartPacing), nameof(AddAndHold));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(add))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = hold;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1)
                throw new InvalidOperationException("StartPacing: expected one AddComponent in AddonLoader.StartAddon, found " + replaced);
        }

        private static Component AddAndHold(GameObject host, Type type)
        {
            Component component = host.AddComponent(type);
            if (s_holding)
            {
                try { Hold(component as MonoBehaviour); }
                catch (Exception e) { Log.Exception("StartPacing: could not hold " + type.FullName + ", it starts as usual", e); }
            }
            return component;
        }

        private static void Hold(MonoBehaviour behaviour)
        {
            // Destroyed or switched off by its own Awake, or inactive: left alone.
            if (behaviour == null || !behaviour.enabled || !behaviour.gameObject.activeInHierarchy)
                return;
            Type type = behaviour.GetType();
            if (type.Assembly == typeof(Part).Assembly || type.Assembly == typeof(StartPacing).Assembly
                || s_exclude.Contains(type.Name) || s_exclude.Contains(type.FullName))
                return;
            if (!Declares(type, "Start"))
                return;
            if (Declares(type, "OnEnable") || Declares(type, "OnDisable"))
            {
                // Its Start runs in the stock frame, so before the held add-ons created ahead of it.
                if (s_held.Count > 0)
                    s_notHeld.Add(type.FullName);
                return;
            }
            behaviour.enabled = false;
            s_held.Add(behaviour);
        }

        private static bool Declares(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (; type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (MethodInfo method in type.GetMethods(flags))
                    if (method.Name == name && method.GetParameters().Length == 0)
                        return true;
            return false;
        }

        // The database coroutine waits (yields a frame) while add-ons are held. Not in the frame the add-ons were created:
        // there it goes on as in stock (nested loads that finish at once resume it in the same frame, before any Start).
        private static bool HoldDatabase(object __instance, ref bool __result)
        {
            if (!s_holding || Time.frameCount == s_holdFrame)
                return true;
            s_current.SetValue(__instance, null);
            __result = true;
            return false;
        }

        protected internal override void OnUpdate()
        {
            if (!s_holding)
                return;
            _frames++;
            if (s_next >= s_held.Count)
            {
                // Unity runs the Start of an add-on enabled during Update before its first Update: give it a frame.
                if (Time.frameCount > _lastRelease + 1)
                    Finish();
                return;
            }
            if (Time.realtimeSinceStartup - _since > _timeoutSeconds)
            {
                Log.Warn("StartPacing: still holding after " + _timeoutSeconds + " s, releasing every add-on at once");
                ReleaseAll();
                return;
            }
            _batch = Time.unscaledDeltaTime * 1000f < _budgetMs ? Mathf.Min(_batch * 2, _maxPerFrame) : 1;
            for (int i = 0; i < _batch && s_next < s_held.Count; i++)
            {
                MonoBehaviour behaviour = s_held[s_next++];
                if (behaviour != null)
                    behaviour.enabled = true;
            }
            _lastRelease = Time.frameCount;
        }

        private void Finish()
        {
            s_holding = false;
            Log.Info("StartPacing: " + s_held.Count + " Instantly add-ons started over " + _frames + " frames ("
                     + ((Stopwatch.GetTimestamp() - _startTicks) * 1000 / Stopwatch.Frequency) + " ms); database loading resumes"
                     + (s_notHeld.Count > 0 ? ". Started ahead of their turn (OnEnable/OnDisable, not held): " + string.Join(", ", s_notHeld.ToArray()) : ""));
            s_held.Clear();
        }

        private static void ReleaseAll()
        {
            for (; s_next < s_held.Count; s_next++)
                if (s_held[s_next] != null)
                    s_held[s_next].enabled = true;
            s_holding = false;
            s_held.Clear();
        }
    }
}
