using System.Diagnostics;
using System.Text;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Diagnostics
{
    /// <summary>
    /// Splits the frame into its main phases to tell where time goes: FixedUpdate scripts, physics simulation (PhysX plus
    /// collision callbacks), Update scripts, the gap before LateUpdate (coroutines, animators), LateUpdate scripts, and the
    /// rest (rendering, present, vsync wait). Two probe components run first and last in each script phase
    /// ([DefaultExecutionOrder]); timestamps only, allocation-free. One log line per interval in flight.
    /// </summary>
    internal sealed class FrameTimeline : Subsystem
    {
        internal static FrameTimeline Active;

        private GameObject _host;
        private float _intervalSeconds = 60f;
        private readonly StringBuilder _sb = new StringBuilder(256);

        // Accumulated ticks per phase since the last report.
        internal long FixedScripts, Physics, UpdateScripts, PreLate, LateScripts, Render;
        internal int Frames, Steps;

        // Never reset by the reports: read by Patching.Overlaps (FixedUpdate scripts time per physics step, in flight).
        internal long LearnFixedTicks;
        internal long LearnSteps;

        // Phase timestamps.
        internal long FixedStart = -1, FixedEnd = -1, UpdateStart = -1, UpdateEnd = -1, LateStart = -1, LateEnd = -1;
        internal int LastFixedFrame = -1;

        private float _nextReport;

        public override string Name => "FrameTimeline";
        public override bool ReadOnly => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _intervalSeconds = Mathf.Clamp(settings.GetFloat("logIntervalSeconds", _intervalSeconds), 2f, 600f);
        }

        protected internal override void OnEnable()
        {
            _host = new GameObject("Hyperdrive.FrameTimeline");
            Object.DontDestroyOnLoad(_host);
            _host.AddComponent<FrameTimelineFirst>();
            _host.AddComponent<FrameTimelineLast>();
            Active = this;
            Log.Info("FrameTimeline: frame phases logged every " + _intervalSeconds + " s in flight");
        }

        protected internal override void OnDisable()
        {
            Active = null;
            if (_host != null)
                Object.Destroy(_host);
            _host = null;
        }

        protected internal override void OnSceneReady(GameScenes scene) => Reset();

        protected internal override void OnUpdate()
        {
            float now = Time.unscaledTime;
            if (now < _nextReport)
                return;
            _nextReport = now + _intervalSeconds;
            if (Frames > 0 && HighLogic.LoadedScene == GameScenes.FLIGHT)
            {
                double toMs = 1000.0 / Stopwatch.Frequency / Frames;
                StringBuilder sb = _sb;
                sb.Length = 0;
                sb.Append(Log.Prefix).Append("Timeline ms/frame (").Append(Frames).Append(" frames, ")
                  .AppendFixed((float)Steps / Frames, 2).Append(" physics steps/frame): FixedUpdate scripts ").AppendFixed((float)(FixedScripts * toMs), 2)
                  .Append(" | physics sim ").AppendFixed((float)(Physics * toMs), 2)
                  .Append(" | Update scripts ").AppendFixed((float)(UpdateScripts * toMs), 2)
                  .Append(" | coroutines/animation ").AppendFixed((float)(PreLate * toMs), 2)
                  .Append(" | LateUpdate scripts ").AppendFixed((float)(LateScripts * toMs), 2)
                  .Append(" | render+wait ").AppendFixed((float)(Render * toMs), 2);
                UnityEngine.Debug.Log(sb.ToString());
            }
            Reset();
        }

        private void Reset()
        {
            FixedScripts = Physics = UpdateScripts = PreLate = LateScripts = Render = 0;
            Frames = Steps = 0;
        }
    }

    [DefaultExecutionOrder(-32000)]
    internal sealed class FrameTimelineFirst : MonoBehaviour
    {
        private void FixedUpdate()
        {
            FrameTimeline t = FrameTimeline.Active;
            if (t == null)
                return;
            long now = Stopwatch.GetTimestamp();
            int frame = Time.frameCount;
            if (t.LastFixedFrame == frame && t.FixedEnd >= 0)
                t.Physics += now - t.FixedEnd;          // simulation of the previous step of this frame
            else if (t.LateEnd >= 0)
                t.Render += now - t.LateEnd;            // first step of the frame: previous frame's rendering
            t.LastFixedFrame = frame;
            t.FixedStart = now;
            t.Steps++;
        }

        private void Update()
        {
            FrameTimeline t = FrameTimeline.Active;
            if (t == null)
                return;
            long now = Stopwatch.GetTimestamp();
            if (t.LastFixedFrame == Time.frameCount && t.FixedEnd >= 0)
                t.Physics += now - t.FixedEnd;          // simulation of the last step of this frame
            else if (t.LateEnd >= 0)
                t.Render += now - t.LateEnd;            // frame without a physics step
            t.UpdateStart = now;
        }

        private void LateUpdate()
        {
            FrameTimeline t = FrameTimeline.Active;
            if (t == null)
                return;
            long now = Stopwatch.GetTimestamp();
            if (t.UpdateEnd >= 0)
                t.PreLate += now - t.UpdateEnd;
            t.LateStart = now;
        }
    }

    [DefaultExecutionOrder(32000)]
    internal sealed class FrameTimelineLast : MonoBehaviour
    {
        private void FixedUpdate()
        {
            FrameTimeline t = FrameTimeline.Active;
            if (t == null || t.FixedStart < 0)
                return;
            long now = Stopwatch.GetTimestamp();
            t.FixedScripts += now - t.FixedStart;
            t.LearnFixedTicks += now - t.FixedStart;
            t.LearnSteps++;
            t.FixedEnd = now;
        }

        private void Update()
        {
            FrameTimeline t = FrameTimeline.Active;
            if (t == null || t.UpdateStart < 0)
                return;
            long now = Stopwatch.GetTimestamp();
            t.UpdateScripts += now - t.UpdateStart;
            t.UpdateEnd = now;
        }

        private void LateUpdate()
        {
            FrameTimeline t = FrameTimeline.Active;
            if (t == null || t.LateStart < 0)
                return;
            long now = Stopwatch.GetTimestamp();
            t.LateScripts += now - t.LateStart;
            t.LateEnd = now;
            t.Frames++;
        }
    }
}
