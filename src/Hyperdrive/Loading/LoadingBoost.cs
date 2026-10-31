using System;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// Two first-load costs that are not loading work:
    /// - FlightGlobals.fetch: with no FlightGlobals in the scene (the whole first load), every call runs
    ///   Object.FindObjectOfType, a search of every object; part and module loading call it thousands of times (about 7 s on
    ///   a big install, measured by KSPCF). Here a search that found nothing is not repeated in the same frame: same answer
    ///   (nothing can appear in the middle of a frame's scripts), at most one search per frame. Once found, as stock.
    /// - Unity's loading settings, for the first load only, restored at the main menu: asynchronous loads (asset bundles,
    ///   scenes) get High background priority (50 ms of each frame instead of 10) and the texture upload pipeline a
    ///   larger time slice and buffer; vsync and the frame rate cap are lifted, so loaders that do one item per frame are no
    ///   longer held to the display's rate (the loading animation keeps its own pacing).
    /// With KSPCF's MinorPerfTweaks enabled (it answers fetch its own way) only the loading settings are applied: KSPCF
    /// does not change them.
    /// </summary>
    internal sealed class LoadingBoost : Subsystem
    {
        private static AccessTools.FieldRef<FlightGlobals> s_fetchField;
        private static int s_missFrame = -1;
        private static long s_saved;
        private bool _settings = true, _unlockFrames = true, _applied;
        private ThreadPriority _priority;
        private int _sliceMs, _bufferMb, _vsync, _targetFps;
        private int _uploadSliceMs = 8, _uploadBufferMb = 64;

        public override string Name => "LoadingBoost";

        // Another mod that replaces one of the game methods patched here (a transpiler, or a prefix that can skip it) keeps
        // its version: this subsystem stands down (reviewed from the start of the load, see SubsystemHost.ReviewSharedTargets).
        public override bool YieldOnSharedTargets => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _settings = settings.GetBool("unitySettings", true);
            _unlockFrames = settings.GetBool("unlockFrameRate", true);
            _uploadSliceMs = Mathf.Clamp(settings.GetInt("uploadSliceMs", _uploadSliceMs), 2, 33);
            _uploadBufferMb = Mathf.Clamp(settings.GetInt("uploadBufferMB", _uploadBufferMb), 4, 1024);
        }

        protected internal override void OnEnable()
        {
            FieldInfo field = AccessTools.Field(typeof(FlightGlobals), "_fetch");
            MethodInfo getter = AccessTools.PropertyGetter(typeof(FlightGlobals), nameof(FlightGlobals.fetch));
            if (field == null || !field.IsStatic || field.FieldType != typeof(FlightGlobals) || getter == null)
                throw new SubsystemUnavailableException("FlightGlobals.fetch not found (game version mismatch?)");
            s_fetchField = AccessTools.StaticFieldRefAccess<FlightGlobals>(field);
            bool kspcfFetch = OtherOptimizers.ActiveKspcfEquivalent(new[] { "MinorPerfTweaks" }) != null;
            if (!kspcfFetch)
                Patch(getter, prefix: nameof(FetchPrefix), postfix: nameof(FetchPostfix));
            if (HighLogic.LoadedScene == GameScenes.LOADING && _settings)
                Apply();
            Log.Info("LoadingBoost: " + (kspcfFetch ? "FlightGlobals.fetch left to KSPCF" : "FlightGlobals.fetch searched at most once per frame while absent")
                     + (_applied ? "; first load: High background loading priority, larger texture upload slices" + (_unlockFrames ? ", no vsync or frame cap" : "") : ""));
        }

        protected internal override void OnDisable() => Restore();

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU)
            {
                Restore();
                if (s_saved > 0)
                    Log.Info("LoadingBoost: " + s_saved + " FlightGlobals.fetch searches skipped (nothing to find in the same frame)");
            }
        }

        private void Apply()
        {
            _priority = Application.backgroundLoadingPriority;
            _sliceMs = QualitySettings.asyncUploadTimeSlice;
            _bufferMb = QualitySettings.asyncUploadBufferSize;
            _vsync = QualitySettings.vSyncCount;
            _targetFps = Application.targetFrameRate;
            Application.backgroundLoadingPriority = ThreadPriority.High;
            QualitySettings.asyncUploadTimeSlice = Math.Max(_sliceMs, _uploadSliceMs);
            QualitySettings.asyncUploadBufferSize = Math.Max(_bufferMb, _uploadBufferMb);
            if (_unlockFrames)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            _applied = true;
        }

        private void Restore()
        {
            if (!_applied)
                return;
            _applied = false;
            Application.backgroundLoadingPriority = _priority;
            QualitySettings.asyncUploadTimeSlice = _sliceMs;
            QualitySettings.asyncUploadBufferSize = _bufferMb;
            if (_unlockFrames)
            {
                QualitySettings.vSyncCount = _vsync;
                Application.targetFrameRate = _targetFps;
            }
        }

        // Stock: if (_fetch == null) _fetch = FindObjectOfType(); return _fetch. A search that found nothing this frame
        // would find nothing again: answered null without searching.
        private static bool FetchPrefix(ref FlightGlobals __result)
        {
            FlightGlobals current = s_fetchField();
            if (current != null)
                return true;   // stock returns it (no search)
            if (s_missFrame == Time.frameCount)
            {
                s_saved++;
                __result = null;
                return false;
            }
            return true;
        }

        private static void FetchPostfix(FlightGlobals __result)
        {
            if (__result == null)
                s_missFrame = Time.frameCount;
        }
    }
}
