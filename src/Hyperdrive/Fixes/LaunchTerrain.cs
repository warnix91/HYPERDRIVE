using System;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Loading;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Fixes
{
    /// <summary>
    /// Stock launch of a new vessel (FlightDriver.setStartupNewVessel): the launch site's sphere is started with
    /// PSystemSetup.SetPQSActive(sphere) (PQS.ForceStart: the whole terrain round the site built at once, every other sphere
    /// reset), the vessel is put on the ground, then SetPQSActive() resets every sphere again: the terrain just built is
    /// thrown away and built a second time under the vessel. Loading a saved flight (and reverting) only does the first
    /// step. Here the second call is left out when the first one ran during the same launch, as for a saved flight: the
    /// other spheres were already reset by the first call. KSPCommunityFixes does the same (PQSOnlyStartOnce). Idea and
    /// measurements credited to Smooth Running (Ishiaki, CC BY 4.0). With fingerprint = true, a terrain fingerprint is logged 6 s into
    /// each flight, for comparison with skipSecondBuild = false.
    /// </summary>
    internal sealed class LaunchTerrain : Subsystem
    {
        private static bool s_active, s_skip = true, s_fingerprint, s_launching, s_started;
        private static long s_skipped;
        private float _fingerprintAt = -1f;

        public override string Name => "LaunchTerrain";

        public override string[] IncompatibleAssemblies => new[] { "SmoothRunning" };
        public override string[] KspcfEquivalents => new[] { "PQSOnlyStartOnce" };
        public override bool YieldOnSharedTargets => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_skip = settings.GetBool("skipSecondBuild", s_skip);
            s_fingerprint = settings.GetBool("fingerprint", s_fingerprint);
        }

        protected internal override void OnEnable()
        {
            MethodInfo setup = AccessTools.Method(typeof(FlightDriver), "setStartupNewVessel");
            MethodInfo one = AccessTools.Method(typeof(PSystemSetup), nameof(PSystemSetup.SetPQSActive), new[] { typeof(PQS) });
            MethodInfo every = AccessTools.Method(typeof(PSystemSetup), nameof(PSystemSetup.SetPQSActive), Type.EmptyTypes);
            if (setup == null || one == null || every == null)
                throw new SubsystemUnavailableException("FlightDriver.setStartupNewVessel / PSystemSetup.SetPQSActive not found (game version mismatch?)");
            Patch(setup, prefix: nameof(LaunchPrefix), finalizer: nameof(LaunchFinalizer));
            Patch(one, postfix: nameof(OneSpherePostfix));
            Patch(every, prefix: nameof(EverySpherePrefix));
            s_active = true;
            Log.Info("LaunchTerrain: " + (s_skip ? "a new launch builds its terrain once" : "terrain built twice at launch, as stock (skipSecondBuild = false)"));
        }

        protected internal override void OnDisable()
        {
            s_active = false;
            s_launching = s_started = false;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            _fingerprintAt = scene == GameScenes.FLIGHT && s_fingerprint ? Time.unscaledTime + 6f : -1f;
        }

        protected internal override void OnUpdate()
        {
            if (_fingerprintAt < 0f || Time.unscaledTime < _fingerprintAt)
                return;
            _fingerprintAt = -1f;
            PQS sphere = FlightGlobals.currentMainBody != null ? FlightGlobals.currentMainBody.pqsController : null;
            if (sphere != null)
                Log.Info("LaunchTerrain: fingerprint " + sphere.name + " 6 s into the flight: " + LaunchSitePacing.Fingerprint(sphere)
                         + " (" + s_skipped + " second builds left out so far)");
        }

        private static void LaunchPrefix()
        {
            s_launching = s_active;
            s_started = false;
        }

        private static Exception LaunchFinalizer(Exception __exception)
        {
            s_launching = s_started = false;
            return __exception;
        }

        private static void OneSpherePostfix(PQS pqs)
        {
            if (s_launching && pqs != null)
                s_started = true;
        }

        private static bool EverySpherePrefix()
        {
            if (!s_skip || !s_launching || !s_started)
                return true;
            s_skipped++;
            s_started = false;
            return false;
        }
    }
}
