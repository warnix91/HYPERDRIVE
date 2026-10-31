using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Physics
{
    /// <summary>
    /// Stock FlightIntegrator.UpdateAerodynamics runs for every part on every physics step, including parts that are
    /// practically motionless (vessel on the launch pad, landed bases, parked rovers): measured on a 310-part vessel
    /// sitting on the pad, it was the single largest cost of the flight integrator.
    /// Below <c>speedThreshold</c> (0.5 m/s: dynamic pressure ~0.15 Pa at sea level, negligible against contact and
    /// gravity forces) a part's aerodynamics are refreshed only one physics step in <c>interval</c>, phase-shifted per
    /// part so the work is spread evenly. Skipped steps leave the last computed state in place (rigidbody angular drag,
    /// areas, drag cube state), so nothing is reset to zero. Parts touching water and EVA kerbals always run stock.
    /// Above the threshold every part runs stock on every step.
    /// </summary>
    internal sealed class AeroIdleThrottle : Subsystem
    {
        private static float s_speedSqr = 0.25f;
        private static int s_interval = 5;
        private static long s_calls, s_skipped;

        private float _nextReport;
        private long _reportedCalls, _reportedSkipped;

        public override string Name => "AeroIdleThrottle";

        public override string[] IncompatibleAssemblies => new[] { "FerramAerospaceResearch", "ksp_plugin_adapter" };
        public override string[] KspcfEquivalents => new[] { "FlightIntegratorPerf" };
        public override bool YieldOnSharedTargets => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            float speed = Mathf.Clamp(settings.GetFloat("speedThreshold", 0.5f), 0.01f, 5f);
            s_speedSqr = speed * speed;
            s_interval = Mathf.Clamp(settings.GetInt("interval", 5), 2, 50);
        }

        protected internal override void OnEnable()
        {
            Patch(HarmonyLib.AccessTools.Method(typeof(FlightIntegrator), "UpdateAerodynamics"), prefix: nameof(UpdateAerodynamicsPrefix));
            Log.Info("AeroIdleThrottle: parts slower than " + Mathf.Sqrt(s_speedSqr) + " m/s refresh aerodynamics every " + s_interval + " physics steps");
        }

        protected internal override void OnUpdate()
        {
            float now = Time.unscaledTime;
            if (now < _nextReport)
                return;
            _nextReport = now + 30f;
            long calls = s_calls - _reportedCalls, skipped = s_skipped - _reportedSkipped;
            if (calls > 0 && HighLogic.LoadedScene == GameScenes.FLIGHT)
                Log.Info("AeroIdleThrottle: skipped " + skipped + " of " + calls + " part aerodynamics updates (" + (100.0 * skipped / calls).ToString("F0") + "%) since last report");
            _reportedCalls = s_calls;
            _reportedSkipped = s_skipped;
        }

        private static bool UpdateAerodynamicsPrefix(Part part)
        {
            s_calls++;
            if (part == null || part.WaterContact || part.submergedPortion > 0.0 || part.isKerbalEVA())
                return true;

            Rigidbody rb = part.Rigidbody;
            if ((object)rb == null)
                return true;

            Vector3 airVelocity = rb.velocity + (Vector3)Krakensbane.GetFrameVelocity();
            if (airVelocity.sqrMagnitude > s_speedSqr)
                return true;

            // Physics step index; each part gets its own phase so the refreshes are spread across steps.
            int step = (int)(Time.fixedTime / Time.fixedDeltaTime + 0.5f);
            int phase = part.GetInstanceID() & 0x7fffffff;
            if ((step + phase) % s_interval == 0)
                return true;

            s_skipped++;
            return false;
        }
    }
}
