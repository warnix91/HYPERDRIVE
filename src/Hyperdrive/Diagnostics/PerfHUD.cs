using System;
using System.Text;
using Hyperdrive.Core;
using Hyperdrive.Memory;
using Hyperdrive.Patching;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace Hyperdrive.Diagnostics
{
    /// <summary>
    /// Frame time, spikes, GC cycles and heap, shown in a small overlay (toggle key) and written to KSP.log
    /// as one summary line per interval. Measurement is allocation-free; the overlay rebuilds its text
    /// twice per second only while visible (one string per rebuild), and the periodic log line allocates
    /// once per interval.
    /// </summary>
    internal sealed class PerfHUD : Subsystem
    {
        private const float DisplayRefreshSeconds = 0.5f;
        private const float MaxCountedFrameSeconds = 1f;       // longer frames are loading hitches, not gameplay
        private const float GcProximitySeconds = 0.1f;          // a spike this close to a GC completion is "near GC"

        private bool _showAtStartup;
        private KeyCode _toggleKey = KeyCode.F10;
        private bool _toggleNeedsAlt = true;
        private float _logIntervalSeconds = 60f;
        private float _spikeThresholdMs = 50f;

        private PerfHudView _view;
        private Exception _viewFailure;
        private readonly StringBuilder _sb = new StringBuilder(768);

        // Display window (0.5 s)
        private float _winElapsed;
        private int _winFrames;
        private float _winMaxFrame;
        private float _shownFps, _shownAvgMs, _shownMaxMs;

        // Log window (logIntervalSeconds)
        private float _logElapsed;
        private int _logFrames;
        private float _logMaxFrame;
        private float _logMinFps = float.MaxValue;
        private int _logSpikes, _logGcSpikes;
        private long _logGcStart;

        // Allocation rate (native Boehm counter, reset at each completed cycle)
        private long _allocLast = -1, _allocGcLast;
        private long _winAllocBytes, _logAllocBytes;
        private float _shownAllocMBps;

        // Session
        private int _spikes, _gcSpikes;
        private long _lastGcCount;
        private float _lastGcTime = -1f;
        private float _ignoreUntil;

        public bool Visible { get; private set; }

        public string Text { get; private set; } = "";

        public override string Name => "PerfHUD";
        public override bool ReadOnly => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _showAtStartup = settings.GetBool("showAtStartup", false);
            _toggleNeedsAlt = settings.GetBool("toggleWithAlt", true);
            _logIntervalSeconds = settings.GetFloat("logIntervalSeconds", _logIntervalSeconds);
            _spikeThresholdMs = Mathf.Max(5f, settings.GetFloat("spikeThresholdMs", _spikeThresholdMs));

            string key = settings.GetString("toggleKey", null);
            if (!string.IsNullOrEmpty(key))
            {
                try { _toggleKey = (KeyCode)Enum.Parse(typeof(KeyCode), key, true); }
                catch (Exception) { Log.Warn("PerfHUD: unknown toggleKey '" + key + "', using F10"); }
            }
        }

        protected internal override void OnEnable()
        {
            BoehmGC.TryInitialize();
            var host = new GameObject("Hyperdrive.PerfHUD");
            UnityEngine.Object.DontDestroyOnLoad(host);
            _view = host.AddComponent<PerfHudView>();
            _view.Owner = this;
            Visible = _showAtStartup;
            _lastGcCount = GC.CollectionCount(0);
            _logGcStart = _lastGcCount;
            Log.Info("PerfHUD ready: toggle with " + (_toggleNeedsAlt ? "Alt+" : "") + _toggleKey
                     + (_logIntervalSeconds > 0 ? ", summary every " + _logIntervalSeconds + " s in KSP.log" : ""));
        }

        protected internal override void OnDisable()
        {
            if (_view != null)
                UnityEngine.Object.Destroy(_view.gameObject);
            _view = null;
            Visible = false;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            _ignoreUntil = Time.unscaledTime + 3f;
        }

        internal void ReportViewFailure(Exception e) => _viewFailure = e;

        protected internal override void OnUpdate()
        {
            if (_viewFailure != null)
                throw new InvalidOperationException("overlay failed", _viewFailure);

            if (Input.GetKeyDown(_toggleKey) && (!_toggleNeedsAlt || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
                Visible = !Visible;

            float now = Time.unscaledTime;
            float dt = Time.unscaledDeltaTime;

            long gcCount = GC.CollectionCount(0);
            if (gcCount != _lastGcCount)
            {
                _lastGcCount = gcCount;
                _lastGcTime = now;
            }

            long allocNow = BoehmGC.BytesSinceGC;
            if (allocNow >= 0)
            {
                long gcNo = BoehmGC.CollectionNumber;
                if (_allocLast >= 0)
                {
                    long delta = gcNo == _allocGcLast && allocNow >= _allocLast ? allocNow - _allocLast : allocNow;
                    _winAllocBytes += delta;
                    _logAllocBytes += delta;
                }
                _allocLast = allocNow;
                _allocGcLast = gcNo;
            }

            bool counted = dt > 0f && dt < MaxCountedFrameSeconds && now >= _ignoreUntil;
            if (counted)
            {
                float ms = dt * 1000f;
                _winFrames++;
                _logFrames++;
                if (ms > _winMaxFrame) _winMaxFrame = ms;
                if (ms > _logMaxFrame) _logMaxFrame = ms;

                if (ms > _spikeThresholdMs)
                {
                    _spikes++;
                    _logSpikes++;
                    if (_lastGcTime >= 0f && now - _lastGcTime <= GcProximitySeconds)
                    {
                        _gcSpikes++;
                        _logGcSpikes++;
                    }
                }
            }

            _winElapsed += dt;
            if (_winElapsed >= DisplayRefreshSeconds)
            {
                if (_winFrames > 0)
                {
                    _shownFps = _winFrames / _winElapsed;
                    _shownAvgMs = _winElapsed * 1000f / _winFrames;
                    _shownMaxMs = _winMaxFrame;
                    if (_shownFps < _logMinFps) _logMinFps = _shownFps;
                }
                _shownAllocMBps = _winAllocBytes / (1024f * 1024f) / _winElapsed;
                _winAllocBytes = 0;
                _winElapsed = 0f;
                _winFrames = 0;
                _winMaxFrame = 0f;
                if (Visible)
                    RebuildText(now);
            }

            _logElapsed += dt;
            if (_logIntervalSeconds > 0f && _logElapsed >= _logIntervalSeconds)
            {
                if (_logFrames > 0)
                    WriteLogLine();
                _logElapsed = 0f;
                _logFrames = 0;
                _logMaxFrame = 0f;
                _logMinFps = float.MaxValue;
                _logSpikes = 0;
                _logGcSpikes = 0;
                _logGcStart = gcCount;
                _logAllocBytes = 0;
            }
        }

        private void RebuildText(float now)
        {
            StringBuilder sb = _sb;
            sb.Length = 0;
            sb.Append("Hyperdrive PerfHUD  (").Append(_toggleNeedsAlt ? "Alt+" : "").Append(KeyName()).Append(")\n");
            sb.Append("FPS ").AppendFixed(_shownFps, 1)
              .Append("   frame ").AppendFixed(_shownAvgMs, 1).Append(" ms avg / ").AppendFixed(_shownMaxMs, 1).Append(" max\n");
            sb.Append("Spikes >").AppendFixed(_spikeThresholdMs, 0).Append(" ms: ").AppendInt(_spikes)
              .Append(" (near GC ").AppendInt(_gcSpikes).Append(")\n");
            sb.Append("GC cycles ").AppendInt(_lastGcCount).Append("   last ");
            if (_lastGcTime < 0f) sb.Append("-");
            else sb.AppendFixed(now - _lastGcTime, 1).Append(" s ago");
            float minutes = _logElapsed / 60f;
            if (minutes > 0.05f)
                sb.Append("   ").AppendFixed((_lastGcCount - _logGcStart) / minutes, 1).Append("/min");
            sb.Append('\n');
            AppendHeap(sb);
            if (_allocLast >= 0)
                sb.Append("   alloc ").AppendFixed(_shownAllocMBps, 1).Append(" MB/s");
            sb.Append('\n');
            AppendGcState(sb);
            Text = sb.ToString();
        }

        private void WriteLogLine()
        {
            StringBuilder sb = _sb;
            sb.Length = 0;
            sb.Append(Log.Prefix).Append("Perf ").AppendFixed(_logElapsed, 0).Append(" s ").Append(HighLogic.LoadedScene.ToString())
              .Append(": fps avg ").AppendFixed(_logFrames / _logElapsed, 1)
              .Append(" min ").AppendFixed(_logMinFps == float.MaxValue ? 0f : _logMinFps, 1)
              .Append(" | frame max ").AppendFixed(_logMaxFrame, 0).Append(" ms")
              .Append(" | spikes>").AppendFixed(_spikeThresholdMs, 0).Append("ms ").AppendInt(_logSpikes)
              .Append(" (near GC ").AppendInt(_logGcSpikes).Append(')')
              .Append(" | GC +").AppendInt(_lastGcCount - _logGcStart).Append(" (total ").AppendInt(_lastGcCount).Append(")")
              .Append(" | ");
            if (_allocLast >= 0)
                sb.Append("alloc ").AppendFixed(_logAllocBytes / (1024.0 * 1024.0) / _logElapsed, 1).Append(" MB/s | ");
            AppendHeap(sb);
            sb.Append(" | ");
            AppendGcState(sb);
            Debug.Log(sb.ToString());
        }

        private static void AppendHeap(StringBuilder sb)
        {
            sb.Append("heap ").AppendMB(Profiler.GetMonoHeapSizeLong())
              .Append(" MB used ").AppendMB(Profiler.GetMonoUsedSizeLong()).Append(" MB");
            long sinceGc = BoehmGC.BytesSinceGC;
            if (sinceGc >= 0)
                sb.Append(" sinceGC ").AppendMB(sinceGc).Append(" MB");
        }

        private static void AppendGcState(StringBuilder sb)
        {
            sb.Append("GC ").Append(GarbageCollector.GCMode == GarbageCollector.Mode.Enabled ? "on" : "OFF")
              .Append(GarbageCollector.isIncremental ? " incr " : " full ")
              .AppendFixed(GarbageCollector.incrementalTimeSliceNanoseconds / 1000000.0, 1).Append(" ms");
            if (BoehmGC.Available)
                sb.Append(" div ").AppendInt(BoehmGC.FreeSpaceDivisor);

            GCWindows windows = GCWindows.Active;
            if (windows != null)
            {
                sb.Append(" holds ").AppendInt(windows.HoldCount);
                if (windows.Holding)
                    sb.Append(" [HOLD ").Append(windows.LastHoldReason).Append(']');
                sb.Append(" quiet ").AppendInt(windows.QuietCycles).Append(" paused ").AppendInt(windows.PausedCollections);
            }
        }

        private string KeyName() => _toggleKey == KeyCode.F10 ? "F10" : _toggleKey.ToString();
    }

    /// <summary>Draws the PerfHUD text. Immediate-mode GUI without GUILayout to keep allocations minimal.</summary>
    internal sealed class PerfHudView : MonoBehaviour
    {
        public PerfHUD Owner;

        private GUIStyle _style;
        private Rect _rect = new Rect(12f, 60f, 470f, 128f);

        private void Awake()
        {
            useGUILayout = false;
        }

        private void OnGUI()
        {
            PerfHUD owner = Owner;
            if (owner == null || !owner.Visible || Event.current.type != EventType.Repaint)
                return;

            try
            {
                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = false, richText = false };
                    _style.normal.textColor = Color.white;
                }
                GUI.Box(_rect, GUIContent.none);
                GUI.Label(new Rect(_rect.x + 8f, _rect.y + 4f, _rect.width - 16f, _rect.height - 8f), owner.Text, _style);
            }
            catch (Exception e)
            {
                enabled = false;
                owner.ReportViewFailure(e);
            }
        }
    }
}
