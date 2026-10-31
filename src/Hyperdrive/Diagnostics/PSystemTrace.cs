using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Hyperdrive.Diagnostics
{
    /// <summary>
    /// Developer tool (off by default): where do the long frames at the end of loading go? Stopwatches on the stock
    /// planetary system build (PSystemManager / PSystemSetup, PQS, celestial bodies, KSC, main menu) plus optional
    /// TARGET { type = ... method = ... } nodes. Every call is recorded with its frame, inclusive and exclusive time
    /// (nested targets subtracted), so a frozen frame can be split into steps. Coroutine methods are timed per
    /// resumption (MoveNext). One report at the main menu, then all patches are removed. Read-only.
    /// Flight window option eventHandlers: every GameEvents subscriber is timed as well (who takes the time in Fire).
    /// </summary>
    internal sealed class PSystemTrace : Subsystem
    {
        private static readonly string[][] BuiltInTargets =
        {
            new[] { "PSystemManager", "Awake", "Start", "SetupPSystem", "SpawnBody", "PostSetup" },
            new[] { "PSystemSetup", "Awake", "Start", "SetupSystem", "SetupFacilities", "SetupLaunchSites", "SpawnAdditionalPrefabs",
                    "OnLevelLoaded", "OnSceneChange", "SetMainMenu", "SetSpaceCentre", "ForceInitPQS", "SetPQSInactive",
                    "SetPQSActive", "SetPQSDisabled", "ResetTransforms" },
            new[] { "Planetarium", "Awake", "UpdateCBs" },
            new[] { "CelestialBody", "Awake", "Start", "SetupConstants" },
            new[] { "PQS", "Start", "ForceStart", "StartUpSphere", "ResetSphere", "RebuildSphere", "ActivateSphere", "SetupMods" },
            new[] { "AtmosphereFromGround", "Start" },
            new[] { "ScaledSpaceFader", "Start" },
            new[] { "PQSCity", "Setup", "Start", "OnSetup", "OnPostSetup" },
            new[] { "PQSCity2", "Setup", "Awake", "Start", "OnSetup", "OnPostSetup" },
            new[] { "SpaceCenter", "Awake", "Start" },
            new[] { "MainMenu", "Awake", "Start" },
            new[] { "MainMenuEnvLogic", "Awake" },
        };

        private struct Span
        {
            public MethodBase Method;
            public string Detail;
            public int Frame;
            public int Depth;
            public long Start;
            public long Total;
            public long Exclusive;
        }

        private static int s_maxSpans = 1 << 18;   // maxCalls: 48 bytes each
        private const int MaxDepth = 128;
        private const int MaxFrames = 32768;
        private static readonly string[] LifecycleMethods = { "Awake", "Start", "OnEnable" };

        // Accessed from the patches and OnUpdate (main thread only).
        private static Span[] s_spans;
        private static int s_spanCount;
        private static readonly long[] s_childTicks = new long[MaxDepth];
        private static int s_depth;
        private static bool s_recording;
        private static int s_firstFrame = -1;
        private static int s_fullAtFrame = -1;      // first frame a call was dropped (buffer full)

        private long[] _frameEnd;
        private long _traceStart;           // when recording began: the first interval runs from here
        private int _gcStart;
        private int[] _frameGc;
        private int _reportAtFrame = -1;
        private int _reportTop = 40;
        private float _longFrameMs = 50f;
        private ConfigNode[] _extraTargets = new ConfigNode[0];
        private bool _lifecycle;
        private bool _eventHandlers, _eventHandlersPatched;
        private bool _fromDatabaseLoaded = true;
        // window = flight: nothing is recorded while loading; AutoBench opens a window of windowSeconds in flight
        // (explosions), reported at its end. window = revert / spacecentre: the same, opened when an AutoBench cycle reverts
        // to launch or goes back to the space centre. window = launch: the same window, opened when AutoBench requests the launch
        // (the space centre to flight scene change).
        private bool _flightWindow;
        private string _windowTrigger = "flight";   // flight (explosion, crash), launch, revert, spacecentre
        private float _windowSeconds = 30f;
        private float _windowEnd = -1f;
        private static PSystemTrace s_instance;
        private readonly List<string> _sceneEvents = new List<string>();

        public override string Name => "PSystemTrace";
        public override bool ReadOnly => true;

        public override bool EnabledByDefault => false;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _reportTop = Mathf.Clamp(settings.GetInt("reportTop", _reportTop), 5, 500);
            _longFrameMs = Mathf.Clamp(settings.GetFloat("longFrameMs", _longFrameMs), 10f, 10000f);
            _extraTargets = settings.GetNodes("TARGET");
            _lifecycle = settings.GetBool("lifecycle", _lifecycle);
            _eventHandlers = settings.GetBool("eventHandlers", _eventHandlers);
            _fromDatabaseLoaded = settings.GetBool("fromDatabaseLoaded", _fromDatabaseLoaded);
            string window = settings.GetString("window", "loading").Trim().ToLowerInvariant();
            _flightWindow = window == "flight" || window == "launch" || window == "revert" || window == "spacecentre" || window == "editor";
            _windowTrigger = _flightWindow ? window : "flight";
            _windowSeconds = Mathf.Clamp(settings.GetFloat("windowSeconds", _windowSeconds), 2f, 600f);
            s_maxSpans = Mathf.Clamp(settings.GetInt("maxCalls", s_maxSpans), 1 << 16, 1 << 23);
        }

        protected internal override void OnEnable()
        {
            s_spans = new Span[s_maxSpans];
            s_spanCount = 0;
            s_fullAtFrame = -1;
            _frameEnd = new long[MaxFrames];
            _frameGc = new int[MaxFrames];
            s_depth = 0;
            s_firstFrame = -1;

            var methods = new List<MethodBase>();
            foreach (string[] target in BuiltInTargets)
                for (int i = 1; i < target.Length; i++)
                    AddTarget(methods, target[0], target[i], false);
            foreach (ConfigNode node in _extraTargets)
                AddTarget(methods, node.GetValue("type"), node.GetValue("method"), true);

            MethodInfo spawnBody = AccessTools.Method("PSystemManager:SpawnBody");
            foreach (MethodBase method in methods)
            {
                if (method == spawnBody)
                    Patch(method, prefix: nameof(SpawnBodyPrefix), postfix: nameof(TimePostfix));
                else
                    Patch(method, prefix: nameof(TimePrefix), postfix: nameof(TimePostfix));
            }

            int lifecycle = 0, lifecycleFailed = 0;
            if (_lifecycle)
                PatchLifecycle(methods, ref lifecycle, ref lifecycleFailed);

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            s_instance = this;
            if (_flightWindow)
            {
                // Recording starts with BeginFlightWindow.
            }
            else if (_fromDatabaseLoaded)
                GameEvents.OnGameDatabaseLoaded.Add(StartRecording);
            else
            {
                s_recording = true;
                _traceStart = Stopwatch.GetTimestamp();
                _gcStart = GC.CollectionCount(0);
            }
            Log.Info("PSystemTrace: timing " + methods.Count + " methods" + (_lifecycle ? " + " + lifecycle + " Awake/Start/OnEnable (" + lifecycleFailed + " not patchable)" : "")
                     + (_flightWindow ? " during a " + _windowSeconds + " s window opened in flight by AutoBench"
                        : (_fromDatabaseLoaded ? " from the end of GameDatabase loading" : "") + " until the main menu"));
        }

        protected internal override void OnDisable()
        {
            s_recording = false;
            GameEvents.OnGameDatabaseLoaded.Remove(StartRecording);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        }

        /// <summary>AutoBench: opens the flight window (window = flight), once.</summary>
        internal static void BeginFlightWindow(string trigger = "flight")
        {
            PSystemTrace self = s_instance;
            if (self == null || !self._flightWindow || self._windowTrigger != trigger || self._windowEnd >= 0f)
                return;
            s_spanCount = 0;
            s_fullAtFrame = -1;
            s_depth = 0;
            Array.Clear(self._frameEnd, 0, self._frameEnd.Length);
            Array.Clear(self._frameGc, 0, self._frameGc.Length);
            self._sceneEvents.Clear();
            self._traceStart = Stopwatch.GetTimestamp();
            self._gcStart = GC.CollectionCount(0);
            s_firstFrame = Time.frameCount;
            self._windowEnd = Time.realtimeSinceStartup + self._windowSeconds;
            s_recording = true;
            Log.Info("PSystemTrace: flight window opened for " + self._windowSeconds + " s");
        }

        private void StartRecording()
        {
            GameEvents.OnGameDatabaseLoaded.Remove(StartRecording);
            s_recording = true;
            _traceStart = Stopwatch.GetTimestamp();
            _gcStart = GC.CollectionCount(0);
        }

        // Awake/Start/OnEnable of every MonoBehaviour of the game and the mods, except part and IVA modules (thousands
        // of instances while parts compile). Patched one by one: a method Harmony cannot patch is just counted.
        private void PatchLifecycle(List<MethodBase> already, ref int patched, ref int failed)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(PSystemTrace), nameof(TimePrefix)));
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(PSystemTrace), nameof(TimePostfix)));
            var seen = new HashSet<Assembly>();
            var assemblies = new List<Assembly> { typeof(Part).Assembly };
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
                if (loaded?.assembly != null)
                    assemblies.Add(loaded.assembly);
            foreach (Assembly assembly in assemblies)
            {
                if (!seen.Add(assembly) || assembly == typeof(PSystemTrace).Assembly || assembly == typeof(Harmony).Assembly)
                    continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }
                foreach (Type type in types)
                {
                    if (type == null || type.ContainsGenericParameters || !type.IsSubclassOf(typeof(MonoBehaviour))
                        || type.IsSubclassOf(typeof(PartModule)) || type.IsSubclassOf(typeof(InternalModule)))
                        continue;
                    foreach (string name in LifecycleMethods)
                    {
                        MethodInfo method;
                        try { method = type.GetMethod(name, flags, null, Type.EmptyTypes, null); }
                        catch (Exception) { continue; }
                        if (method == null || method.IsAbstract || method.GetMethodBody() == null)
                            continue;
                        MethodBase target = method.ReturnType == typeof(IEnumerator) ? AccessTools.EnumeratorMoveNext(method) : method;
                        if (target == null || already.Contains(target))
                            continue;
                        try
                        {
                            Harmony.Patch(target, prefix: prefix, postfix: postfix);
                            patched++;
                        }
                        catch (Exception)
                        {
                            failed++;
                        }
                    }
                }
            }
        }

        // Resolves every overload named methodName; an IEnumerator method is replaced by its MoveNext.
        private void AddTarget(List<MethodBase> methods, string typeName, string methodName, bool warn)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            Type type = string.IsNullOrEmpty(typeName) ? null : AccessTools.TypeByName(typeName);
            int found = 0;
            if (type != null && methodName == ".ctor")
            {
                foreach (ConstructorInfo ctor in type.GetConstructors(flags))
                {
                    if (ctor.IsStatic || ctor.GetMethodBody() == null || methods.Contains(ctor))
                        continue;
                    methods.Add(ctor);
                    found++;
                }
            }
            else if (type != null && !string.IsNullOrEmpty(methodName))
            {
                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    if (method.Name != methodName || method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null)
                        continue;
                    MethodBase target = method.ReturnType == typeof(IEnumerator) ? AccessTools.EnumeratorMoveNext(method) : method;
                    if (target == null || methods.Contains(target))
                        continue;
                    methods.Add(target);
                    found++;
                }
            }
            if (found == 0 && warn)
                Log.Warn("PSystemTrace: TARGET not found: " + typeName + "." + methodName);
            else if (found == 0)
                Log.Debug("PSystemTrace: no method " + typeName + "." + methodName + " in this game version");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => AddSceneEvent("loaded " + scene.name + " (" + mode + ")");

        private void OnActiveSceneChanged(Scene from, Scene to) => AddSceneEvent("active " + from.name + " -> " + to.name);

        private void AddSceneEvent(string what)
        {
            if (s_recording)
                _sceneEvents.Add("frame " + Time.frameCount + " +" + Ms(Stopwatch.GetTimestamp() - SpanOrigin()) + " ms: " + what);
        }

        // Frame boundaries (one per OnUpdate) from the first recorded call; the report waits a few frames after the menu.
        protected internal override void OnUpdate()
        {
            if (!s_recording || s_firstFrame < 0)
                return;
            int index = Time.frameCount - s_firstFrame;
            if (index >= 0 && index < MaxFrames)
            {
                _frameEnd[index] = Stopwatch.GetTimestamp();
                _frameGc[index] = GC.CollectionCount(0);
            }
            if (_flightWindow && _windowEnd > 0f && Time.realtimeSinceStartup >= _windowEnd && _reportAtFrame < 0)
                _reportAtFrame = Time.frameCount;
            if (_reportAtFrame > 0 && Time.frameCount >= _reportAtFrame)
            {
                s_recording = false;
                Report(Mathf.Min(index + 1, MaxFrames));
                Harmony.UnpatchAll(HarmonyId);
            }
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU && _reportAtFrame < 0 && !_flightWindow)
                _reportAtFrame = Time.frameCount + 120;
            if (scene == GameScenes.FLIGHT && _flightWindow && _eventHandlers && !_eventHandlersPatched)
            {
                _eventHandlersPatched = true;
                PatchEventHandlers();
            }
        }

        // eventHandlers (flight window): every method subscribed to a GameEvents event when the flight scene is ready
        // (stock and mods alike) is timed, so a frame spent inside GameEvents.X.Fire is split by subscriber.
        private void PatchEventHandlers()
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(PSystemTrace), nameof(TimePrefix)));
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(PSystemTrace), nameof(TimePostfix)));
            var done = new HashSet<MethodBase>();
            var holders = new List<Type> { typeof(GameEvents) };
            holders.AddRange(typeof(GameEvents).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            int events = 0, patched = 0, failed = 0;
            foreach (Type holder in holders)
                foreach (FieldInfo field in holder.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!typeof(BaseGameEvent).IsAssignableFrom(field.FieldType) || !(field.GetValue(null) is BaseGameEvent ev))
                        continue;
                    FieldInfo list = ev.GetType().GetField("events", any);
                    if (!(list?.GetValue(ev) is IList delegates) || delegates.Count == 0)
                        continue;
                    events++;
                    foreach (object entry in delegates)
                    {
                        Delegate handler = entry?.GetType().GetField("evt", any)?.GetValue(entry) as Delegate;
                        MethodInfo method = handler?.Method;
                        if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.DeclaringType == typeof(PSystemTrace)
                            || method.GetMethodBody() == null || !done.Add(method))
                            continue;
                        try
                        {
                            Harmony.Patch(method, prefix: prefix, postfix: postfix);
                            patched++;
                        }
                        catch (Exception)
                        {
                            failed++;
                        }
                    }
                }
            Log.Info("PSystemTrace: " + patched + " GameEvents subscribers timed (" + events + " events with subscribers, " + failed + " not patchable)");
        }

        // ---- patches: allocation-free ----

        private static void TimePrefix(MethodBase __originalMethod, out int __state) => __state = Begin(__originalMethod, null);

        private static void SpawnBodyPrefix(MethodBase __originalMethod, PSystemBody body, out int __state)
            => __state = Begin(__originalMethod, body != null ? body.name : null);

        private static int Begin(MethodBase method, string detail)
        {
            if (!s_recording || s_depth >= MaxDepth)
                return -1;
            if (s_spanCount >= s_maxSpans)
            {
                if (s_fullAtFrame < 0)
                    s_fullAtFrame = Time.frameCount;
                return -1;
            }
            if (s_firstFrame < 0)
                s_firstFrame = Time.frameCount;
            int index = s_spanCount++;
            ref Span span = ref s_spans[index];
            span.Method = method;
            span.Detail = detail;
            span.Frame = Time.frameCount;
            span.Depth = s_depth;
            s_childTicks[s_depth] = 0;
            s_depth++;
            span.Start = Stopwatch.GetTimestamp();
            return index;
        }

        private static void TimePostfix(int __state)
        {
            if (__state < 0 || s_spans == null)
                return;
            long now = Stopwatch.GetTimestamp();
            ref Span span = ref s_spans[__state];
            span.Total = now - span.Start;
            s_depth = span.Depth;
            span.Exclusive = span.Total - s_childTicks[span.Depth];
            if (span.Depth > 0)
                s_childTicks[span.Depth - 1] += span.Total;
        }

        // ---- report ----

        private static long SpanOrigin() => s_spanCount > 0 ? s_spans[0].Start : Stopwatch.GetTimestamp();

        private static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F1");

        private static string Label(ref Span span) => MethodLabel(span.Method) + (span.Detail != null ? "(" + span.Detail + ")" : "");

        // "Type.Method [Assembly]"; iterator steps are shown under their coroutine's name, the stock game without suffix.
        private static string MethodLabel(MethodBase method)
        {
            Type type = method.DeclaringType;
            bool iterator = method.Name == "MoveNext";
            Type owner = iterator && type?.DeclaringType != null ? type.DeclaringType : type;
            string assembly = owner?.Assembly.GetName().Name;
            return owner?.Name + "." + (iterator ? IteratorName(method) : method.Name)
                   + (assembly == null || assembly == "Assembly-CSharp" ? "" : " [" + assembly + "]");
        }

        // "<SetupSystem>d__55" -> "SetupSystem[step]"
        private static string IteratorName(MethodBase moveNext)
        {
            string name = moveNext.DeclaringType?.Name ?? "MoveNext";
            int open = name.IndexOf('<'), close = name.IndexOf('>');
            return open >= 0 && close > open ? name.Substring(open + 1, close - open - 1) + "[step]" : name;
        }

        private void Report(int frames)
        {
            var sb = new StringBuilder(8192);
            sb.Append("===== PSystemTrace: ").AppendInt(s_spanCount).Append(" calls over ").AppendInt(frames)
              .Append(" frames from frame ").AppendInt(s_firstFrame).AppendLine(" =====");
            if (s_fullAtFrame >= 0)
                sb.Append("  BUFFER FULL at frame ").AppendInt(s_fullAtFrame).Append(": later calls not recorded (raise maxCalls, now ")
                  .AppendInt(s_maxSpans).AppendLine(", or drop per-part targets)");
            foreach (string e in _sceneEvents)
                sb.Append("  scene ").AppendLine(e);

            // Long frames: duration, GC cycles, time covered by top-level targets, and the heaviest steps inside.
            long longTicks = (long)(_longFrameMs / 1000.0 * Stopwatch.Frequency);
            // A frame without an Update of ours (the first ones: the host updates from the frame after it starts) is
            // merged into the next interval, so the work done before the first Update is not lost.
            // Spans are stored in call order, so their start times only grow: one cursor walks them along the frames.
            long previous = _traceStart;
            int previousGc = _gcStart;
            int cursor = 0;
            var inFrame = new List<int>();
            for (int f = 0; f < frames; f++)
            {
                if (_frameEnd[f] == 0)
                    continue;
                long begin = previous;
                int gcBegin = previousGc;
                previous = _frameEnd[f];
                previousGc = _frameGc[f];
                long duration = _frameEnd[f] - begin;
                if (begin == 0 || duration < longTicks)
                    continue;
                // Calls are attributed by time, not by frame number: coroutines resume after the Update that ends a
                // frame here, so their work falls in the next interval.
                int frame = s_firstFrame + f;
                long covered = 0;
                inFrame.Clear();
                while (cursor < s_spanCount && s_spans[cursor].Start <= begin)
                    cursor++;
                for (int i = cursor; i < s_spanCount && s_spans[i].Start <= _frameEnd[f]; i++)
                {
                    inFrame.Add(i);
                    if (s_spans[i].Depth == 0)
                        covered += s_spans[i].Total;
                }
                inFrame.Sort((a, b) => s_spans[b].Exclusive.CompareTo(s_spans[a].Exclusive));
                sb.Append("Interval before frame ").AppendInt(frame).Append(" update: ").Append(Ms(duration)).Append(" ms, GC +")
                  .AppendInt(_frameGc[f] - gcBegin).Append(", traced ").Append(Ms(covered)).Append(" ms, untraced ")
                  .Append(Ms(duration - covered)).AppendLine(" ms");
                for (int k = 0; k < inFrame.Count && k < 12; k++)
                {
                    ref Span s = ref s_spans[inFrame[k]];
                    if (s.Exclusive * 1000 < Stopwatch.Frequency)
                        break;
                    sb.Append("    ").Append(Label(ref s)).Append(": self ").Append(Ms(s.Exclusive)).Append(" ms, total ")
                      .Append(Ms(s.Total)).Append(" ms, depth ").AppendInt(s.Depth).AppendLine();
                }
            }

            // Totals per method (self time), then per call for the heaviest single calls. Each span's enclosing call, from
            // one pass with a stack (call order), finds recursion without walking back over siblings.
            int[] parent = new int[s_spanCount];
            int[] open = new int[MaxDepth + 1];
            int openCount = 0;
            var byMethod = new Dictionary<MethodBase, long[]>();
            for (int i = 0; i < s_spanCount; i++)
            {
                ref Span s = ref s_spans[i];
                while (openCount > 0 && s_spans[open[openCount - 1]].Depth >= s.Depth)
                    openCount--;
                parent[i] = openCount > 0 ? open[openCount - 1] : -1;
                if (openCount < open.Length)
                    open[openCount++] = i;
                if (s.Method == null)
                    continue;
                if (!byMethod.TryGetValue(s.Method, out long[] t))
                    byMethod[s.Method] = t = new long[3];
                t[0]++;
                t[1] += s.Exclusive;
                if (!SameMethodAbove(i, parent))
                    t[2] += s.Total;
            }
            var methods = new List<KeyValuePair<string, long[]>>(byMethod.Count);
            var merged = new Dictionary<string, long[]>();   // methods with the same label (overloads) are added up
            foreach (KeyValuePair<MethodBase, long[]> entry in byMethod)
            {
                string key = MethodLabel(entry.Key);
                if (merged.TryGetValue(key, out long[] sum))
                {
                    sum[0] += entry.Value[0];
                    sum[1] += entry.Value[1];
                    sum[2] += entry.Value[2];
                }
                else
                    merged[key] = (long[])entry.Value.Clone();
            }
            methods.AddRange(merged);
            // Self time per assembly: the stock game, each mod (who takes the time, whatever the method).
            var byAssembly = new Dictionary<string, long[]>();
            foreach (KeyValuePair<MethodBase, long[]> entry in byMethod)
            {
                Type type = entry.Key.DeclaringType;
                string assembly = type?.Assembly.GetName().Name ?? "?";
                if (!byAssembly.TryGetValue(assembly, out long[] sum))
                    byAssembly[assembly] = sum = new long[2];
                sum[0] += entry.Value[0];
                sum[1] += entry.Value[1];
            }
            var assemblies = new List<KeyValuePair<string, long[]>>(byAssembly);
            assemblies.Sort((a, b) => b.Value[1].CompareTo(a.Value[1]));
            sb.AppendLine("By assembly (self time of the traced methods, calls):");
            for (int k = 0; k < assemblies.Count && k < _reportTop; k++)
                sb.Append("  ").Append(assemblies[k].Key).Append(": ").Append(Ms(assemblies[k].Value[1])).Append(" ms, ")
                  .AppendInt(assemblies[k].Value[0]).AppendLine(" calls");

            methods.Sort((a, b) => b.Value[1].CompareTo(a.Value[1]));
            sb.AppendLine("By method (self time, total time without recursion, calls):");
            for (int k = 0; k < methods.Count && k < _reportTop; k++)
                sb.Append("  ").Append(methods[k].Key).Append(": self ").Append(Ms(methods[k].Value[1])).Append(" ms, total ")
                  .Append(Ms(methods[k].Value[2])).Append(" ms, ").AppendInt(methods[k].Value[0]).AppendLine(" calls");

            // The heaviest calls, kept sorted in a small array (no sort of every span).
            int[] top = new int[_reportTop];
            int topCount = 0;
            for (int i = 0; i < s_spanCount; i++)
            {
                long self = s_spans[i].Exclusive;
                if (topCount == top.Length && self <= s_spans[top[topCount - 1]].Exclusive)
                    continue;
                int at = topCount < top.Length ? topCount++ : topCount - 1;
                while (at > 0 && s_spans[top[at - 1]].Exclusive < self)
                {
                    top[at] = top[at - 1];
                    at--;
                }
                top[at] = i;
            }
            sb.AppendLine("Heaviest single calls (self time):");
            for (int k = 0; k < topCount; k++)
            {
                ref Span s = ref s_spans[top[k]];
                sb.Append("  frame ").AppendInt(s.Frame).Append(' ').Append(Label(ref s)).Append(": self ").Append(Ms(s.Exclusive))
                  .Append(" ms, total ").Append(Ms(s.Total)).AppendLine(" ms");
            }
            Log.Info(sb.ToString());
            s_spans = null;
            s_spanCount = 0;
        }

        // True if an enclosing open call of the same method exists (recursion: its total already includes this one).
        private static bool SameMethodAbove(int index, int[] parent)
        {
            MethodBase method = s_spans[index].Method;
            for (int i = parent[index]; i >= 0; i = parent[i])
                if (s_spans[i].Method == method)
                    return true;
            return false;
        }
    }
}
