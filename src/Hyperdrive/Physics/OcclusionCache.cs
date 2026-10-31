using System.Collections.Generic;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Physics
{
    /// <summary>
    /// Stock FlightIntegrator recomputes, every few frames, how much each part is shaded from the sun and from the
    /// planet by the other parts (UpdateOcclusionSolar / UpdateOcclusionBody): every part is projected along the
    /// direction and tested against every visible part before it, O(n²). Measured on a 310-part vessel: ~2.7 ms per call
    /// for each of the two, i.e. ~1.8 ms per frame on average and a spike every third frame.
    /// The result only depends on the vessel's shape and on the direction seen from the vessel. This subsystem keeps the
    /// previous result while the direction, expressed in the root part's frame, has moved less than <c>maxAngleDegrees</c>
    /// and the part list is unchanged, and recomputes anyway after <c>maxAgeSeconds</c> (flex, drag cube changes).
    /// Rebuilds of the thermal graph (staging, docking, part added or lost) always run stock.
    /// With ModularFlightIntegrator (FAR, Kopernicus) this stays on: MFI runs the stock UpdateOcclusion unless a mod took
    /// that step (see MfiHooks; logged on the first flight). Sun and body areas a mod computes inside it (FAR's GetSunArea /
    /// GetBodyArea overrides) are part of the reused result, under the same direction and age limits; a mod lighting a
    /// vessel from several stars changes the direction, which recomputes.
    /// </summary>
    internal sealed class OcclusionCache : Subsystem
    {
        private sealed class Shading
        {
            public FlightIntegrator Owner;
            public Vector3 SunDir, BodyDir;
            public float SunTime = float.MinValue, BodyTime = float.MinValue;
            public int SunParts = -1, BodyParts = -1;
        }

        private const int PruneThreshold = 32;

        private static float s_minDot = Mathf.Cos(0.5f * Mathf.Deg2Rad);
        private static float s_maxAge = 1f;
        private static readonly Dictionary<int, Shading> s_states = new Dictionary<int, Shading>();
        private static readonly List<int> s_dead = new List<int>();
        private static long s_calls, s_skipped;

        private float _nextReport;
        private long _reportedCalls, _reportedSkipped;

        public override string Name => "OcclusionCache";

        public override string[] IncompatibleAssemblies => new[] { "ksp_plugin_adapter" };
        private bool _mfiReported;
        public override string[] KspcfEquivalents => new[] { "FlightIntegratorPerf" };
        public override bool YieldOnSharedTargets => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_minDot = Mathf.Cos(Mathf.Clamp(settings.GetFloat("maxAngleDegrees", 0.5f), 0.05f, 5f) * Mathf.Deg2Rad);
            s_maxAge = Mathf.Clamp(settings.GetFloat("maxAgeSeconds", 1f), 0.1f, 10f);
        }

        protected internal override void OnEnable()
        {
            Patch(HarmonyLib.AccessTools.Method(typeof(FlightIntegrator), "UpdateOcclusionSolar"), prefix: nameof(SolarPrefix));
            Patch(HarmonyLib.AccessTools.Method(typeof(FlightIntegrator), "UpdateOcclusionBody"), prefix: nameof(BodyPrefix));
            Log.Info("OcclusionCache: sun and planet shading reused while the direction moves less than "
                     + (Mathf.Acos(s_minDot) * Mathf.Rad2Deg).ToString("F2") + " deg, refreshed at least every " + s_maxAge + " s");
        }

        protected internal override void OnDisable() => s_states.Clear();

        protected internal override void OnSceneReady(GameScenes scene)
        {
            s_states.Clear();
            if (scene == GameScenes.FLIGHT && !_mfiReported && MfiHooks.Installed)
            {
                _mfiReported = true;
                MfiHooks.Report(Name, "updateOcclusionOverride", "UpdateOcclusion");
            }
        }

        protected internal override void OnUpdate()
        {
            float now = Time.unscaledTime;
            if (now < _nextReport)
                return;
            _nextReport = now + 30f;
            long calls = s_calls - _reportedCalls, skipped = s_skipped - _reportedSkipped;
            if (calls > 0 && HighLogic.LoadedScene == GameScenes.FLIGHT)
                Log.Info("OcclusionCache: reused " + skipped + " of " + calls + " sun/planet occlusion passes (" + (100.0 * skipped / calls).ToString("F0") + "%) since last report");
            _reportedCalls = s_calls;
            _reportedSkipped = s_skipped;
        }

        private static bool SolarPrefix(FlightIntegrator __instance, bool ___needOcclusion, int ___partThermalDataCount)
        {
            s_calls++;
            if (___needOcclusion || ___partThermalDataCount != __instance.partThermalDataList.Count)
                return true;
            Shading s = GetState(__instance);
            Vector3 dir = __instance.transform.InverseTransformDirection(__instance.sunVector);
            float now = Time.time;
            if (s.SunParts == ___partThermalDataCount && now - s.SunTime < s_maxAge && Vector3.Dot(dir, s.SunDir) >= s_minDot)
            {
                s_skipped++;
                return false;
            }
            s.SunDir = dir;
            s.SunTime = now;
            s.SunParts = ___partThermalDataCount;
            return true;
        }

        private static bool BodyPrefix(FlightIntegrator __instance, bool ___needOcclusion, int ___partThermalDataCount)
        {
            s_calls++;
            Vessel vessel = __instance.Vessel;
            if (___needOcclusion || (object)vessel == null || ___partThermalDataCount != __instance.partThermalDataList.Count)
                return true;
            Shading s = GetState(__instance);
            Vector3 dir = __instance.transform.InverseTransformDirection(-vessel.upAxis);
            float now = Time.time;
            if (s.BodyParts == ___partThermalDataCount && now - s.BodyTime < s_maxAge && Vector3.Dot(dir, s.BodyDir) >= s_minDot)
            {
                s_skipped++;
                return false;
            }
            s.BodyDir = dir;
            s.BodyTime = now;
            s.BodyParts = ___partThermalDataCount;
            return true;
        }

        // Allocates only when a new vessel is seen; destroyed integrators are pruned at that moment.
        private static Shading GetState(FlightIntegrator fi)
        {
            int id = fi.GetInstanceID();
            if (s_states.TryGetValue(id, out Shading s))
                return s;
            if (s_states.Count >= PruneThreshold)
                Prune();
            s = new Shading { Owner = fi };
            s_states[id] = s;
            return s;
        }

        private static void Prune()
        {
            s_dead.Clear();
            foreach (KeyValuePair<int, Shading> kv in s_states)
            {
                if (kv.Value.Owner == null)
                    s_dead.Add(kv.Key);
            }
            for (int i = 0; i < s_dead.Count; i++)
                s_states.Remove(s_dead[i]);
        }
    }
}
