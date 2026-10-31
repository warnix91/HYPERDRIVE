using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Memory;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Throttle
{
    /// <summary>
    /// On-demand profiler of per-frame Unity callbacks (Update, LateUpdate, FixedUpdate, OnGUI) of mods and, with
    /// includeStock, of the stock game. Press the hotkey (Alt+F11 by default): every such method gets a prefix/postfix
    /// for a few seconds that measures its time AND the managed memory it allocates, then two rankings are written to
    /// KSP.log and all profiling patches are removed. Zero cost when not sampling.
    /// Allocation figures come from the Boehm GC counter of bytes allocated since the last collection: it advances in
    /// thread-local chunks, so a single call is coarse, but totals over thousands of calls rank allocators reliably.
    /// Samples spanning a completed GC cycle (counter reset) are discarded.
    /// </summary>
    internal sealed class ModProfiler : Subsystem
    {
        private static readonly string[] Callbacks = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };

        private sealed class Stat
        {
            public MethodBase Method;
            public long Ticks;
            public long MaxTicks;
            public long Bytes;
            public long MaxBytes;
            public int Calls;
        }

        private struct Sample
        {
            public long Ticks;
            public long Bytes;
            public long Gc;
        }

        // Accessed from the patches (main thread only: Unity callbacks run on the main thread).
        private static Dictionary<MethodBase, Stat> s_stats;
        private static bool s_sampling;
        private static bool s_measureBytes;

        private KeyCode _key = KeyCode.F11;
        private bool _needsAlt = true;
        private float _sampleSeconds = 15f;
        private int _reportTop = 20;
        private bool _profileOnSceneReady;
        private bool _includeStock;
        private bool _moreCallbacks, _coroutines;
        private ConfigNode[] _extraTargets = new ConfigNode[0];

        private float _sampleEnd;
        private int _frames;
        private string _lastSceneSampled;
        private long _frameBytesTotal, _lastBytesSinceGc, _lastGcNo;
        private float _sampleStart;

        public static string LastReportSummary { get; private set; } = "";

        // For the Hyperdrive window's performance tab: the last ranking per owner (ms per frame), main thread only.
        internal static ModProfiler Instance { get; private set; }
        internal static List<KeyValuePair<string, double>> LastOwners { get; private set; }
        internal static double LastCallbacksMs { get; private set; }
        internal static double LastFrameMs { get; private set; }
        internal static int LastVersion { get; private set; }
        internal bool Sampling => s_sampling;
        internal float Remaining => s_sampling ? Mathf.Max(0f, (_warmupUntil > 0f ? _warmupUntil + _measureSeconds : _sampleEnd) - Time.unscaledTime) : 0f;

        /// <summary>One sample started from the window: the given length, mods and stock game, then the usual report.</summary>
        internal void BeginSampling(float seconds, bool includeStock)
        {
            if (s_sampling)
                return;
            _sampledStock = includeStock;
            float configured = _sampleSeconds;
            bool stock = _includeStock;
            _sampleSeconds = seconds;
            _includeStock = includeStock;
            try { StartSampling(); }
            finally
            {
                _sampleSeconds = configured;
                _includeStock = stock;
            }
        }

        private bool _sampledStock;
        private float _warmupUntil, _measureSeconds;
        private const float WarmupSeconds = 1.5f;

        public override string Name => "ModProfiler";
        public override bool ReadOnly => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _needsAlt = settings.GetBool("toggleWithAlt", true);
            _sampleSeconds = Mathf.Clamp(settings.GetFloat("sampleSeconds", _sampleSeconds), 2f, 300f);
            _reportTop = Mathf.Clamp(settings.GetInt("reportTop", _reportTop), 5, 200);
            _profileOnSceneReady = settings.GetBool("profileOnFlightStart", false);
            _includeStock = settings.GetBool("includeStock", false);
            _moreCallbacks = settings.GetBool("physicsAndRenderCallbacks", false);
            _coroutines = settings.GetBool("coroutines", false);
            _extraTargets = settings.GetNodes("TARGET");
            string key = settings.GetString("toggleKey", null);
            if (!string.IsNullOrEmpty(key))
            {
                try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), key, true); }
                catch (Exception) { Log.Warn("ModProfiler: unknown toggleKey '" + key + "', using F11"); }
            }
        }

        protected internal override void OnEnable()
        {
            Instance = this;
            BoehmGC.TryInitialize();
            Log.Info("ModProfiler ready: press " + (_needsAlt ? "Alt+" : "") + _key + " to profile " + (_includeStock ? "mods + stock game" : "mods")
                     + " for " + _sampleSeconds + " s" + (BoehmGC.Available ? " (time + allocations)" : " (time only: native GC counter unavailable)"));
        }

        protected internal override void OnDisable()
        {
            if (Instance == this)
                Instance = null;
            s_sampling = false;
            s_stats = null;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (_profileOnSceneReady && scene == GameScenes.FLIGHT && !s_sampling && _lastSceneSampled != "FLIGHT")
            {
                _lastSceneSampled = "FLIGHT";
                _sampledStock = false;
                StartSampling();
            }
        }

        protected internal override void OnUpdate()
        {
            if (s_sampling && _warmupUntil > 0f)
            {
                // The first calls of freshly patched methods are compiled: what they cost is not the game's.
                if (Time.unscaledTime < _warmupUntil)
                    return;
                _warmupUntil = 0f;
                foreach (Stat stat in s_stats.Values)
                {
                    stat.Ticks = stat.MaxTicks = stat.Bytes = stat.MaxBytes = 0;
                    stat.Calls = 0;
                }
                _frames = 0;
                _frameBytesTotal = 0;
                _lastBytesSinceGc = BoehmGC.BytesSinceGC;
                _lastGcNo = BoehmGC.CollectionNumber;
                _sampleStart = Time.unscaledTime;
                _sampleEnd = _sampleStart + _measureSeconds;
                return;
            }
            if (s_sampling)
            {
                _frames++;
                TrackFrameBytes();
                if (Time.unscaledTime >= _sampleEnd)
                    StopSampling();
                return;
            }

            if (Input.GetKeyDown(_key) && (!_needsAlt || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
            {
                _sampledStock = false;
                StartSampling();
            }
        }

        // Whole-process allocation over the session, to show which share the patched callbacks explain.
        private void TrackFrameBytes()
        {
            if (!s_measureBytes)
                return;
            long now = BoehmGC.BytesSinceGC, gc = BoehmGC.CollectionNumber;
            _frameBytesTotal += gc == _lastGcNo && now >= _lastBytesSinceGc ? now - _lastBytesSinceGc : now;
            _lastBytesSinceGc = now;
            _lastGcNo = gc;
        }

        private void StartSampling()
        {
            var watch = Stopwatch.StartNew();
            List<MethodInfo> methods = ModAssemblies.FindCallbacks(Callbacks, _includeStock);
            if (_moreCallbacks || _coroutines)
            {
                foreach (MethodInfo method in ModAssemblies.FindMoreCallbacks(_includeStock, _coroutines))
                    if (_moreCallbacks || method.Name == "MoveNext")
                        methods.Add(method);
            }
            AddExtraTargets(methods);
            s_stats = new Dictionary<MethodBase, Stat>(methods.Count);
            s_measureBytes = BoehmGC.Available;
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(ModProfiler), nameof(ProfilePrefix)));
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(ModProfiler), nameof(ProfilePostfix)));

            int patched = 0, failed = 0;
            foreach (MethodInfo method in methods)
            {
                try
                {
                    s_stats[method] = new Stat { Method = method };
                    Harmony.Patch(method, prefix: prefix, postfix: postfix);
                    patched++;
                }
                catch (Exception e)
                {
                    failed++;
                    s_stats.Remove(method);
                    Log.Debug("ModProfiler: cannot patch " + ModAssemblies.ShortName(method) + ": " + e.Message);
                }
            }

            _frames = 0;
            _frameBytesTotal = 0;
            _lastBytesSinceGc = BoehmGC.BytesSinceGC;
            _lastGcNo = BoehmGC.CollectionNumber;
            _sampleStart = Time.unscaledTime;
            _measureSeconds = _sampleSeconds;
            _warmupUntil = _sampleStart + WarmupSeconds;
            _sampleEnd = _warmupUntil + _sampleSeconds;
            s_sampling = true;
            Log.Info("ModProfiler: sampling " + patched + " callbacks" + (failed > 0 ? " (" + failed + " skipped)" : "")
                     + (_includeStock ? " (mods + stock game)" : " (mods only)")
                     + " for " + _sampleSeconds + " s in " + HighLogic.LoadedScene + " (setup " + watch.ElapsedMilliseconds + " ms)");
        }

        // TARGET { type = FlightIntegrator  method = UpdateThermodynamics } nodes: any method (all overloads with that name)
        // can be profiled, e.g. the internals of a heavy callback. Nested targets are counted in their caller too.
        private void AddExtraTargets(List<MethodInfo> methods)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (ConfigNode node in _extraTargets)
            {
                string typeName = node.GetValue("type"), methodName = node.GetValue("method");
                Type type = string.IsNullOrEmpty(typeName) ? null : AccessTools.TypeByName(typeName);
                if (type == null || string.IsNullOrEmpty(methodName))
                {
                    Log.Warn("ModProfiler: TARGET not found: " + typeName + "." + methodName);
                    continue;
                }
                int found = 0;
                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    if (method.Name != methodName || method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null || methods.Contains(method))
                        continue;
                    methods.Add(method);
                    found++;
                }
                if (found == 0)
                    Log.Warn("ModProfiler: TARGET has no patchable method: " + typeName + "." + methodName);
            }
        }

        private void StopSampling()
        {
            s_sampling = false;
            float seconds = Mathf.Max(0.001f, Time.unscaledTime - _sampleStart);
            Harmony.UnpatchAll(HarmonyId);
            Dictionary<MethodBase, Stat> stats = s_stats;
            s_stats = null;
            if (stats != null)
                Report(stats, seconds);
        }

        private static string Owner(MethodBase method)
        {
            Assembly assembly = method.DeclaringType?.Assembly;
            return assembly != null && ModAssemblies.IsStock(assembly) ? "KSP" : ModAssemblies.ModName(method);
        }

        private void Report(Dictionary<MethodBase, Stat> stats, float seconds)
        {
            double tickMs = 1000.0 / Stopwatch.Frequency;
            int frames = Math.Max(1, _frames);

            var timeByOwner = new Dictionary<string, double>();
            var bytesByOwner = new Dictionary<string, long>();
            var list = new List<Stat>(stats.Count);
            double totalMs = 0;
            long totalBytes = 0;
            foreach (Stat stat in stats.Values)
            {
                if (stat.Calls == 0)
                    continue;
                list.Add(stat);
                double ms = stat.Ticks * tickMs;
                totalMs += ms;
                totalBytes += stat.Bytes;
                string owner = Owner(stat.Method);
                timeByOwner.TryGetValue(owner, out double t);
                timeByOwner[owner] = t + ms;
                bytesByOwner.TryGetValue(owner, out long b);
                bytesByOwner[owner] = b + stat.Bytes;
            }

            var sb = new StringBuilder(8192);
            sb.Append("===== Profiler report: ").Append(HighLogic.LoadedScene).Append(", ").Append(frames).Append(" frames, ")
              .Append(seconds.ToString("F1")).Append(" s, ").Append(_includeStock || _sampledStock ? "mods + stock" : "mods only")
              .Append(" | callbacks ").Append((totalMs / frames).ToString("F2")).Append(" ms/frame");
            if (s_measureBytes)
                sb.Append(" | allocated ").Append(Kb(totalBytes / (double)frames)).Append(" KB/frame by callbacks, ")
                  .Append(Kb(_frameBytesTotal / (double)frames)).Append(" KB/frame in total (")
                  .Append((_frameBytesTotal / (1024.0 * 1024.0) / seconds).ToString("F1")).Append(" MB/s, ")
                  .Append(_frameBytesTotal > 0 ? (100.0 * totalBytes / _frameBytesTotal).ToString("F0") : "0").Append("% explained)");
            sb.AppendLine(" =====");

            var owners = new List<KeyValuePair<string, double>>(timeByOwner);
            owners.Sort((a, b) => b.Value.CompareTo(a.Value));
            sb.AppendLine("Per owner (ms/frame | KB/frame):");
            foreach (KeyValuePair<string, double> owner in owners)
            {
                bytesByOwner.TryGetValue(owner.Key, out long b);
                sb.Append("  ").Append(owner.Key).Append(": ").Append((owner.Value / frames).ToString("F3"))
                  .Append(" | ").AppendLine(s_measureBytes ? Kb(b / (double)frames) : "-");
            }

            list.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
            sb.Append("Top ").Append(Math.Min(_reportTop, list.Count)).AppendLine(" by TIME (avg ms/frame | worst call ms | calls/frame | KB/frame):");
            AppendRows(sb, list, frames, tickMs);

            if (s_measureBytes)
            {
                list.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
                sb.Append("Top ").Append(Math.Min(_reportTop, list.Count)).AppendLine(" by ALLOCATION (avg ms/frame | worst call ms | calls/frame | KB/frame):");
                AppendRows(sb, list, frames, tickMs);
            }
            sb.Append("To throttle a visual-only Update, add a TARGET to ThrottleGuard in Hyperdrive.user.cfg (see Hyperdrive.cfg).");
            Log.Info(sb.ToString());

            LastOwners = owners.ConvertAll(o => new KeyValuePair<string, double>(o.Key, o.Value / frames));
            LastCallbacksMs = totalMs / frames;
            LastFrameMs = seconds * 1000.0 / frames;
            LastVersion++;
            LastReportSummary = owners.Count == 0 ? "no callbacks ran"
                : "top: " + owners[0].Key + " " + (owners[0].Value / frames).ToString("F2") + " ms/frame, all " + (totalMs / frames).ToString("F2")
                  + (s_measureBytes ? " | alloc " + (_frameBytesTotal / (1024.0 * 1024.0) / seconds).ToString("F1") + " MB/s" : "");
        }

        private void AppendRows(StringBuilder sb, List<Stat> list, int frames, double tickMs)
        {
            for (int i = 0; i < list.Count && i < _reportTop; i++)
            {
                Stat s = list[i];
                if (s.Ticks == 0 && s.Bytes == 0)
                    break;
                sb.Append("  ").Append(Owner(s.Method)).Append(' ').Append(s.Method.DeclaringType?.FullName).Append('.').Append(s.Method.Name)
                  .Append(": ").Append((s.Ticks * tickMs / frames).ToString("F3"))
                  .Append(" | ").Append((s.MaxTicks * tickMs).ToString("F2"))
                  .Append(" | ").Append(((double)s.Calls / frames).ToString("F1"))
                  .Append(" | ").AppendLine(s_measureBytes ? Kb(s.Bytes / (double)frames) : "-");
            }
        }

        private static string Kb(double bytes) => (bytes / 1024.0).ToString("F2");

        // ---- patches: allocation-free ----

        private static void ProfilePrefix(out Sample __state)
        {
            __state.Bytes = s_measureBytes ? BoehmGC.BytesSinceGC : 0;
            __state.Gc = s_measureBytes ? BoehmGC.CollectionNumber : 0;
            __state.Ticks = Stopwatch.GetTimestamp();
        }

        private static void ProfilePostfix(Sample __state, MethodBase __originalMethod)
        {
            long elapsed = Stopwatch.GetTimestamp() - __state.Ticks;
            Dictionary<MethodBase, Stat> stats = s_stats;
            if (!s_sampling || stats == null || !stats.TryGetValue(__originalMethod, out Stat stat))
                return;
            stat.Ticks += elapsed;
            stat.Calls++;
            if (elapsed > stat.MaxTicks)
                stat.MaxTicks = elapsed;

            if (s_measureBytes && BoehmGC.CollectionNumber == __state.Gc)
            {
                long bytes = BoehmGC.BytesSinceGC - __state.Bytes;
                if (bytes > 0)
                {
                    stat.Bytes += bytes;
                    if (bytes > stat.MaxBytes)
                        stat.MaxBytes = bytes;
                }
            }
        }
    }
}
