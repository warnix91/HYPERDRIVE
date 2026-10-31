using System;
using System.Diagnostics;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace Hyperdrive.Memory
{
    /// <summary>
    /// Makes GC cycles rarer by giving the Mono heap more headroom, using the Boehm GC's own knobs
    /// instead of MemGraph-style dummy allocations:
    ///  - free space divisor: a collection starts after roughly (live data / divisor) bytes have been allocated,
    ///    so lowering it (3 -> 2 or 1) makes cycles 1.5x-3x rarer, at the cost of a bigger heap;
    ///  - pre-expansion (GC_expand_hp): grows the heap up front instead of in many steps during play.
    /// A safety valve raises the divisor back step by step if the heap exceeds a share of system RAM.
    /// Started at Instantly, so game loading also benefits.
    /// Where the Boehm knobs are not exported (Unity's Windows player), loading still gets rarer cycles through Unity's
    /// public API: automatic collection is suspended (GarbageCollector.GCMode) and one full collection runs whenever the
    /// used heap has grown by a share of system RAM since the last one, until the main menu.
    /// </summary>
    internal sealed class HeapPadder : Subsystem
    {
        private const float SafetyCheckSeconds = 5f;
        private const long MB = 1024L * 1024L;

        private int _divisorSetting;      // 0 = auto
        private int _preExpandSetting;    // -1 = auto, 0 = off
        private int _maxHeapPercentOfRam = 40;
        private int _loadingDivisor = 1;  // 0 = off
        private bool _loadingBoostActive;
        private int _managedGrowthPercent;       // 0 = off (default)
        private bool _managedLoading;
        private long _managedGrowthBytes;
        private long _usedAfterCollect;
        private int _managedCollections;
        private long _managedCollectTicks;

        private int _originalDivisor;
        private int _appliedDivisor;
        private long _heapBudget;
        private float _nextSafetyCheck;

        public override string Name => "HeapPadder";

        // The HeapPadder mod and MemGraph pad the Mono heap themselves: one padder only.
        public override string[] IncompatibleAssemblies => new[] { "HeapPadder", "MemGraph" };

        protected internal override void LoadSettings(SettingsSection settings)
        {
            string divisor = settings.GetString("freeSpaceDivisor", "auto");
            _divisorSetting = string.Equals(divisor, "auto", StringComparison.OrdinalIgnoreCase) ? 0 : Mathf.Clamp(settings.GetInt("freeSpaceDivisor", 0), 0, 10);

            string preExpand = settings.GetString("preExpandMB", "auto");
            _preExpandSetting = string.Equals(preExpand, "auto", StringComparison.OrdinalIgnoreCase) ? -1 : Mathf.Clamp(settings.GetInt("preExpandMB", 0), 0, 8192);

            _maxHeapPercentOfRam = Mathf.Clamp(settings.GetInt("maxHeapPercentOfRam", _maxHeapPercentOfRam), 10, 80);
            _loadingDivisor = Mathf.Clamp(settings.GetInt("loadingDivisor", _loadingDivisor), 0, 10);
            _managedGrowthPercent = Mathf.Clamp(settings.GetInt("managedLoadingGrowthPercentOfRam", _managedGrowthPercent), 0, 25);
        }

        protected internal override void OnEnable()
        {
            int ramMB = SystemInfo.systemMemorySize;
            if (!BoehmGC.TryInitialize())
            {
                if (_managedGrowthPercent <= 0 || HighLogic.LoadedScene != GameScenes.LOADING)
                    throw new SubsystemUnavailableException("native Boehm API unavailable: " + BoehmGC.Status);
                StartManagedLoading(ramMB);
                return;
            }

            _originalDivisor = BoehmGC.FreeSpaceDivisor;
            _heapBudget = ramMB * MB * _maxHeapPercentOfRam / 100;

            int target = _divisorSetting > 0 ? _divisorSetting : AutoDivisor(ramMB, _originalDivisor);
            if (target != _originalDivisor)
                BoehmGC.FreeSpaceDivisor = target;
            _appliedDivisor = BoehmGC.FreeSpaceDivisor;

            int expandMB = _preExpandSetting >= 0 ? _preExpandSetting : AutoPreExpandMB(ramMB);
            bool expanded = expandMB > 0 && BoehmGC.ExpandHeap(expandMB * MB);

            // Even rarer GC while the game loads (lots of garbage, nobody playing); normal value back at the main menu.
            if (_loadingDivisor > 0 && _loadingDivisor < _appliedDivisor && HighLogic.LoadedScene == GameScenes.LOADING)
            {
                BoehmGC.FreeSpaceDivisor = _loadingDivisor;
                _loadingBoostActive = true;
            }

            Log.Info("HeapPadder: free space divisor " + _originalDivisor + " -> " + _appliedDivisor
                     + (_loadingBoostActive ? " (" + _loadingDivisor + " while loading)" : "")
                     + (expandMB > 0 ? ", heap pre-expanded by " + expandMB + " MB" + (expanded ? "" : " (FAILED)") : "")
                     + ", heap budget " + (_heapBudget / MB) + " MB (" + _maxHeapPercentOfRam + "% of " + ramMB + " MB RAM)");
        }

        protected internal override void OnDisable()
        {
            if (_originalDivisor > 0 && BoehmGC.Available)
                BoehmGC.FreeSpaceDivisor = _originalDivisor;
            if (_managedLoading)
                EndManagedLoading("subsystem disabled");
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (_managedLoading && scene != GameScenes.LOADING)
                EndManagedLoading("loading finished");

            if (_loadingBoostActive && scene != GameScenes.LOADING)
            {
                _loadingBoostActive = false;
                BoehmGC.FreeSpaceDivisor = _appliedDivisor;
                Log.Info("HeapPadder: loading finished, divisor back to " + _appliedDivisor + " (heap " + (BoehmGC.HeapSize / MB) + " MB)");
            }
        }

        protected internal override void OnUpdate()
        {
            if (_managedLoading)
            {
                UpdateManagedLoading();
                return;
            }
            if (!BoehmGC.Available)
                return;
            float now = Time.unscaledTime;
            if (now < _nextSafetyCheck)
                return;
            _nextSafetyCheck = now + SafetyCheckSeconds;

            // The heap never shrinks: once over budget, collect more often to stop further growth.
            if (_loadingBoostActive && BoehmGC.HeapSize > _heapBudget)
            {
                _loadingBoostActive = false;
                BoehmGC.FreeSpaceDivisor = _appliedDivisor;
                Log.Warn("HeapPadder: heap over budget during loading, loading boost stopped");
            }
            else if (_appliedDivisor < _originalDivisor && BoehmGC.HeapSize > _heapBudget)
            {
                _appliedDivisor++;
                BoehmGC.FreeSpaceDivisor = _appliedDivisor;
                Log.Warn("HeapPadder: heap " + (BoehmGC.HeapSize / MB) + " MB is over budget, divisor raised to " + _appliedDivisor);
            }
        }

        // ---- managed loading mode (no Boehm knobs) ----

        private void StartManagedLoading(int ramMB)
        {
            _heapBudget = ramMB * MB * _maxHeapPercentOfRam / 100;
            _managedGrowthBytes = ramMB * MB * _managedGrowthPercent / 100;
            _usedAfterCollect = Profiler.GetMonoUsedSizeLong();
            GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
            _managedLoading = true;
            Log.Info("HeapPadder: native Boehm API unavailable (" + BoehmGC.Status + "); while loading, automatic GC suspended and one collection per "
                     + (_managedGrowthBytes / MB) + " MB of heap growth (" + _managedGrowthPercent + "% of " + ramMB + " MB RAM), heap budget " + (_heapBudget / MB) + " MB");
        }

        // Every frame while loading: two native counter reads, no allocation.
        private void UpdateManagedLoading()
        {
            if (GarbageCollector.GCMode != GarbageCollector.Mode.Disabled)
            {
                // Someone else turned automatic collection back on: leave it to them.
                EndManagedLoading("GC mode changed by another component");
                return;
            }
            if (Profiler.GetMonoHeapSizeLong() > _heapBudget)
            {
                EndManagedLoading("heap over budget");
                return;
            }
            if (Profiler.GetMonoUsedSizeLong() - _usedAfterCollect < _managedGrowthBytes)
                return;
            long start = Stopwatch.GetTimestamp();
            GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
            GC.Collect();
            GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
            _managedCollectTicks += Stopwatch.GetTimestamp() - start;
            _managedCollections++;
            _usedAfterCollect = Profiler.GetMonoUsedSizeLong();
        }

        private void EndManagedLoading(string why)
        {
            _managedLoading = false;
            if (GarbageCollector.GCMode == GarbageCollector.Mode.Disabled)
                GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
            Log.Info("HeapPadder: " + why + ": automatic GC back on after " + _managedCollections + " managed collections ("
                     + (_managedCollectTicks * 1000 / Stopwatch.Frequency) + " ms in total), heap " + (Profiler.GetMonoHeapSizeLong() / MB) + " MB, used "
                     + (Profiler.GetMonoUsedSizeLong() / MB) + " MB");
        }

        // Lower divisor = rarer GC but bigger heap. Only ever lowers the game's own value.
        private static int AutoDivisor(int ramMB, int original)
        {
            int target = ramMB >= 24000 ? 1 : ramMB >= 12000 ? 2 : original;
            return Math.Min(target, original);
        }

        private static int AutoPreExpandMB(int ramMB) => ramMB >= 24000 ? 1024 : ramMB >= 12000 ? 512 : 256;
    }
}
