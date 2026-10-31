using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;

namespace Hyperdrive.Fixes
{
    /// <summary>
    /// Stock leak: a terrain sphere starts its update coroutine with StartCoroutine(UpdateSphere()) at every start (every
    /// load, scene change, launch and revert) and stops it with StopCoroutine("UpdateSphere"), by name, which only stops
    /// coroutines started by name: none is. The old one would end by itself (it runs while the sphere is alive), but the
    /// sphere is started again before its next step, so each start adds one more update of the same sphere, every frame,
    /// until the game is closed (measured by Smooth Running: Kerbin updated 75 times a frame after an evening, 6 ms of
    /// every frame). ResetAndWaitCoroutine leaks the same way.
    ///
    /// Here only the most recently started coroutine of each kind of each sphere goes on, while it runs; an older one ends at
    /// its next step. "Started" is the coroutine's first step, which Unity runs inside StartCoroutine: the method that
    /// creates the coroutine object is not patched, because it is small enough to be inlined into its caller (it was, in
    /// StartSphere, and a first version ended the wrong coroutine). If the newest has ended, the others are left alone, as
    /// stock. A freshly started game has one coroutine per sphere: that is what is kept. endDuplicates = false only counts
    /// them. Idea and measurements credited to Smooth Running (Ishiaki, CC BY 4.0).
    /// </summary>
    internal sealed class TerrainCoroutines : Subsystem
    {
        private sealed class Newest
        {
            public int Sequence;
            public object Coroutine;
            public bool Finished;
        }

        private static readonly string[] Kinds = { "UpdateSphere", "ResetAndWaitCoroutine" };
        private static readonly Dictionary<int, Newest>[] s_newest = { new Dictionary<int, Newest>(), new Dictionary<int, Newest>() };
        private static readonly Dictionary<object, int> s_sequence = new Dictionary<object, int>();
        private static readonly Dictionary<Type, int> s_kindOf = new Dictionary<Type, int>();
        private static readonly AccessTools.FieldRef<object, PQS>[] s_owner = new AccessTools.FieldRef<object, PQS>[2];
        private static bool s_active, s_endDuplicates = true;
        private static int s_nextSequence;
        private static long s_ended;
        private static int s_updateSteps, s_maxFrameSteps, s_frames;
        // Main-thread time of the terrain update steps (UpdateSphere), per scene: what the duplicates cost.
        private static long s_stepStart, s_stepTicks, s_sceneTicks;

        public override string Name => "TerrainCoroutines";

        public override string[] IncompatibleAssemblies => new[] { "SmoothRunning" };
        public override string[] KspcfEquivalents => new[] { "PQSCoroutineLeak" };
        public override bool YieldOnSharedTargets => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_endDuplicates = settings.GetBool("endDuplicates", s_endDuplicates);
        }

        protected internal override void OnEnable()
        {
            for (int k = 0; k < Kinds.Length; k++)
            {
                MethodInfo start = AccessTools.Method(typeof(PQS), Kinds[k]);
                MethodInfo moveNext = start != null ? AccessTools.EnumeratorMoveNext(start) : null;
                FieldInfo self = moveNext?.DeclaringType.GetField("<>4__this", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (moveNext == null || self == null)
                    throw new SubsystemUnavailableException("PQS." + Kinds[k] + " state machine not found (game version mismatch?)");
                s_kindOf[moveNext.DeclaringType] = k;
                s_owner[k] = AccessTools.FieldRefAccess<object, PQS>(self);
                Patch(moveNext, prefix: nameof(MoveNextPrefix), postfix: nameof(MoveNextPostfix));
            }
            s_active = true;
            Log.Info("TerrainCoroutines: " + (s_endDuplicates ? "one update coroutine per terrain sphere" : "duplicate terrain coroutines counted, not ended (endDuplicates = false)"));
        }

        protected internal override void OnDisable()
        {
            s_active = false;
        }

        protected internal override void OnUpdate()
        {
            // Coroutines resumed after the previous Update: one frame's worth of UpdateSphere steps.
            if (s_updateSteps > s_maxFrameSteps)
                s_maxFrameSteps = s_updateSteps;
            s_updateSteps = 0;
            s_sceneTicks += s_stepTicks;
            s_stepTicks = 0;
            s_frames++;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            double ms = s_frames > 0 ? s_sceneTicks * 1000.0 / Stopwatch.Frequency / s_frames : 0.0;
            Log.Info("TerrainCoroutines: up to " + s_maxFrameSteps + " terrain updates in a frame since the last scene, "
                     + ms.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " ms a frame on average"
                     + (s_endDuplicates ? ", " + s_ended + " duplicate coroutines ended so far" : ""));
            s_maxFrameSteps = 0;
            s_sceneTicks = 0;
            s_frames = 0;
        }

        private static bool MoveNextPrefix(object __instance, ref bool __result)
        {
            if (!s_active || !s_kindOf.TryGetValue(__instance.GetType(), out int kind))
                return true;
            PQS sphere = s_owner[kind](__instance);
            if (ReferenceEquals(sphere, null))
                return true;
            if (kind == 0)
            {
                s_updateSteps++;
                s_stepStart = Stopwatch.GetTimestamp();
            }
            int id = sphere.GetInstanceID();
            if (!s_newest[kind].TryGetValue(id, out Newest newest))
            {
                newest = new Newest();
                s_newest[kind][id] = newest;
            }
            if (!s_sequence.TryGetValue(__instance, out int sequence))
            {
                // First step: this coroutine was just started, it is the newest of its sphere.
                sequence = ++s_nextSequence;
                s_sequence[__instance] = sequence;
                newest.Sequence = sequence;
                newest.Coroutine = __instance;
                newest.Finished = false;
                return true;
            }
            if (sequence >= newest.Sequence || newest.Finished || !s_endDuplicates)
                return true;
            s_sequence.Remove(__instance);
            s_ended++;
            s_stepStart = 0;
            __result = false;
            return false;
        }

        private static void MoveNextPostfix(object __instance, bool __result)
        {
            if (s_stepStart != 0)
            {
                s_stepTicks += Stopwatch.GetTimestamp() - s_stepStart;
                s_stepStart = 0;
            }
            if (__result || !s_active || !s_kindOf.TryGetValue(__instance.GetType(), out int kind))
                return;
            s_sequence.Remove(__instance);
            PQS sphere = s_owner[kind](__instance);
            if (!ReferenceEquals(sphere, null) && s_newest[kind].TryGetValue(sphere.GetInstanceID(), out Newest newest)
                && ReferenceEquals(newest.Coroutine, __instance))
            {
                newest.Finished = true;
                newest.Coroutine = null;
            }
        }
    }
}
