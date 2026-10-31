using System;
using System.Collections.Generic;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Physics
{
    /// <summary>
    /// Stock PartBuoyancy.FixedUpdate runs for every part on every physics step, even far from any water: each call reads
    /// several transforms and computes the part's depth in double precision before finding there is nothing to do.
    /// Measured on a 310-part vessel on the launch pad: 348 calls and ~0.8 ms per frame for zero effect.
    /// Once per frame this subsystem marks the loaded vessels that certainly take the stock "dry" branch: terrain under
    /// the vessel at least 20 m + margin above sea level, or lowest possible part altitude (vessel altitude minus extent
    /// and a vertical-speed margin) above the water level plus the stock buoyancy range plus a margin. On those vessels a part's update is skipped only if its buoyancy state is already
    /// the stock "dry" state (not splashed, no water contact, nothing submerged). Near water everything runs as stock.
    /// </summary>
    internal sealed class BuoyancyGuard : Subsystem
    {
        private const int MaxVessels = 64;
        private const float ExtentRefreshSeconds = 1f;
        private const double StockTerrainLimit = 20.0;   // PartBuoyancy: expensive path only if vessel.terrainAltitude < 20

        // Read by the prefix on every call: plain statics, allocation-free.
        private static readonly Vessel[] s_dry = new Vessel[MaxVessels];
        private static int s_dryCount;
        private static long s_calls, s_skipped;

        private double _marginMeters = 10;
        private readonly Dictionary<Vessel, float> _extent = new Dictionary<Vessel, float>();
        private float _nextExtentRefresh;
        private long _reportedCalls, _reportedSkipped;
        private float _nextReport;
        private const float ReportSeconds = 30f;

        public override string Name => "BuoyancyGuard";

        public override string[] IncompatibleAssemblies => new[] { "ksp_plugin_adapter" };
        public override string[] KspcfEquivalents => new[] { "OnDemandPartBuoyancy" };
        public override bool YieldOnSharedTargets => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _marginMeters = Mathf.Clamp(settings.GetFloat("marginMeters", (float)_marginMeters), 2f, 500f);
        }

        protected internal override void OnEnable()
        {
            Patch(HarmonyLib.AccessTools.Method(typeof(PartBuoyancy), "FixedUpdate"), prefix: nameof(FixedUpdatePrefix));
            Log.Info("BuoyancyGuard: buoyancy skipped on vessels that are certainly far from water (margin " + _marginMeters + " m)");
        }

        protected internal override void OnDisable()
        {
            s_dryCount = 0;
            Array.Clear(s_dry, 0, s_dry.Length);
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            ReportAndReset();
            _extent.Clear();
            s_dryCount = 0;
            Array.Clear(s_dry, 0, s_dry.Length);
        }

        protected internal override void OnUpdate()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready)
            {
                s_dryCount = 0;
                return;
            }

            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            float now = Time.unscaledTime;
            if (now >= _nextReport)
            {
                _nextReport = now + ReportSeconds;
                ReportAndReset();
            }
            bool refreshExtents = now >= _nextExtentRefresh;
            if (refreshExtents)
                _nextExtentRefresh = now + ExtentRefreshSeconds;

            int count = 0;
            for (int i = 0; i < loaded.Count && count < MaxVessels; i++)
            {
                Vessel v = loaded[i];
                if (v == null || !v.loaded || v.packed || v.mainBody == null)
                    continue;

                if (refreshExtents || !_extent.TryGetValue(v, out float extent))
                    extent = _extent[v] = Extent(v);

                if (v.isEVA)
                    continue;

                // Stock runs the expensive path only if the part is within BuoyancyRange (1000 m in Physics.cfg) of the
                // water level AND the terrain under the vessel is below 20 m. Either condition failing means the stock
                // "dry" branch, so it is mirrored here with margins for the one-frame delay of this check.
                double lowest = v.altitude - extent - Math.Abs(v.verticalSpeed) * 0.25;
                double water = Math.Max(0.0, v.waterOffset) + PhysicsGlobals.BuoyancyRange + _marginMeters;
                bool highTerrain = v.terrainAltitude >= StockTerrainLimit + _marginMeters;
                if (!v.mainBody.ocean || lowest > water || highTerrain)
                    s_dry[count++] = v;
            }
            for (int i = count; i < s_dryCount; i++)
                s_dry[i] = null;
            s_dryCount = count;
        }

        // Farthest part from the centre of mass, plus a part-size margin.
        private static float Extent(Vessel v)
        {
            Vector3 com = v.CoM;
            float max = 0f;
            List<Part> parts = v.parts;
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                if (p == null)
                    continue;
                float d = (p.transform.position - com).sqrMagnitude;
                if (d > max) max = d;
            }
            return Mathf.Sqrt(max) + 5f;
        }

        private void ReportAndReset()
        {
            long calls = s_calls - _reportedCalls, skipped = s_skipped - _reportedSkipped;
            if (calls > 0)
                Log.Info("BuoyancyGuard: skipped " + skipped + " of " + calls + " part buoyancy updates (" + (100.0 * skipped / calls).ToString("F0") + "%) since last report");
            _reportedCalls = s_calls;
            _reportedSkipped = s_skipped;
        }

        private static bool FixedUpdatePrefix(PartBuoyancy __instance, Part ___part)
        {
            s_calls++;
            int count = s_dryCount;
            if (count == 0 || __instance.splashed || ___part == null || ___part.WaterContact || ___part.submergedPortion != 0.0)
                return true;

            Vessel vessel = ___part.vessel;
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(s_dry[i], vessel))
                {
                    s_skipped++;
                    return false;
                }
            }
            return true;
        }
    }
}
