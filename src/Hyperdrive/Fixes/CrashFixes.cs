using System;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;

namespace Hyperdrive.Fixes
{
    /// <summary>
    /// Crash freezes. When a part dies (crash, explosion), Part.Die decouples each of its physically significant children:
    /// every one becomes a vessel of its own, and every decouple fires onVesselPartCountChanged on the vessel losing it,
    /// which rebuilds that vessel's resource sets (PartSet.BuildPartSets over all its parts). A part holding a big vessel
    /// together rebuilds it once per detached piece: measured with a 310-part craft dropped from 200 m, 155 pieces and
    /// 622 rebuilds (534 ms) in a 1.5 s frame. KSP already avoids this while staging (StageManager.ActivateStage turns the
    /// vessel's updateResourcesOnEvent off, activates every part of the stage, turns it back on and calls
    /// UpdateResourceSetsIfDirty). The same is done here around the outermost Part.Die: the dying part's vessel rebuilds
    /// its sets once, at the end of the cascade, from its final parts, before any physics step can use them.
    /// </summary>
    internal sealed class CrashFixes : Subsystem
    {
        private static int s_depth;
        private static Vessel s_vessel;
        private static bool s_deferred;
        private static long s_cascades, s_deferredRebuilds;

        public override string Name => "CrashFixes";

        public override bool YieldOnSharedTargets => true;

        protected internal override void OnEnable()
        {
            Patch(AccessTools.Method(typeof(Part), nameof(Part.Die), Type.EmptyTypes), prefix: nameof(DiePrefix), finalizer: nameof(DieFinalizer));
            Log.Info("CrashFixes: a dying part's vessel rebuilds its resource sets once per cascade, as when staging");
        }

        protected internal override void OnDisable()
        {
            s_depth = 0;
            Restore();
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (s_cascades > 0)
                Log.Debug("CrashFixes: " + s_cascades + " part death cascades, " + s_deferredRebuilds + " resource set rebuilds deferred to their end");
        }

        private static void DiePrefix(Part __instance)
        {
            if (s_depth++ > 0)
                return;
            Vessel v = __instance != null ? __instance.vessel : null;
            if (v == null || !v.updateResourcesOnEvent)
                return;
            s_vessel = v;
            s_deferred = true;
            v.updateResourcesOnEvent = false;
        }

        private static Exception DieFinalizer(Exception __exception)
        {
            if (--s_depth <= 0)
            {
                s_depth = 0;
                Restore();
            }
            return __exception;
        }

        private static void Restore()
        {
            if (!s_deferred)
                return;
            Vessel v = s_vessel;
            s_deferred = false;
            s_vessel = null;
            if (v == null)
                return;
            s_cascades++;
            if (v.resourcesDirty)
                s_deferredRebuilds++;
            v.updateResourcesOnEvent = true;
            v.UpdateResourceSetsIfDirty();
        }
    }
}
