using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// Every Part and PartModule builds its event and action lists in Awake (BaseEventList.CreateDelegates,
    /// BaseActionList.ReflectActions): one Delegate.CreateDelegate(type, target, methodName, false) per KSPEvent and
    /// KSPAction, and that overload looks the method up by name among all the methods of the target's type, every time.
    /// Measured on a heavy modpack (15000 parts and modules during the first load, Mac): 0.46 s, and again for every
    /// part of every vessel loaded later. Here the first call for a given delegate type, target type and name is the
    /// stock call; the method it bound is kept, and the next targets of the same type get a delegate on that same method
    /// (Delegate.CreateDelegate(type, target, method)): the very method stock would find, since the lookup only depends on
    /// the target's type and the name. Failures are never cached (stock throws as before). verify = true: every delegate
    /// is also made the stock way and compared (method and target).
    /// </summary>
    internal sealed class DelegateCache : Subsystem
    {
        private static readonly MethodInfo ByName = AccessTools.Method(typeof(Delegate), nameof(Delegate.CreateDelegate),
            new[] { typeof(Type), typeof(object), typeof(string), typeof(bool) });
        private static readonly object s_lock = new object();
        private static readonly Dictionary<Type, Dictionary<Type, Dictionary<string, MethodInfo>>> s_cache =
            new Dictionary<Type, Dictionary<Type, Dictionary<string, MethodInfo>>>();
        private static bool s_verify;
        private static int s_replaced;
        private static long s_hits, s_misses, s_mismatches;

        public override string Name => "DelegateCache";
        public override bool YieldOnSharedTargets => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_verify = settings.GetBool("verify", false);
        }

        protected internal override void OnEnable()
        {
            if (ByName == null)
                throw new SubsystemUnavailableException("Delegate.CreateDelegate(Type, object, string, bool) not found");
            MethodInfo events = AccessTools.Method(typeof(BaseEventList), "CreateDelegates");
            MethodInfo actions = AccessTools.Method(typeof(BaseActionList), "ReflectActions");
            if (events == null || actions == null)
                throw new SubsystemUnavailableException("BaseEventList.CreateDelegates / BaseActionList.ReflectActions not found (game version mismatch?)");
            s_replaced = 0;
            Patch(events, transpiler: nameof(Transpiler));
            Patch(actions, transpiler: nameof(Transpiler));
            if (s_replaced != 3)
                throw new InvalidOperationException(s_replaced + " CreateDelegate calls found, 3 expected (game version mismatch?)");
            Log.Info("DelegateCache: part and module event/action delegates bound to the method found once per type and name"
                     + (s_verify ? " (verify: each one also made the stock way and compared)" : ""));
        }

        protected internal override void OnDisable()
        {
            lock (s_lock)
                s_cache.Clear();
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU && s_hits + s_misses > 0)
                Log.Info("DelegateCache: " + s_hits + " delegates from the cache, " + s_misses + " looked up by name"
                         + (s_verify ? "; verify: " + s_mismatches + " different from stock" : ""));
        }

        // call Delegate::CreateDelegate(Type, object, string, bool) -> call DelegateCache::Create(Type, object, string, bool):
        // same arguments on the stack, same return type.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo create = AccessTools.Method(typeof(DelegateCache), nameof(Create));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo m && m == ByName)
                {
                    instruction.operand = create;
                    s_replaced++;
                }
                yield return instruction;
            }
        }

        private static Delegate Create(Type type, object target, string method, bool ignoreCase)
        {
            if (type == null || target == null || method == null || ignoreCase)
                return Delegate.CreateDelegate(type, target, method, ignoreCase);
            Type targetType = target.GetType();
            MethodInfo bound = null;
            Dictionary<string, MethodInfo> names = null;
            lock (s_lock)
            {
                if (!s_cache.TryGetValue(type, out Dictionary<Type, Dictionary<string, MethodInfo>> types))
                    s_cache[type] = types = new Dictionary<Type, Dictionary<string, MethodInfo>>();
                if (!types.TryGetValue(targetType, out names))
                    types[targetType] = names = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
                names.TryGetValue(method, out bound);
            }
            if (bound != null)
            {
                s_hits++;
                Delegate fast = Delegate.CreateDelegate(type, target, bound);
                if (s_verify)
                {
                    Delegate stock = Delegate.CreateDelegate(type, target, method, false);
                    if (stock.Method != fast.Method || !ReferenceEquals(stock.Target, fast.Target))
                    {
                        s_mismatches++;
                        if (s_mismatches <= 10)
                            Log.Warn("DelegateCache verify: " + targetType.Name + "." + method + " bound to " + fast.Method + ", stock " + stock.Method);
                        return stock;
                    }
                }
                return fast;
            }
            s_misses++;
            Delegate result = Delegate.CreateDelegate(type, target, method, false);
            if (result != null && ReferenceEquals(result.Target, target) && result.Method != null)
                lock (s_lock)
                    names[method] = result.Method;
            return result;
        }
    }
}
