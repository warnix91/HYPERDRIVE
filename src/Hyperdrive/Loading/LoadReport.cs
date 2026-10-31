using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// Which mod makes loading slow? While the game loads, KSP logs one line per asset ("Load(Texture): Mod/...",
    /// "Load(Model): ...", "Load(Audio): ...", "Config(PART) ...", "PartLoader: Compiling Part '...'"). The time until the
    /// next such line is charged to that asset's mod (first folder of its URL) and category. At the main menu a ranking
    /// is written to KSP.log. Read-only, no patch; stops listening once loading is over.
    /// </summary>
    internal sealed class LoadReport : Subsystem
    {
        private enum Category { Texture, Model, Audio, Config, Part, Count }

        private static readonly string[] CategoryNames = { "textures", "models", "audio", "configs", "parts" };

        private sealed class ModStats
        {
            public readonly int[] Count = new int[(int)Category.Count];
            public readonly long[] Ticks = new long[(int)Category.Count];
            public long Total;
        }

        private readonly Dictionary<string, ModStats> _mods = new Dictionary<string, ModStats>(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _clock = new Stopwatch();
        private ModStats _pendingMod;
        private Category _pendingCategory;
        private long _pendingStart;
        private int _pendingFrame;
        private readonly int[] _frames = new int[(int)Category.Count];
        private string _pendingMessage;
        private string _lastAnyMessage;
        private long _lastAnyTicks = -1;
        private readonly List<KeyValuePair<double, string>> _silences = new List<KeyValuePair<double, string>>();
        private readonly List<KeyValuePair<double, string>> _stalls = new List<KeyValuePair<double, string>>();
        private float _lastFrameTime = -1f;
        private int _framesOver100ms;
        private readonly List<KeyValuePair<double, string>> _gaps = new List<KeyValuePair<double, string>>();
        private bool _listening;
        private int _reportTop = 25;

        public override string Name => "LoadReport";
        public override bool ReadOnly => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _reportTop = Mathf.Clamp(settings.GetInt("reportTop", _reportTop), 5, 200);
        }

        protected internal override void OnEnable()
        {
            _clock.Start();
            Application.logMessageReceived += OnLog;
            _listening = true;
        }

        protected internal override void OnDisable() => StopListening();

        // Frames that took long while loading (the loading screen is frozen during them), with the last log line.
        protected internal override void OnUpdate()
        {
            if (!_listening)
                return;
            float now = Time.realtimeSinceStartup;
            if (_lastFrameTime >= 0f)
            {
                float gap = now - _lastFrameTime;
                if (gap > 0.1f)
                    _framesOver100ms++;
                if (gap > 0.4f)
                {
                    _stalls.Add(new KeyValuePair<double, string>(gap, _lastAnyMessage));
                    if (_stalls.Count > 40)
                    {
                        _stalls.Sort((a, b) => b.Key.CompareTo(a.Key));
                        _stalls.RemoveRange(15, _stalls.Count - 15);
                    }
                }
            }
            _lastFrameTime = now;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (!_listening || scene != GameScenes.MAINMENU)
                return;
            ClosePending(_clock.ElapsedTicks);
            StopListening();
            Report();
        }

        private void StopListening()
        {
            if (!_listening)
                return;
            Application.logMessageReceived -= OnLog;
            _listening = false;
        }

        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (message == null)
                return;
            TrackSilence(message);
            if (message.Length < 8)
                return;

            int urlStart;
            Category category;
            if (message.StartsWith("Load(Texture): ", StringComparison.Ordinal)) { category = Category.Texture; urlStart = 15; }
            else if (message.StartsWith("Load(Model): ", StringComparison.Ordinal)) { category = Category.Model; urlStart = 13; }
            else if (message.StartsWith("Load(Audio): ", StringComparison.Ordinal)) { category = Category.Audio; urlStart = 13; }
            else if (message.StartsWith("PartLoader: Compiling Part '", StringComparison.Ordinal)) { category = Category.Part; urlStart = 28; }
            else if (message.StartsWith("Config(", StringComparison.Ordinal))
            {
                int close = message.IndexOf(") ", 7, StringComparison.Ordinal);
                if (close < 0) return;
                category = Category.Config;
                urlStart = close + 2;
            }
            else return;

            long now = _clock.ElapsedTicks;
            ClosePending(now);

            int slash = message.IndexOf('/', urlStart);
            string mod = slash > urlStart ? message.Substring(urlStart, slash - urlStart) : "(root)";
            if (!_mods.TryGetValue(mod, out ModStats stats))
            {
                stats = new ModStats();
                _mods[mod] = stats;
            }
            stats.Count[(int)category]++;
            _pendingMod = stats;
            _pendingCategory = category;
            _pendingStart = now;
            _pendingFrame = Time.frameCount;
            _pendingMessage = message;
        }

        // Time between any two log lines while loading: the longest ones show phases that log nothing (drag cubes,
        // bundles, icon rendering...). Keeps the 12 longest above 0.5 s.
        private void TrackSilence(string message)
        {
            long now = _clock.ElapsedTicks;
            if (_lastAnyTicks >= 0)
            {
                double seconds = (double)(now - _lastAnyTicks) / Stopwatch.Frequency;
                if (seconds >= 0.5)
                {
                    _silences.Add(new KeyValuePair<double, string>(seconds, _lastAnyMessage));
                    if (_silences.Count > 24)
                    {
                        _silences.Sort((a, b) => b.Key.CompareTo(a.Key));
                        _silences.RemoveRange(12, _silences.Count - 12);
                    }
                }
            }
            _lastAnyTicks = now;
            _lastAnyMessage = message.Length > 160 ? message.Substring(0, 160) : message;
        }

        // A long silence after an asset line is usually other loader work (part compilation of another phase, drag cubes,
        // bundles...), not that asset: only the first MaxItemSeconds are charged to it, the rest goes to "unattributed".
        private const double MaxItemSeconds = 1.0;
        private long _unattributedTicks;

        private void ClosePending(long now)
        {
            if (_pendingMod == null)
                return;
            long elapsed = now - _pendingStart;
            long cap = (long)(MaxItemSeconds * Stopwatch.Frequency);
            if (elapsed <= cap)
                _frames[(int)_pendingCategory] += Time.frameCount - _pendingFrame;   // frames of a long gap belong to it, not to the asset
            if (elapsed > cap)
            {
                _gaps.Add(new KeyValuePair<double, string>((double)elapsed / Stopwatch.Frequency, _pendingMessage));
                _unattributedTicks += elapsed - cap;
                elapsed = cap;
            }
            _pendingMod.Ticks[(int)_pendingCategory] += elapsed;
            _pendingMod.Total += elapsed;
            _pendingMod = null;
        }

        private void Report()
        {
            double tickSeconds = 1.0 / Stopwatch.Frequency;
            var ranked = new List<KeyValuePair<string, ModStats>>(_mods);
            ranked.Sort((a, b) => b.Value.Total.CompareTo(a.Value.Total));

            long all = 0;
            var totals = new long[(int)Category.Count];
            var counts = new int[(int)Category.Count];
            foreach (KeyValuePair<string, ModStats> pair in ranked)
            {
                all += pair.Value.Total;
                for (int c = 0; c < (int)Category.Count; c++)
                {
                    totals[c] += pair.Value.Ticks[c];
                    counts[c] += pair.Value.Count[c];
                }
            }

            var sb = new StringBuilder(4096);
            sb.Append("===== LoadReport: ").Append((all * tickSeconds).ToString("F1")).Append(" s attributed to ").Append(ranked.Count)
              .Append(" GameData folders (of ").Append(Time.realtimeSinceStartup.ToString("F1")).Append(" s total; ")
              .Append((_unattributedTicks * tickSeconds).ToString("F1")).AppendLine(" s in long gaps not charged to any asset) =====");
            sb.Append("By category:");
            for (int c = 0; c < (int)Category.Count; c++)
                sb.Append(' ').Append(CategoryNames[c]).Append(' ').Append((totals[c] * tickSeconds).ToString("F1")).Append(" s (").Append(counts[c]).Append(", ").Append(_frames[c]).Append(" frames)");
            sb.AppendLine();
            sb.AppendLine("By folder (seconds: total | textures | models | audio | configs | parts):");
            for (int i = 0; i < ranked.Count && i < _reportTop; i++)
            {
                ModStats s = ranked[i].Value;
                sb.Append("  ").Append(ranked[i].Key).Append(": ").Append((s.Total * tickSeconds).ToString("F2"));
                for (int c = 0; c < (int)Category.Count; c++)
                    sb.Append(" | ").Append((s.Ticks[c] * tickSeconds).ToString("F2")).Append(" (").Append(s.Count[c]).Append(')');
                sb.AppendLine();
            }
            if (_gaps.Count > 0)
            {
                _gaps.Sort((a, b) => b.Key.CompareTo(a.Key));
                sb.AppendLine("Longest gaps after an asset line (seconds, last line before the gap):");
                for (int i = 0; i < _gaps.Count && i < 12; i++)
                    sb.Append("  ").Append(_gaps[i].Key.ToString("F2")).Append(" after: ").AppendLine(_gaps[i].Value);
            }
            if (_silences.Count > 0)
            {
                _silences.Sort((a, b) => b.Key.CompareTo(a.Key));
                sb.AppendLine("Longest silences between any two log lines (seconds, line before):");
                for (int i = 0; i < _silences.Count && i < 12; i++)
                    sb.Append("  ").Append(_silences[i].Key.ToString("F2")).Append(" after: ").AppendLine(_silences[i].Value);
            }
            if (_stalls.Count > 0)
            {
                _stalls.Sort((a, b) => b.Key.CompareTo(a.Key));
                sb.Append("Frozen frames over 0.4 s (").Append(_framesOver100ms).AppendLine(" frames over 100 ms in total; seconds, last log line):");
                for (int i = 0; i < _stalls.Count && i < 12; i++)
                    sb.Append("  ").Append(_stalls[i].Key.ToString("F2")).Append(" after: ").AppendLine(_stalls[i].Value);
            }
            Log.Info(sb.ToString());
            _mods.Clear();
            _gaps.Clear();
            _silences.Clear();
            _stalls.Clear();
        }
    }
}
