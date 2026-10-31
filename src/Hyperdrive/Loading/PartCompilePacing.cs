using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// First load: PartLoader.CompileAll runs its steps (parts, internal props, internal spaces) as nested coroutines,
    /// each started with StartCoroutine, and each part step waits for a new frame whenever its slice of time is used.
    /// Every nested start and every wait costs a whole frame (loading screen drawn, vsync). Here the whole compilation runs
    /// as one flattened coroutine: nested steps are run in place, and a frame is only given back once budgetMs of work
    /// is done (as KSPCF's loader does). Steps that need real frames are left to Unity: the drag cube rendering
    /// (DragCubeSystem coroutines), and anything that yields a YieldInstruction, a CustomYieldInstruction or an
    /// AsyncOperation. Same parts, same order, same code; an exception in a nested step is logged and the step dropped,
    /// as Unity does for a coroutine. Stands down with KSPCF (its loader does this) and Warm Start.
    /// </summary>
    internal sealed class PartCompilePacing : Subsystem
    {
        private static readonly MethodInfo StartCoroutine = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), new[] { typeof(IEnumerator) });
        private static float s_budget = 0.03f;
        private static int s_replaced;
        private static long s_steps, s_frames, s_nested, s_toUnity;

        public override string Name => "PartCompilePacing";
        public override string[] IncompatibleAssemblies => new[] { "KSPCommunityFixes", "WarmStart" };
        public override bool YieldOnSharedTargets => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_budget = Mathf.Clamp(settings.GetFloat("budgetMs", 30f), 8f, 200f) / 1000f;
        }

        protected internal override void OnEnable()
        {
            if (HighLogic.LoadedScene != GameScenes.LOADING)
                throw new SubsystemUnavailableException("only during the game's first load");
            MethodInfo startLoad = AccessTools.Method(typeof(PartLoader), "StartLoad");
            MethodInfo compileAll = AccessTools.Method(typeof(PartLoader), "CompileAll");
            if (startLoad == null || compileAll == null || StartCoroutine == null)
                throw new SubsystemUnavailableException("PartLoader.StartLoad / CompileAll not found (game version mismatch?)");
            s_replaced = 0;
            Patch(startLoad, transpiler: nameof(StartLoadTranspiler));
            if (s_replaced != 1)
                throw new InvalidOperationException("PartLoader.StartLoad: " + s_replaced + " CompileAll starts found, 1 expected (game version mismatch?)");
            int nested = 0;
            foreach (string step in new[] { "CompileAll", "CompileParts", "CompileInternalProps", "CompileInternalSpaces" })
            {
                MethodInfo m = AccessTools.Method(typeof(PartLoader), step);
                MethodInfo moveNext = m != null ? AccessTools.EnumeratorMoveNext(m) : null;
                if (moveNext == null)
                    throw new SubsystemUnavailableException("PartLoader." + step + " not found (game version mismatch?)");
                s_replaced = 0;
                Patch(moveNext, transpiler: nameof(NestedTranspiler));
                nested += s_replaced;
            }
            if (nested < 4)
                throw new InvalidOperationException("PartLoader: " + nested + " nested coroutine starts found, at least 4 expected (game version mismatch?)");
            Log.Info("PartCompilePacing: part compilation run as one coroutine, a frame given back every " + (s_budget * 1000f).ToString("F0") + " ms of work");
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU && s_steps > 0)
                Log.Info("PartCompilePacing: " + s_steps + " steps in " + s_frames + " frames, " + s_nested + " nested steps run in place, "
                         + s_toUnity + " waits left to Unity");
        }

        // StartCoroutine(this.CompileAll()) -> StartCoroutine(Flatten(this.CompileAll())).
        private static IEnumerable<CodeInstruction> StartLoadTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo compileAll = AccessTools.Method(typeof(PartLoader), "CompileAll");
            MethodInfo flatten = AccessTools.Method(typeof(PartCompilePacing), nameof(Flatten));
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo m && m == compileAll)
                {
                    s_replaced++;
                    yield return new CodeInstruction(OpCodes.Call, flatten);
                }
            }
        }

        // Inside the compile steps: StartCoroutine(step) -> Nested(this, step), which hands the step itself to Flatten.
        private static IEnumerable<CodeInstruction> NestedTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo nested = AccessTools.Method(typeof(PartCompilePacing), nameof(Nested));
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo m && m == StartCoroutine)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = nested;
                    s_replaced++;
                }
                yield return instruction;
            }
        }

        // The drag cube rendering positions the part and needs a rendered frame: started as a real coroutine.
        private static object Nested(MonoBehaviour owner, IEnumerator routine)
        {
            if (routine == null)
                return owner.StartCoroutine(routine);
            string type = routine.GetType().FullName ?? "";
            if (type.IndexOf("DragCube", StringComparison.Ordinal) >= 0)
                return owner.StartCoroutine(routine);
            return routine;
        }

        private static IEnumerator Flatten(IEnumerator root)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            float sliceEnd = Time.realtimeSinceStartup + s_budget;
            while (stack.Count > 0)
            {
                IEnumerator current = stack.Peek();
                bool more;
                try { more = current.MoveNext(); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    stack.Pop();
                    if (current == root)
                        yield break;   // as Unity: the coroutine stops
                    continue;          // a nested step stops; the step that started it goes on
                }
                if (!more)
                {
                    stack.Pop();
                    continue;
                }
                s_steps++;
                object yielded = current.Current;
                if (yielded is IEnumerator inner)
                {
                    s_nested++;
                    stack.Push(inner);
                    continue;
                }
                if (yielded is YieldInstruction || yielded is CustomYieldInstruction || yielded is AsyncOperation)
                {
                    s_toUnity++;
                    s_frames++;
                    yield return yielded;
                    sliceEnd = Time.realtimeSinceStartup + s_budget;
                    continue;
                }
                // null (or anything else): "next frame" in Unity; given back only once the slice is used.
                if (Time.realtimeSinceStartup >= sliceEnd)
                {
                    s_frames++;
                    yield return null;
                    sliceEnd = Time.realtimeSinceStartup + s_budget;
                }
            }
        }
    }
}
