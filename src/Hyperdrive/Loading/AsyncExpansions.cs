using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Expansions;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// The expansion asset bundles (Making History, Breaking Ground: 455 MB) are opened by stock with the blocking
    /// AssetBundle.LoadFromMemory: the game shows the same frozen frame for ~6 s and then ~12 s while they open.
    /// Here they are opened with AssetBundle.LoadFromFileAsync (Unity's background loading at high priority for the
    /// duration) so frames keep coming, which the loading animation needs. Their MD5 (required by the expansion signature
    /// check) is computed on background threads from the start of loading with the same MD5CryptoServiceProvider, so the
    /// hash is identical. Everything stock records is recorded the same way: asset and scene lists with the same log
    /// lines, and the bundle with its path and hash in BundleLoader.loadedBundles. Any problem runs the stock loader.
    ///
    /// LZ4 cache (lz4Cache): the expansion bundles ship as one LZMA block each (Breaking Ground 288 MB -> 684 MB, Making
    /// History 133 MB -> 419 MB): Unity decompresses the whole block on one thread before the bundle opens (~12 s on a
    /// Mac) and keeps it in memory. At the main menu, a copy recompressed to chunked LZ4 by Unity itself
    /// (AssetBundle.RecompressAssetBundleAsync, same content) is written to PluginData/BundleCache, on a low-priority
    /// background thread. Later starts open that copy instead when its source size and date match; once the source MD5 is
    /// computed (as before, for KSP's signature check) it is compared with the one recorded for the copy, and on any
    /// difference the copy is dropped and the original opened. The game's files are never changed; the path and hash
    /// recorded for KSP are the original's. Only written when the disk keeps at least cacheMinFreeGB free.
    /// </summary>
    internal sealed class AsyncExpansions : Subsystem
    {
        private sealed class HashJob
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public byte[] Hash;
            public Exception Error;
            public AssetBundleCreateRequest Early;     // opened ahead of KSP's turn (preopen)
            public bool Claimed;
            public bool FromCache;                     // Early opens the LZ4 copy
            public string CacheMd5;                    // MD5 of the source recorded with the copy
        }

        private static AsyncExpansions s_instance;
        private static readonly Dictionary<string, HashJob> s_jobs = new Dictionary<string, HashJob>(StringComparer.Ordinal);
        private static MethodInfo s_original;
        private bool _preopen = true;
        private static bool s_lz4Cache = true;
        private static float s_cacheMinFreeGB = 4f;
        private static readonly Queue<string> s_toRecompress = new Queue<string>();
        private static AssetBundleRecompressOperation s_recompress;
        private static string s_recompressSource, s_recompressTemp;
        private static readonly Stopwatch s_recompressClock = new Stopwatch();

        public override string Name => "AsyncExpansions";

        // Another mod that replaces one of the game methods patched here (a transpiler, or a prefix that can skip it) keeps
        // its version: this subsystem stands down (reviewed from the start of the load, see SubsystemHost.ReviewSharedTargets).
        public override bool YieldOnSharedTargets => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _preopen = settings.GetBool("preopen", _preopen);
            s_lz4Cache = settings.GetBool("lz4Cache", s_lz4Cache);
            s_cacheMinFreeGB = Mathf.Clamp(settings.GetFloat("cacheMinFreeGB", s_cacheMinFreeGB), 1f, 1000f);
        }

        public override string[] KspcfEquivalents => new[] { "ExpansionBundlePreload" };

        protected internal override void OnEnable()
        {
            s_original = AccessTools.Method(typeof(BundleLoader), "LoadAssetBundle", new[] { typeof(string), typeof(string) });
            Patch(s_original, prefix: nameof(LoadAssetBundlePrefix));
            s_instance = this;
            int started = StartHashJobs();
            int early = _preopen ? StartEarlyOpens() : 0;
            Log.Info("AsyncExpansions: " + started + " expansion bundles hashed in the background and opened asynchronously"
                     + (early > 0 ? ", " + early + " opening from now on, while the game database loads" : ""));
        }

        protected internal override void OnDisable()
        {
            s_instance = null;
            ReleaseUnclaimed("subsystem disabled");
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene != GameScenes.MAINMENU)
                return;
            ReleaseUnclaimed("main menu reached");
            if (!s_lz4Cache || s_toRecompress.Count > 0 || s_recompress != null)
                return;
            lock (s_jobs)
            {
                foreach (KeyValuePair<string, HashJob> entry in s_jobs)
                    if (entry.Value.Claimed && !entry.Value.FromCache && entry.Value.Hash != null && entry.Value.Error == null)
                        s_toRecompress.Enqueue(entry.Key);
            }
        }

        // ------------------------------------------------------------------ LZ4 cache

        private static string CacheDirectory => Path.Combine(Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"), "BundleCache");

        private static string CachePath(string source)
        {
            string root = Path.GetFullPath(KSPUtil.ApplicationRootPath);
            string relative = source.StartsWith(root, StringComparison.Ordinal) ? source.Substring(root.Length) : Path.GetFileName(source);
            foreach (char c in new[] { '/', '\\', ':', ' ' })
                relative = relative.Replace(c, '_');
            return Path.Combine(CacheDirectory, relative + ".lz4");
        }

        private static string Hex(byte[] hash)
        {
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // The copy is used when its record names the source's current size and date; the MD5 is checked later.
        private static bool ValidCache(string source, out string cache, out string md5)
        {
            cache = CachePath(source);
            md5 = null;
            try
            {
                string record = cache + ".cfg";
                if (!File.Exists(cache) || !File.Exists(record))
                    return false;
                ConfigNode node = ConfigNode.Load(record)?.GetNode("BUNDLE_CACHE");
                var info = new FileInfo(source);
                if (node == null || node.GetValue("size") != info.Length.ToString() || node.GetValue("time") != info.LastWriteTimeUtc.Ticks.ToString())
                    return false;
                md5 = node.GetValue("md5");
                return !string.IsNullOrEmpty(md5);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void DropCache(string source)
        {
            try
            {
                string cache = CachePath(source);
                if (File.Exists(cache)) File.Delete(cache);
                if (File.Exists(cache + ".cfg")) File.Delete(cache + ".cfg");
            }
            catch (Exception) { }
        }

        private static long FreeBytes(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace; }
            catch (Exception) { return -1; }
        }

        // Main menu and later: one recompression at a time on Unity's background thread, polled here.
        protected internal override void OnUpdate()
        {
            if (s_recompress != null)
            {
                if (!s_recompress.isDone)
                    return;
                FinishRecompress();
            }
            if (s_toRecompress.Count == 0)
                return;
            string source = s_toRecompress.Dequeue();
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                long free = FreeBytes(CacheDirectory);
                long need = (long)(s_cacheMinFreeGB * 1024 * 1024 * 1024) + new FileInfo(source).Length * 3;
                if (free < need)
                {
                    Log.Info("AsyncExpansions: no LZ4 copy of " + Path.GetFileName(source) + " (disk: " + (free / (1024 * 1024)) + " MB free, keeping at least "
                             + s_cacheMinFreeGB + " GB)");
                    s_toRecompress.Clear();
                    return;
                }
                s_recompressSource = source;
                s_recompressTemp = CachePath(source) + ".tmp";
                // Nothing is being written now: any .tmp is left from a game closed during a copy (ours, or Unity's own
                // "<temp>.tmp", hundreds of MB for Breaking Ground).
                foreach (string stale in Directory.GetFiles(CacheDirectory, "*.tmp"))
                {
                    try { File.Delete(stale); }
                    catch (Exception e) { Log.Warn("AsyncExpansions: could not delete " + Path.GetFileName(stale) + " (" + e.Message + ")"); }
                }
                s_recompressClock.Reset();
                s_recompressClock.Start();
                s_recompress = AssetBundle.RecompressAssetBundleAsync(source, s_recompressTemp, BuildCompression.LZ4Runtime, 0, UnityEngine.ThreadPriority.Low);
            }
            catch (Exception e)
            {
                Log.Warn("AsyncExpansions: LZ4 copy of " + Path.GetFileName(source) + " not started (" + e.Message + ")");
                s_recompress = null;
            }
        }

        private static void FinishRecompress()
        {
            AssetBundleRecompressOperation op = s_recompress;
            string source = s_recompressSource, temp = s_recompressTemp;
            s_recompress = null;
            try
            {
                HashJob job;
                lock (s_jobs)
                    s_jobs.TryGetValue(source, out job);
                if (!op.success || job == null || job.Hash == null || !File.Exists(temp))
                {
                    Log.Warn("AsyncExpansions: LZ4 copy of " + Path.GetFileName(source) + " failed (" + op.humanReadableResult + ")");
                    if (File.Exists(temp)) File.Delete(temp);
                    return;
                }
                string cache = CachePath(source);
                var info = new FileInfo(source);
                var root = new ConfigNode();
                ConfigNode node = root.AddNode("BUNDLE_CACHE");
                node.AddValue("source", Path.GetFileName(source));
                node.AddValue("size", info.Length.ToString());
                node.AddValue("time", info.LastWriteTimeUtc.Ticks.ToString());
                node.AddValue("md5", Hex(job.Hash));
                if (File.Exists(cache)) File.Delete(cache);
                File.Move(temp, cache);
                root.Save(cache + ".cfg", "Hyperdrive: LZ4 copy of an expansion bundle, used while the source keeps this size, date and MD5");
                Log.Info("AsyncExpansions: LZ4 copy of " + Path.GetFileName(source) + " written in " + s_recompressClock.Elapsed.TotalSeconds.ToString("F1")
                         + " s (" + (new FileInfo(cache).Length / (1024 * 1024)) + " MB, source " + (info.Length / (1024 * 1024)) + " MB): opened from it at the next start");
            }
            catch (Exception e)
            {
                Log.Warn("AsyncExpansions: LZ4 copy of " + Path.GetFileName(source) + " not kept (" + e.Message + ")");
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            }
        }

        // KSP opens the expansion bundles one after the other once the game database is loaded: ~17 s in which nothing else
        // loads (LZMA bundles are decompressed whole). Unity decompresses on its own loading thread, idle while the database
        // loads on the main thread, so the requests start right away at the default priority; KSP's turn then finds them
        // open or nearly. Same bundles, same records, same moment for KSP: only the waiting moves.
        private static int StartEarlyOpens()
        {
            int count = 0;
            lock (s_jobs)
            {
                foreach (KeyValuePair<string, HashJob> entry in s_jobs)
                {
                    try
                    {
                        HashJob job = entry.Value;
                        if (s_lz4Cache && ValidCache(entry.Key, out string cache, out string md5))
                        {
                            job.FromCache = true;
                            job.CacheMd5 = md5;
                            job.Early = AssetBundle.LoadFromFileAsync(cache);
                        }
                        else
                            job.Early = AssetBundle.LoadFromFileAsync(entry.Key);
                        count++;
                    }
                    catch (Exception e)
                    {
                        Log.Warn("AsyncExpansions: early open of " + Path.GetFileName(entry.Key) + " failed (" + e.Message + "), KSP's turn opens it");
                    }
                }
            }
            return count;
        }

        // A bundle KSP never asked for (an expansion it did not load) is closed again.
        private static void ReleaseUnclaimed(string why)
        {
            lock (s_jobs)
            {
                foreach (KeyValuePair<string, HashJob> entry in s_jobs)
                {
                    HashJob job = entry.Value;
                    if (job.Early == null || job.Claimed)
                        continue;
                    // Still opening: assetBundle waits for it, so that no copy stays open for whoever loads it next.
                    AssetBundle early = job.Early.assetBundle;
                    if (early != null)
                        early.Unload(true);
                    job.Early = null;
                    Log.Info("AsyncExpansions: " + Path.GetFileName(entry.Key) + " opened early but never used by KSP, closed (" + why + ")");
                }
            }
        }

        private static int StartHashJobs()
        {
            string root = KSPExpansionsUtils.ExpansionsGameDataPath;
            if (!Directory.Exists(root))
                return 0;
            int count = 0;
            foreach (string dir in Directory.GetDirectories(root, "AssetBundles", SearchOption.AllDirectories))
            {
                foreach (string file in Directory.GetFiles(dir))
                {
                    string path = Path.GetFullPath(file);
                    lock (s_jobs)
                    {
                        if (s_jobs.ContainsKey(path))
                            continue;
                        var job = new HashJob();
                        s_jobs[path] = job;
                        new Thread(() => Hash(path, job)) { IsBackground = true, Name = "Hyperdrive.AsyncExpansions" }.Start();
                        count++;
                    }
                }
            }
            return count;
        }

        // Background thread: pure C# file reading and hashing.
        private static void Hash(string path, HashJob job)
        {
            try
            {
                using (var md5 = new MD5CryptoServiceProvider())
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                    job.Hash = md5.ComputeHash(stream);
            }
            catch (Exception e)
            {
                job.Error = e;
            }
            job.Done.Set();
        }

        private static bool LoadAssetBundlePrefix(string bundleName, string folderPath, ref IEnumerator __result)
        {
            if (s_instance == null || bundleName == null || folderPath == null)
                return true;
            string protocol = KSPUtil.ApplicationFileProtocol;
            if (!folderPath.StartsWith(protocol, StringComparison.Ordinal))
                return true;
            string path;
            try
            {
                path = Path.GetFullPath(folderPath.Substring(protocol.Length) + bundleName);
            }
            catch (Exception)
            {
                return true;
            }
            HashJob job;
            lock (s_jobs)
                s_jobs.TryGetValue(path, out job);
            if (job == null || !File.Exists(path))
                return true;   // unknown bundle: stock
            __result = Load(bundleName, folderPath, path, job);
            return false;
        }

        private static IEnumerator Load(string bundleName, string folderPath, string path, HashJob job)
        {
            if (BundleLoader.IsBundleLoaded(bundleName))
            {
                UnityEngine.Debug.Log(bundleName + " already loaded - skipping...");
                yield break;
            }

            var clock = Stopwatch.StartNew();
            AssetBundleCreateRequest request = job.Early;
            job.Claimed = true;
            bool early = request != null, wasDone = early && request.isDone;
            if (!wasDone)
            {
                UnityEngine.ThreadPriority previous = Application.backgroundLoadingPriority;
                Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.High;
                // vsync is off while KSP loads: uncapped, the loading screen renders hundreds of frames per second and the
                // bundle opens 2.5x slower (measured: 46-51 s instead of 20 s). Cap at the display rate while it opens.
                int previousRate = Application.targetFrameRate;
                if (previousRate <= 0 || previousRate > 144)
                    Application.targetFrameRate = Mathf.Clamp(Screen.currentResolution.refreshRate, 60, 144);
                if (request == null)
                    request = AssetBundle.LoadFromFileAsync(path);
                yield return request;
                Application.targetFrameRate = previousRate;
                Application.backgroundLoadingPriority = previous;
            }
            AssetBundle bundle = request.assetBundle;

            while (!job.Done.WaitOne(0))
                yield return null;

            // LZ4 copy: valid only for the very file it was made from.
            if (job.FromCache && (job.Hash == null || bundle == null || !string.Equals(Hex(job.Hash), job.CacheMd5, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Warn("AsyncExpansions: LZ4 copy of " + bundleName + " does not match the bundle any more, opening the original");
                if (bundle != null)
                    bundle.Unload(true);
                DropCache(path);
                job.FromCache = false;
                AssetBundleCreateRequest original = AssetBundle.LoadFromFileAsync(path);
                yield return original;
                bundle = original.assetBundle;
            }

            if (bundle == null || job.Error != null || job.Hash == null)
            {
                if (bundle != null)
                    bundle.Unload(true);
                Log.Warn("AsyncExpansions: " + bundleName + " could not be prepared (" + (job.Error?.Message ?? "bundle did not open") + "), stock loader takes it");
                IEnumerator stock = StockLoad(bundleName, folderPath);
                while (stock != null && stock.MoveNext())
                    yield return stock.Current;
                yield break;
            }

            // Same records and log lines as stock BundleLoader.LoadAssetBundle.
            string[] assets = bundle.GetAllAssetNames();
            if (assets.Length != 0)
                UnityEngine.Debug.Log("Assets:");
            for (int i = 0; i < assets.Length; i++)
            {
                UnityEngine.Debug.Log(Path.GetFileName(assets[i]));
                BundleLoader.loadedAssets.Add(assets[i], new BundleLoader.ABAssetInfo(assets[i], bundleName, false));
            }
            string[] scenes = bundle.GetAllScenePaths();
            if (scenes.Length != 0)
                UnityEngine.Debug.Log("Scenes:");
            for (int j = 0; j < scenes.Length; j++)
            {
                UnityEngine.Debug.Log(Path.GetFileName(scenes[j]));
                BundleLoader.loadedAssets.Add(scenes[j], new BundleLoader.ABAssetInfo(scenes[j], bundleName, true));
            }
            BundleLoader.loadedBundles.Add(bundle.name, new BundleLoader.ABInfo(bundle, folderPath + bundleName, job.Hash));
            Log.Info("AsyncExpansions: " + bundleName + " opened in " + clock.Elapsed.TotalSeconds.ToString("F2") + " s"
                     + (early ? (wasDone ? " (already open)" : " (opened early, finished now)") : "") + (job.FromCache ? " from its LZ4 copy" : ""));
        }

        // The original coroutine, for the fallback path (s_instance is cleared meanwhile so the prefix lets it through).
        private static IEnumerator StockLoad(string bundleName, string folderPath)
        {
            AsyncExpansions self = s_instance;
            s_instance = null;
            try
            {
                return (IEnumerator)s_original.Invoke(null, new object[] { bundleName, folderPath });
            }
            finally
            {
                s_instance = self;
            }
        }
    }
}
