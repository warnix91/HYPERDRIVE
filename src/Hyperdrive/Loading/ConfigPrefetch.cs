using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// KSP builds its config tree twice at startup: once to find the plugins, then again, right after the plugins'
    /// "Instantly" addons (Hyperdrive among them), for the real database. The second build parses every .cfg of GameData
    /// again, one after the other on the main thread (~14,000 files in a big install), while the screen is frozen.
    /// As soon as Hyperdrive starts, worker threads parse every GameData .cfg with the stock parser (ConfigNode.Load:
    /// pure C#, no Unity API, no shared state), and the second build takes each result instead of reading the file.
    /// A result is used once, and only if the file's size and write time are unchanged since it was read; anything else
    /// goes through the stock path. Same parser, same input: the same ConfigNode tree. Only the first load is affected
    /// (an in-game database reload is stock). Stands down with KSPCF, which replaces the parser.
    /// </summary>
    internal sealed class ConfigPrefetch : Subsystem
    {
        private sealed class Slot
        {
            public string Path;
            public int State;          // 0 pending, 1 parsing, 2 parsed, 3 taken or left to the stock path
            public ConfigNode Node;
            public long Length;
            public long WriteTicks;
        }

        private const int Pending = 0, Parsing = 1, Parsed = 2, Closed = 3;

        // Published once by the coordinator thread, then read-only; read by the main thread and the workers.
        private static volatile Dictionary<string, Slot> s_index;
        private static Slot[] s_slots;
        private static readonly ManualResetEvent IndexReady = new ManualResetEvent(false);
        private static volatile bool s_active;
        private static int s_next, s_hits, s_misses, s_stale;
        private static long s_waitTicks;
        private static readonly Stopwatch Clock = new Stopwatch();
        private static long s_parsedAtTicks = -1;
        private static int s_workersLeft;
        private static WorkerLogFilter s_filter;

        // A file whose parse logs anything (malformed config) is left to the main thread, which parses it the stock way
        // and logs exactly as before: a log from a worker thread would skip Application.logMessageReceived listeners.
        [ThreadStatic] private static bool t_worker;
        [ThreadStatic] private static bool t_logged;

        private int _threads;

        public override string Name => "ConfigPrefetch";

        public override string[] IncompatibleAssemblies => new[] { "KSPCommunityFixes", "WarmStart" };

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _threads = settings.GetInt("threads", 0);
        }

        protected internal override void OnEnable()
        {
            if (GameDatabase.Instance == null || GameDatabase.Instance.IsReady())
                throw new SubsystemUnavailableException("the game database is already loaded");

            MethodInfo createNodeList = AccessTools.Method(typeof(UrlDir.UrlConfig), "CreateNodeList", new[] { typeof(UrlDir), typeof(UrlDir.UrlFile) });
            Patch(createNodeList, transpiler: nameof(CreateNodeListTranspiler));
            ConstructorInfo rootCtor = AccessTools.Constructor(typeof(UrlDir), new[] { typeof(UrlDir.ConfigDirectory[]), typeof(UrlDir.ConfigFileType[]) });
            Patch(rootCtor, postfix: nameof(RootBuilt));

            int threads = _threads > 0 ? _threads : Environment.ProcessorCount - 1;
            threads = Mathf.Clamp(threads, 1, 8);
            string gameData = Path.Combine(KSPUtil.ApplicationRootPath, "GameData");
            // Paths are compared as the file system does (case-insensitive on Windows and macOS).
            StringComparer comparer = Application.platform == RuntimePlatform.LinuxPlayer ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            s_filter = new WorkerLogFilter(UnityEngine.Debug.unityLogger.logHandler);
            UnityEngine.Debug.unityLogger.logHandler = s_filter;
            s_active = true;
            Clock.Start();
            var coordinator = new Thread(() => Run(gameData, comparer, threads)) { IsBackground = true, Name = "Hyperdrive.ConfigPrefetch" };
            coordinator.Start();
        }

        protected internal override void OnDisable() => Close();

        // ---- worker side ----

        private static void Run(string gameData, StringComparer comparer, int threads)
        {
            var slots = new List<Slot>(16384);
            var index = new Dictionary<string, Slot>(16384, comparer);
            try
            {
                Collect(new DirectoryInfo(gameData), slots, index);
            }
            catch (Exception)
            {
                // Unreadable tree: publish what was found; the rest goes through the stock path.
            }
            s_slots = slots.ToArray();
            s_index = index;
            IndexReady.Set();

            s_workersLeft = threads;
            for (int t = 0; t < threads; t++)
                new Thread(Work) { IsBackground = true, Name = "Hyperdrive.ConfigPrefetch." + t }.Start();
        }

        // Same traversal as UrlDir.Create: every file, minus folders named .svn, PluginData and zDeprecated.
        private static void Collect(DirectoryInfo dir, List<Slot> slots, Dictionary<string, Slot> index)
        {
            FileInfo[] files = dir.GetFiles();
            for (int i = 0; i < files.Length; i++)
            {
                if (Path.GetExtension(files[i].Name) != ".cfg")
                    continue;
                var slot = new Slot { Path = files[i].FullName };
                if (index.ContainsKey(slot.Path))
                    continue;
                index.Add(slot.Path, slot);
                slots.Add(slot);
            }
            DirectoryInfo[] children = dir.GetDirectories();
            for (int i = 0; i < children.Length; i++)
            {
                string name = children[i].Name;
                if (name == ".svn" || name == "PluginData" || name == "zDeprecated")
                    continue;
                Collect(children[i], slots, index);
            }
        }

        private static void Work()
        {
            t_worker = true;
            Slot[] slots = s_slots;
            while (s_active && slots != null)
            {
                int i = Interlocked.Increment(ref s_next) - 1;
                if (i >= slots.Length)
                    break;
                Slot slot = slots[i];
                if (Interlocked.CompareExchange(ref slot.State, Parsing, Pending) != Pending)
                    continue;
                int state = Closed;
                t_logged = false;
                try
                {
                    var before = new FileInfo(slot.Path);
                    long length = before.Length, ticks = before.LastWriteTimeUtc.Ticks;
                    ConfigNode node = ConfigNode.Load(slot.Path);
                    var after = new FileInfo(slot.Path);
                    if (!t_logged && after.Length == length && after.LastWriteTimeUtc.Ticks == ticks)
                    {
                        slot.Node = node;
                        slot.Length = length;
                        slot.WriteTicks = ticks;
                        state = Parsed;
                    }
                }
                catch (Exception)
                {
                    // Left to the stock path, which reports the error as usual.
                }
                Volatile.Write(ref slot.State, state);
            }
            if (Interlocked.Decrement(ref s_workersLeft) == 0)
                Interlocked.Exchange(ref s_parsedAtTicks, Clock.ElapsedTicks);
        }

        // ---- main thread side ----

        private static IEnumerable<CodeInstruction> CreateNodeListTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo stock = AccessTools.Method(typeof(ConfigNode), nameof(ConfigNode.Load), new[] { typeof(string) });
            MethodInfo ours = AccessTools.Method(typeof(ConfigPrefetch), nameof(Load));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(stock))
                {
                    instruction.operand = ours;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1)
                throw new InvalidOperationException("ConfigPrefetch: expected one ConfigNode.Load call in UrlConfig.CreateNodeList, found " + replaced);
        }

        // Replaces ConfigNode.Load(path) in UrlConfig.CreateNodeList.
        internal static ConfigNode Load(string path)
        {
            if (s_active && path != null)
            {
                if (s_index == null)
                {
                    long start = Clock.ElapsedTicks;
                    IndexReady.WaitOne(10000);
                    s_waitTicks += Clock.ElapsedTicks - start;
                }
                Dictionary<string, Slot> index = s_index;
                if (index != null && index.TryGetValue(path, out Slot slot))
                {
                    // Pending: this thread takes the file itself (stock path below). Parsing: wait for the worker.
                    if (Interlocked.CompareExchange(ref slot.State, Closed, Pending) != Pending)
                    {
                        if (Volatile.Read(ref slot.State) == Parsing)
                        {
                            long start = Clock.ElapsedTicks;
                            var spin = new SpinWait();
                            while (Volatile.Read(ref slot.State) == Parsing)
                                spin.SpinOnce();
                            s_waitTicks += Clock.ElapsedTicks - start;
                        }
                        if (Interlocked.CompareExchange(ref slot.State, Closed, Parsed) == Parsed)
                        {
                            ConfigNode node = slot.Node;
                            slot.Node = null;
                            if (Unchanged(slot))
                            {
                                s_hits++;
                                return node;
                            }
                            s_stale++;
                        }
                    }
                }
            }
            s_misses++;
            return ConfigNode.Load(path);
        }

        private static bool Unchanged(Slot slot)
        {
            try
            {
                var info = new FileInfo(slot.Path);
                return info.Length == slot.Length && info.LastWriteTimeUtc.Ticks == slot.WriteTicks;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The first root built while active is the one the prefetch was for; any later build is stock.
        private static void RootBuilt()
        {
            if (!s_active || s_index == null)
                return;
            long elapsed = Clock.ElapsedTicks;
            long parsedAt = Interlocked.Read(ref s_parsedAtTicks);
            Log.Info("ConfigPrefetch: " + s_hits + " of " + s_slots.Length + " GameData configs parsed ahead on worker threads, "
                     + s_misses + " parsed on the main thread" + (s_stale > 0 ? ", " + s_stale + " re-read (changed on disk)" : "")
                     + "; main thread waited " + (s_waitTicks * 1000 / Stopwatch.Frequency) + " ms; workers "
                     + (parsedAt >= 0 ? "done " + (parsedAt * 1000 / Stopwatch.Frequency) + " ms" : "still running") + " after start, config tree built "
                     + (elapsed * 1000 / Stopwatch.Frequency) + " ms after start");
            Close();
        }

        private static void Close()
        {
            s_active = false;
            WorkerLogFilter filter = s_filter;
            s_filter = null;
            if (filter != null)
            {
                filter.Bypass = true;
                // Another mod may have wrapped our filter since: then it stays in the chain as a pass-through.
                if (ReferenceEquals(UnityEngine.Debug.unityLogger.logHandler, filter))
                    UnityEngine.Debug.unityLogger.logHandler = filter.Inner;
            }
            Slot[] slots = s_slots;
            if (slots != null)
                for (int i = 0; i < slots.Length; i++)
                    slots[i].Node = null;
            s_slots = null;
            s_index = null;
        }
    
        private sealed class WorkerLogFilter : ILogHandler
        {
            public readonly ILogHandler Inner;
            public volatile bool Bypass;

            public WorkerLogFilter(ILogHandler inner) { Inner = inner; }

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
            {
                if (!Bypass && t_worker)
                {
                    t_logged = true;
                    return;
                }
                Inner.LogFormat(logType, context, format, args);
            }

            public void LogException(Exception exception, UnityEngine.Object context)
            {
                if (!Bypass && t_worker)
                {
                    t_logged = true;
                    return;
                }
                Inner.LogException(exception, context);
            }
        }
    }
}
