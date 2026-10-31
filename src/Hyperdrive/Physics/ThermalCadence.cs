using System.Collections.Generic;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Physics
{
    /// <summary>
    /// Stock FlightIntegrator.UpdateThermodynamics runs the whole thermal model (skin properties, conduction, convection
    /// and radiation precalculation for every part, then an integration pass) on every physics step: ~4.3 ms per frame
    /// measured on a 310-part vessel standing on the launch pad, where nothing changes temperature.
    /// The stock model is built for longer steps: heat added by modules is accumulated as energy
    /// (Part.AddThermalFlux multiplies by the step length), integration uses the elapsed time since the last pass
    /// (thermalIntegrationMinStep), conduction is clamped against overshoot, and one pass is allowed up to
    /// thermalIntegrationMaxTimeOnePass (0.07 s). When a vessel is thermally calm this subsystem lets the stock model
    /// integrate once every <c>interval</c> physics steps (0.06 s at 3) instead of every step, phase-shifted per vessel.
    /// Calm means: no time warp, subsonic or in vacuum (no reentry heating), and every part below
    /// <c>maxTemperatureRatio</c> of its core and skin limits. Anything else runs stock on every step.
    /// With ModularFlightIntegrator (FAR, Kopernicus) this stays on: MFI runs the stock UpdateThermodynamics unless a mod
    /// took that step (see MfiHooks; logged on the first flight). Steps added before or after it (FAR recomputes each part's
    /// exposed and radiative areas before it) still run on every physics step; a deferred pass then integrates with the
    /// latest of them, as stock does below thermalIntegrationMinStep.
    /// </summary>
    internal sealed class ThermalCadence : Subsystem
    {
        private static int s_interval = 3;
        private static double s_machLimit = 0.8;
        private static double s_maxRatio = 0.5;
        private static long s_calls, s_skipped;

        private float _nextReport;
        private long _reportedCalls, _reportedSkipped;

        public override string Name => "ThermalCadence";

        public override string[] IncompatibleAssemblies => new[] { "ksp_plugin_adapter" };
        public override bool YieldOnSharedTargets => true;
        private bool _mfiReported;

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene != GameScenes.FLIGHT || _mfiReported || !MfiHooks.Installed)
                return;
            _mfiReported = true;
            MfiHooks.Report(Name, "updateThermodynamicsOverride", "UpdateThermodynamics");
            string pre = MfiHooks.Owner("updateThermodynamicsPre"), post = MfiHooks.Owner("updateThermodynamicsPost");
            if (pre != null || post != null)
                Log.Info(Name + ": steps around it run on every physics step as before (before: " + (pre ?? "none") + ", after: " + (post ?? "none") + ")");
        }

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_interval = Mathf.Clamp(settings.GetInt("interval", 3), 2, 3);
            s_machLimit = Mathf.Clamp(settings.GetFloat("machThreshold", 0.8f), 0f, 2f);
            s_maxRatio = Mathf.Clamp(settings.GetFloat("maxTemperatureRatio", 0.5f), 0.05f, 0.9f);
        }

        protected internal override void OnEnable()
        {
            Patch(HarmonyLib.AccessTools.Method(typeof(FlightIntegrator), "UpdateThermodynamics"), prefix: nameof(UpdateThermodynamicsPrefix));
            Log.Info("ThermalCadence: thermally calm vessels integrate heat every " + s_interval + " physics steps (below Mach "
                     + s_machLimit + ", parts below " + (s_maxRatio * 100).ToString("F0") + "% of their limits)");
        }

        protected internal override void OnUpdate()
        {
            float now = Time.unscaledTime;
            if (now < _nextReport)
                return;
            _nextReport = now + 30f;
            long calls = s_calls - _reportedCalls, skipped = s_skipped - _reportedSkipped;
            if (calls > 0 && HighLogic.LoadedScene == GameScenes.FLIGHT)
                Log.Info("ThermalCadence: deferred " + skipped + " of " + calls + " vessel thermal updates (" + (100.0 * skipped / calls).ToString("F0") + "%) since last report");
            _reportedCalls = s_calls;
            _reportedSkipped = s_skipped;
        }

        private static bool UpdateThermodynamicsPrefix(FlightIntegrator __instance, ref double ___fTimeSinceThermo, double ___fDeltaTime, bool ___isAnalytical)
        {
            s_calls++;
            if (TimeWarp.CurrentRate != 1f || ___isAnalytical || __instance.mach > s_machLimit)
                return true;

            // Never let the accumulated time exceed what this interval allows (stock one-pass bound is 0.07 s).
            double fixedDt = TimeWarp.fixedDeltaTime;
            if (___fTimeSinceThermo + ___fDeltaTime > fixedDt * (s_interval - 0.5))
                return true;

            Vessel vessel = __instance.Vessel;
            if ((object)vessel == null)
                return true;

            // The first update after spawn or load sets the starting temperatures (stock analytic branch, lastUT < 0
            // or a long gap): never shift it, so the vessel starts exactly as in stock.
            double sinceLast = Planetarium.GetUniversalTime() - vessel.lastUT;
            if (vessel.lastUT < 0.0 || sinceLast < 0.0 || sinceLast > fixedDt * (s_interval + 1))
                return true;

            // Phase per vessel so several loaded vessels do not integrate on the same step.
            int step = (int)(Time.fixedTime / Time.fixedDeltaTime + 0.5f);
            int phase = vessel.GetInstanceID() & 0x7fffffff;
            if ((step + phase) % s_interval == 0 || !BelowLimits(vessel.parts))
                return true;

            // Same bookkeeping as the stock early-out below thermalIntegrationMinStep: time accumulates and the next
            // pass integrates all of it, together with the module heat accumulated as energy in the meantime.
            ___fTimeSinceThermo += ___fDeltaTime;
            s_skipped++;
            return false;
        }

        private static bool BelowLimits(List<Part> parts)
        {
            double ratio = s_maxRatio;
            for (int i = parts.Count; i-- > 0;)
            {
                Part p = parts[i];
                if ((object)p == null)
                    continue;
                if (p.temperature > p.maxTemp * ratio || p.skinTemperature > p.skinMaxTemp * ratio)
                    return false;
            }
            return true;
        }
    }
}
