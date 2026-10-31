using System;
using System.Threading;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace Hyperdrive.Memory
{
    /// <summary>
    /// Decides WHEN the garbage collector works, on top of KSP's incremental GC:
    ///  - quiet scenes (Space Center, Tracking Station, editor, main menu): cycles already in progress get extra
    ///    incremental budget so that they finish sooner;
    ///  - paused flight (Esc menu): one full collection per pause when enough garbage has piled up, invisible to the
    ///    player, so that play resumes with maximum headroom before the next cycle;
    ///  - holds: during short critical moments in flight (staging, decoupling, explosions, crashes) or on request
    ///    through the API, the GC is disabled for a few seconds, then re-enabled. Holds are capped in duration and
    ///    memory growth and followed by a cooldown, so the GC can never stay off.
    /// </summary>
    internal sealed class GCWindows : Subsystem
    {
        private const long MB = 1024L * 1024L;

        public static GCWindows Active { get; private set; }

        // Settings
        private float _incrementalSliceMs;          // 0 = keep the game's value
        private bool _quietCollect = true;
        private float _quietBudgetMs = 4f;
        private bool _collectWhenPaused = true;
        private long _pausedCollectTriggerBytes = 64 * MB;
        private long _pausedCollectMaxUsedBytes = 1536 * MB;
        private float _pausedCollectMaxMs = 200f;
        private bool _holdOnFlightEvents = true;
        private float _holdSeconds = 1.5f;
        private float _maxHoldSeconds = 4f;
        private float _holdCooldownSeconds = 10f;
        private long _holdMaxGrowthBytes = 384 * MB;
        private float _sceneGraceSeconds = 5f;

        // State
        private ulong _originalSliceNs;
        private int _mainThreadId;
        private bool _holding;
        private float _holdStart, _holdUntil, _cooldownUntil;
        private float _graceUntil = float.MaxValue;
        private long _usedAtHoldStart;
        private bool _cycleInProgress;
        private bool _collectedThisPause;
        private int _lastGcCount = -1;
        private long _usedAfterLastGc;
        private bool _pausedCollectTooSlow;

        public int HoldCount { get; private set; }
        /// <summary>GC cycles finished with our extra budget in quiet scenes.</summary>
        public int QuietCycles { get; private set; }
        /// <summary>Full collections done while the game was paused.</summary>
        public int PausedCollections { get; private set; }
        public bool Holding => _holding;
        public string LastHoldReason { get; private set; } = "";

        public override string Name => "GCWindows";

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _incrementalSliceMs = Mathf.Clamp(settings.GetFloat("incrementalSliceMs", 0f), 0f, 20f);
            _quietCollect = settings.GetBool("quietCollect", _quietCollect);
            _quietBudgetMs = Mathf.Clamp(settings.GetFloat("quietBudgetMs", _quietBudgetMs), 0.5f, 20f);
            _collectWhenPaused = settings.GetBool("collectWhenPaused", _collectWhenPaused);
            _pausedCollectTriggerBytes = Mathf.Clamp(settings.GetInt("pausedCollectTriggerMB", 64), 1, 8192) * MB;
            _pausedCollectMaxUsedBytes = Mathf.Clamp(settings.GetInt("pausedCollectMaxHeapMB", 1536), 64, 65536) * MB;
            _pausedCollectMaxMs = Mathf.Clamp(settings.GetFloat("pausedCollectMaxMs", _pausedCollectMaxMs), 10f, 5000f);
            _holdOnFlightEvents = settings.GetBool("holdOnFlightEvents", _holdOnFlightEvents);
            _holdSeconds = Mathf.Clamp(settings.GetFloat("holdSeconds", _holdSeconds), 0.1f, 10f);
            _maxHoldSeconds = Mathf.Clamp(settings.GetFloat("maxHoldSeconds", _maxHoldSeconds), _holdSeconds, 15f);
            _holdCooldownSeconds = Mathf.Clamp(settings.GetFloat("holdCooldownSeconds", _holdCooldownSeconds), 1f, 120f);
            _holdMaxGrowthBytes = Mathf.Clamp(settings.GetInt("holdMaxGrowthMB", 384), 32, 4096) * MB;
            _sceneGraceSeconds = Mathf.Clamp(settings.GetFloat("sceneGraceSeconds", _sceneGraceSeconds), 0f, 60f);
        }

        protected internal override void OnEnable()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            BoehmGC.TryInitialize();

            _originalSliceNs = GarbageCollector.incrementalTimeSliceNanoseconds;
            if (_incrementalSliceMs > 0f && GarbageCollector.isIncremental)
                GarbageCollector.incrementalTimeSliceNanoseconds = (ulong)(_incrementalSliceMs * 1000000f);

            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onStageActivate.Add(OnStageActivate);
            GameEvents.onPartDeCouple.Add(OnPartDeCouple);
            GameEvents.onPartExplode.Add(OnPartExplode);
            GameEvents.onPartDie.Add(OnPartDie);
            GameEvents.onCrash.Add(OnCrash);

            Active = this;
            Log.Info("GCWindows: incremental=" + GarbageCollector.isIncremental
                     + " slice " + (_originalSliceNs / 1000000.0).ToString("F1") + " -> " + (GarbageCollector.incrementalTimeSliceNanoseconds / 1000000.0).ToString("F1") + " ms"
                     + ", quiet-scene budget " + (_quietCollect && GarbageCollector.isIncremental ? "+" + _quietBudgetMs + " ms/frame" : "off")
                     + ", collect when paused " + (_collectWhenPaused ? "on (>= " + (_pausedCollectTriggerBytes / MB) + " MB)" : "off")
                     + ", flight holds " + (_holdOnFlightEvents ? "on (" + _holdSeconds + " s, max " + _maxHoldSeconds + " s, +" + (_holdMaxGrowthBytes / MB) + " MB)" : "off"));
        }

        protected internal override void OnDisable()
        {
            if (Active == this)
                Active = null;

            EndHold();
            if (GarbageCollector.incrementalTimeSliceNanoseconds != _originalSliceNs && _originalSliceNs > 0)
                GarbageCollector.incrementalTimeSliceNanoseconds = _originalSliceNs;

            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onStageActivate.Remove(OnStageActivate);
            GameEvents.onPartDeCouple.Remove(OnPartDeCouple);
            GameEvents.onPartExplode.Remove(OnPartExplode);
            GameEvents.onPartDie.Remove(OnPartDie);
            GameEvents.onCrash.Remove(OnCrash);
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            _graceUntil = Time.unscaledTime + _sceneGraceSeconds;
        }

        protected internal override void OnUpdate()
        {
            if (_holding)
            {
                float now = Time.unscaledTime;
                if (now >= _holdUntil || now - _holdStart >= _maxHoldSeconds
                    || Profiler.GetMonoUsedSizeLong() - _usedAtHoldStart > _holdMaxGrowthBytes)
                    EndHold();
                return;
            }

            // Used heap right after the last cycle: where the Boehm counter is missing (Windows), growth since then
            // stands in for the bytes allocated since the last GC.
            int gcCount = GC.CollectionCount(0);
            if (gcCount != _lastGcCount)
            {
                _lastGcCount = gcCount;
                _usedAfterLastGc = Profiler.GetMonoUsedSizeLong();
            }

            bool paused = HighLogic.LoadedScene == GameScenes.FLIGHT && FlightDriver.Pause;
            if (!paused)
                _collectedThisPause = false;

            if (GarbageCollector.GCMode != GarbageCollector.Mode.Enabled)
            {
                _cycleInProgress = false;
                return;
            }

            if (paused)
                CollectWhilePaused();
            else if (_quietCollect && GarbageCollector.isIncremental && IsQuietScene())
                FinishCycleInQuietScene();
            else
                _cycleInProgress = false;
        }

        /// <summary>
        /// Disable the GC for up to <paramref name="seconds"/> (capped). Main thread only.
        /// Returns false when refused (cooldown, scene loading, someone else controls the GC, wrong thread).
        /// </summary>
        public bool RequestHold(float seconds, string reason)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId || seconds <= 0f)
                return false;

            float now = Time.unscaledTime;
            if (now < _graceUntil)
                return false;

            if (_holding)
            {
                float extended = Mathf.Min(now + seconds, _holdStart + _maxHoldSeconds);
                if (extended > _holdUntil)
                    _holdUntil = extended;
                return true;
            }

            if (now < _cooldownUntil || GarbageCollector.GCMode != GarbageCollector.Mode.Enabled)
                return false;

            _usedAtHoldStart = Profiler.GetMonoUsedSizeLong();
            _holdStart = now;
            _holdUntil = now + Mathf.Min(seconds, _maxHoldSeconds);
            GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
            _holding = true;
            _cycleInProgress = false;
            HoldCount++;
            LastHoldReason = reason ?? "";
            return true;
        }

        private void EndHold()
        {
            if (!_holding)
                return;
            _holding = false;
            GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
            _cooldownUntil = Time.unscaledTime + _holdCooldownSeconds;
        }

        private static bool IsQuietScene()
        {
            switch (HighLogic.LoadedScene)
            {
                case GameScenes.MAINMENU:
                case GameScenes.SPACECENTER:
                case GameScenes.TRACKSTATION:
                case GameScenes.EDITOR:
                    return true;
                default:
                    return false;
            }
        }

        // Measured on KSP 1.12.5: CollectIncremental advances a cycle that is already running but does not start one
        // (Boehm only starts a cycle once its allocation threshold is reached). So in quiet scenes we only speed up
        // cycles in progress; this is a cheap no-op when there is none.
        private void FinishCycleInQuietScene()
        {
            bool moreWork = GarbageCollector.CollectIncremental((ulong)(_quietBudgetMs * 1000000f));
            if (_cycleInProgress && !moreWork)
                QuietCycles++;
            _cycleInProgress = moreWork;
        }

        // The game is paused (Esc menu): a blocking full collection is invisible here and resets the allocation
        // counter, so play resumes with the maximum headroom before the next cycle. Once per pause.
        private void CollectWhilePaused()
        {
            if (_collectedThisPause || !_collectWhenPaused)
                return;
            _collectedThisPause = true;

            // A full collection costs time in proportion to the live heap: ~1 s for 5 GB on a fast PC (measured on a
            // heavily modded install). The pause menu would freeze for that long, so big heaps and slow machines skip it.
            long used = Profiler.GetMonoUsedSizeLong();
            if (_pausedCollectTooSlow || used > _pausedCollectMaxUsedBytes)
                return;
            long sinceGc = BoehmGC.BytesSinceGC;
            if (sinceGc < 0)
                sinceGc = used - _usedAfterLastGc;
            if (sinceGc < _pausedCollectTriggerBytes)
                return;

            float start = Time.realtimeSinceStartup;
            GC.Collect();
            PausedCollections++;
            float ms = (Time.realtimeSinceStartup - start) * 1000f;
            Log.Debug("GCWindows: full collection while paused took " + ms.ToString("F0") + " ms");
            if (ms > _pausedCollectMaxMs)
            {
                _pausedCollectTooSlow = true;
                Log.Info("GCWindows: a full collection while paused took " + ms.ToString("F0") + " ms (heap " + (used / MB)
                         + " MB used): no more collections on pause this session");
            }
        }

        // ---- flight events (allocation-free handlers) ----

        private void OnSceneLoadRequested(GameScenes scene)
        {
            EndHold();
            _graceUntil = float.MaxValue;   // until the next scene is ready
        }

        private bool FlightHoldsAllowed => _holdOnFlightEvents && HighLogic.LoadedScene == GameScenes.FLIGHT;

        private void OnStageActivate(int stage)
        {
            if (FlightHoldsAllowed) RequestHold(_holdSeconds, "staging");
        }

        private void OnPartDeCouple(Part part)
        {
            if (FlightHoldsAllowed) RequestHold(_holdSeconds, "decouple");
        }

        private void OnPartExplode(GameEvents.ExplosionReaction reaction)
        {
            if (FlightHoldsAllowed) RequestHold(_holdSeconds, "explosion");
        }

        private void OnPartDie(Part part)
        {
            if (FlightHoldsAllowed) RequestHold(_holdSeconds, "part destroyed");
        }

        private void OnCrash(EventReport report)
        {
            if (FlightHoldsAllowed) RequestHold(_holdSeconds, "crash");
        }
    }
}
