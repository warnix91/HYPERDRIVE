using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// Scene changes: KSP writes the save (GamePersistence.SaveGame, e.g. persistent.sfs when leaving the space centre)
    /// and the next scene reads it again (GamePersistence.LoadSFSFile: ConfigNode.Load on the main thread, a large share of
    /// a second for a big career). Right after a save is written, a worker thread reads that file's lines as
    /// File.ReadAllLines does (UTF-8, byte order marks honoured) but without locking it (the game can write it again at any
    /// time), and parses them with the same parser ConfigNode.Load uses (LoadFromStringArray: pure C#, no Unity API, no
    /// shared state); LoadSFSFile for the same file takes the result, once, if
    /// the file still has the size and write time the worker read. Same parser, same bytes: the same tree, built from the
    /// file as stock does (nothing shared with the game's live objects, nothing added to the file). A parse that logs
    /// anything (malformed file) is dropped, and the stock path parses and logs as before. At the main menu, the save
    /// written last (the one a player resumes) is parsed the same way, ahead of the choice. A result not taken is dropped
    /// at the next scene.
    /// The game writes a save with File.Open(path, FileMode.Create), which fails (sharing violation, on Windows and in Mono
    /// on macOS/Linux alike) while any other handle on that file is open. So the worker holds the file only to read its
    /// bytes in one go, under a lock; from the moment a parse of a file is queued until its bytes are read, a game or mod
    /// write of that file (ConfigNode.Save) holds the same lock for the whole write: it waits for a read in progress (a
    /// few milliseconds) instead of failing, and the worker cannot open the file during the write. A write changes size
    /// or time, so that parse is then not used.
    /// KSPCommunityFixes SceneLoadSpeedBoost does this job its own way: when enabled, it is left to it.
    /// </summary>
    internal sealed class SavePrefetch : Subsystem
    {
        private sealed class Job
        {
            public string Path;
            public long Length, Ticks;
            public ConfigNode Node;
            public bool Usable;
            public double ParseMs;
            public readonly ManualResetEvent Finished = new ManualResetEvent(false);
        }

        private static Job s_job;                 // the last save written; main thread only
        private static readonly object s_fileLock = new object();   // the worker reading the file, or a write of it
        private static string s_pendingPath;                         // queued and not read yet; null when none
        private static Func<string[], bool, ConfigNode> s_fromLines;
        private static WorkerLogFilter s_filter;
        private static int s_hits, s_misses;
        [ThreadStatic] private static bool t_worker;
        [ThreadStatic] private static bool t_logged;

        public override string Name => "SavePrefetch";

        public override string[] KspcfEquivalents => new[] { "SceneLoadSpeedBoost" };

        private static bool s_verify;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            // Developer: each save taken from the worker is also parsed the stock way and both trees compared.
            s_verify = settings.GetBool("verify", false);
        }

        protected internal override void OnEnable()
        {
            var fromLines = AccessTools.Method(typeof(ConfigNode), "LoadFromStringArray", new[] { typeof(string[]), typeof(bool) });
            if (fromLines == null || fromLines.ReturnType != typeof(ConfigNode))
                throw new SubsystemUnavailableException("ConfigNode.LoadFromStringArray not found (game version mismatch?)");
            s_fromLines = (Func<string[], bool, ConfigNode>)Delegate.CreateDelegate(typeof(Func<string[], bool, ConfigNode>), fromLines);
            Patch(AccessTools.Method(typeof(GamePersistence), nameof(GamePersistence.SaveGame), new[] { typeof(Game), typeof(string), typeof(string), typeof(SaveMode) }),
                  postfix: nameof(SaveGamePostfix));
            Patch(AccessTools.Method(typeof(GamePersistence), nameof(GamePersistence.SaveGame), new[] { typeof(GameBackup), typeof(string), typeof(string), typeof(SaveMode) }),
                  postfix: nameof(SaveGamePostfix));
            Patch(AccessTools.Method(typeof(GamePersistence), nameof(GamePersistence.LoadSFSFile), new[] { typeof(string), typeof(string) }),
                  prefix: nameof(LoadSFSFilePrefix));
            Patch(AccessTools.Method(typeof(ConfigNode), nameof(ConfigNode.Save), new[] { typeof(string), typeof(string) }),
                  prefix: nameof(ConfigNodeSavePrefix), finalizer: nameof(ConfigNodeSaveFinalizer));
            s_filter = new WorkerLogFilter(UnityEngine.Debug.unityLogger.logHandler);
            UnityEngine.Debug.unityLogger.logHandler = s_filter;
            Log.Info("SavePrefetch: a save just written is parsed on a worker thread for the next scene");
        }

        protected internal override void OnDisable()
        {
            s_job = null;
            WorkerLogFilter filter = s_filter;
            s_filter = null;
            if (filter == null)
                return;
            filter.Bypass = true;
            // Someone may have wrapped the handler since: then this one stays in the chain as a pass-through.
            if (ReferenceEquals(UnityEngine.Debug.unityLogger.logHandler, filter))
                UnityEngine.Debug.unityLogger.logHandler = filter.Inner;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            s_job = null;   // the next scene has loaded what it needed
            if (s_hits + s_misses > 0 && scene == GameScenes.FLIGHT)
                Log.Info("SavePrefetch: " + s_hits + " saves read from the worker's parse, " + s_misses + " from disk so far");
            if (scene == GameScenes.MAINMENU)
                PrefetchLatestSave();
        }

        // saves/<folder>/persistent.sfs written last.
        private static void PrefetchLatestSave()
        {
            try
            {
                string root = KSPUtil.ApplicationRootPath + "saves/";
                if (!Directory.Exists(root))
                    return;
                string best = null, bestFolder = null;
                long bestTicks = long.MinValue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string file = Path.Combine(dir, "persistent.sfs");
                    if (!File.Exists(file))
                        continue;
                    long ticks = File.GetLastWriteTimeUtc(file).Ticks;
                    if (ticks > bestTicks)
                    {
                        bestTicks = ticks;
                        best = file;
                        bestFolder = Path.GetFileName(dir);
                    }
                }
                if (best == null)
                    return;
                Queue(new Job { Path = SavePath("persistent", bestFolder) });
            }
            catch (Exception e)
            {
                Log.Info("SavePrefetch: no save parsed ahead at the main menu (" + e.Message + ")");
            }
        }

        private static string SavePath(string fileName, string saveFolder)
        {
            return KSPUtil.ApplicationRootPath + "saves/" + saveFolder + "/" + fileName + ".sfs";
        }

        // __result: the file name actually written (stock may have changed it), empty when nothing was saved.
        private static void SaveGamePostfix(string __result, string saveFolder)
        {
            if (string.IsNullOrEmpty(__result) || s_filter == null)
                return;
            Queue(new Job { Path = SavePath(__result, saveFolder) });
        }

        private static void Queue(Job job)
        {
            s_job = job;
            Volatile.Write(ref s_pendingPath, job.Path);
            ThreadPool.QueueUserWorkItem(Parse, job);
        }

        private static void Parse(object state)
        {
            var job = (Job)state;
            t_worker = true;
            t_logged = false;
            try
            {
                long start = Stopwatch.GetTimestamp();
                var before = new FileInfo(job.Path);
                long length = before.Length, ticks = before.LastWriteTimeUtc.Ticks;
                byte[] bytes = ReadAllBytes(job.Path);
                if (bytes == null || bytes.LongLength != length)
                    return;
                // Decoded as File.ReadAllLines does: UTF-8, byte order marks honoured.
                var lines = new System.Collections.Generic.List<string>();
                using (var reader = new StreamReader(new MemoryStream(bytes, false), System.Text.Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        lines.Add(line);
                }
                ConfigNode node = s_fromLines(lines.ToArray(), true);   // ConfigNode.Load(path) = LoadFromStringArray(ReadAllLines(path), true)
                var after = new FileInfo(job.Path);
                if (node != null && !t_logged && after.Length == length && after.LastWriteTimeUtc.Ticks == ticks)
                {
                    job.Node = node;
                    job.Length = length;
                    job.Ticks = ticks;
                    job.ParseMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    job.Usable = true;
                }
            }
            catch (Exception)
            {
                // Left to the stock path, which reports any error as usual.
            }
            finally
            {
                Interlocked.CompareExchange(ref s_pendingPath, null, job.Path);   // also when the file was never opened
                t_worker = false;
                job.Finished.Set();
            }
        }

        // The file is open only here, under s_fileLock, for one read of its bytes; a write of it waits (ConfigNodeSavePrefix).
        private static byte[] ReadAllBytes(string path)
        {
            lock (s_fileLock)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
                    {
                        long size = stream.Length;
                        if (size > int.MaxValue)
                            return null;
                        var bytes = new byte[size];
                        int read = 0;
                        while (read < bytes.Length)
                        {
                            int n = stream.Read(bytes, read, bytes.Length - read);
                            if (n <= 0)
                                return null;
                            read += n;
                        }
                        return bytes;
                    }
                }
                finally
                {
                    Interlocked.CompareExchange(ref s_pendingPath, null, path);
                }
            }
        }

        // Any write through ConfigNode.Save (stock saves, mods) of the file a worker is about to read or reading: the write
        // holds s_fileLock until it ends, so that File.Open(FileMode.Create) finds no other handle on it.
        private static void ConfigNodeSavePrefix(string fileFullName, out bool __state)
        {
            __state = false;
            string pending = Volatile.Read(ref s_pendingPath);
            if (pending == null || fileFullName == null || !SamePath(pending, fileFullName))
                return;
            long start = Stopwatch.GetTimestamp();
            Monitor.Enter(s_fileLock, ref __state);
            double waitMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (waitMs >= 1.0)
                Log.Info("SavePrefetch: the write of " + Path.GetFileName(fileFullName) + " waited " + waitMs.ToString("F0") + " ms for the worker's read to end");
        }

        private static Exception ConfigNodeSaveFinalizer(Exception __exception, bool __state)
        {
            if (__state)
                Monitor.Exit(s_fileLock);
            return __exception;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        private static bool LoadSFSFilePrefix(string filename, string saveFolder, ref ConfigNode __result)
        {
            Job job = s_job;
            if (job == null || filename == null || saveFolder == null)
                return true;
            string path = SavePath(filename, saveFolder);
            if (!string.Equals(path, job.Path, StringComparison.Ordinal))
                return true;
            s_job = null;   // used once
            long start = Stopwatch.GetTimestamp();
            job.Finished.WaitOne();
            double waitMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            try
            {
                var now = new FileInfo(path);
                if (job.Usable && now.Exists && now.Length == job.Length && now.LastWriteTimeUtc.Ticks == job.Ticks)
                {
                    __result = job.Node;
                    s_hits++;
                    if (s_verify)
                    {
                        ConfigNode stock = ConfigNode.Load(path);
                        Log.Info("SavePrefetch verify: " + filename + ".sfs " + (stock != null && stock.ToString() == job.Node.ToString() ? "identical to" : "DIFFERENT from")
                                 + " the stock parse");
                    }
                    Log.Info("SavePrefetch: " + filename + ".sfs taken from the worker's parse (" + job.ParseMs.ToString("F0") + " ms parse, "
                             + waitMs.ToString("F0") + " ms waited)");
                    return false;
                }
            }
            catch (Exception)
            {
            }
            s_misses++;
            return true;
        }

        // Logs from the worker are swallowed and mark its parse as unusable (the stock path then logs them on the main
        // thread, as before); every other log goes through untouched.
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
