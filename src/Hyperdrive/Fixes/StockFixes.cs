using System;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Fixes
{
    /// <summary>
    /// Small fixes for stock exceptions met during the benchmarks.
    /// FXGroup.VolumeFade: the coroutine that fades an engine's sound keeps reading its AudioSource after the part was
    /// destroyed (engine exploded, debris crashed), throwing a NullReferenceException every time. A finalizer on the
    /// coroutine's MoveNext ends the coroutine quietly when, and only when, that AudioSource no longer exists; any
    /// other exception is left untouched.
    /// SpaceCenterBuildingCollider.OnMouseOver/OnMouseExit: a KSC building collider whose building reference is null
    /// throws a NullReferenceException on every frame the mouse is over it (seen in flight near the space centre, about
    /// 80 a second). The call is skipped in exactly that case, where stock throws before doing anything.
    /// </summary>
    internal sealed class StockFixes : Subsystem
    {
        private static FieldInfo s_audioSourceField;
        private static long s_swallowed;
        private static long s_hoverSkipped;

        public override string Name => "StockFixes";

        public override bool YieldOnSharedTargets => true;

        protected internal override void OnEnable()
        {
            Type iterator = AccessTools.FirstInner(typeof(FXGroup), t => t.Name.Contains("VolumeFade"));
            MethodInfo moveNext = iterator != null ? AccessTools.Method(iterator, "MoveNext") : null;
            s_audioSourceField = iterator != null ? AccessTools.Field(iterator, "audioSource") : null;
            if (moveNext == null || s_audioSourceField == null || s_audioSourceField.FieldType != typeof(AudioSource))
                throw new SubsystemUnavailableException("FXGroup.VolumeFade coroutine not found (game version mismatch?)");
            Patch(moveNext, finalizer: nameof(VolumeFadeFinalizer));
            Log.Info("StockFixes: FXGroup.VolumeFade stops quietly when its sound source was destroyed");

            MethodInfo over = AccessTools.Method(typeof(SpaceCenterBuildingCollider), "OnMouseOver");
            MethodInfo exit = AccessTools.Method(typeof(SpaceCenterBuildingCollider), "OnMouseExit");
            if (over != null && exit != null)
            {
                Patch(over, prefix: nameof(ColliderHoverPrefix));
                Patch(exit, prefix: nameof(ColliderHoverPrefix));
                Log.Info("StockFixes: KSC building colliders without a building no longer throw on mouse hover");
            }
            else
                Log.Warn("StockFixes: SpaceCenterBuildingCollider mouse methods not found, hover fix skipped");
        }

        // Stock calls building.ColliderHover(), which only stores a bool: it throws when, and only when, the reference is null.
        private static bool ColliderHoverPrefix(SpaceCenterBuildingCollider __instance)
        {
            if ((object)__instance.building != null)
                return true;
            if (s_hoverSkipped++ == 0)
                Log.Info("StockFixes: skipped the hover of a KSC building collider without a building (stock NullReferenceException avoided)");
            return false;
        }

        private static Exception VolumeFadeFinalizer(object __instance, Exception __exception, ref bool __result)
        {
            if (__exception == null)
                return null;
            if (!(__exception is NullReferenceException) && !(__exception is MissingReferenceException))
                return __exception;
            AudioSource source = s_audioSourceField.GetValue(__instance) as AudioSource;
            if (source != null)
                return __exception;   // the source still exists: not the known stock bug
            __result = false;         // end the coroutine
            if (s_swallowed++ == 0)
                Log.Info("StockFixes: ended an engine sound fade whose part was destroyed (stock NullReferenceException avoided)");
            return null;
        }
    }
}
