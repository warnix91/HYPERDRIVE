using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using DDSHeaders;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;
using UnityEngine.Networking;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// Faster first load of textures: DDS (the bulk of GameData: 1068 files, 1.3 GB in a stock install) and PNG (632 files).
    /// Stock DatabaseLoaderTexture_DDS reads each file with File.ReadAllBytes, then copies the pixel data again with
    /// BinaryReader.ReadBytes (twice the file size of garbage per texture), and the database loop starts a coroutine per
    /// file. Here, before the stock loop runs, a background thread reads the files into reused buffers while the main
    /// thread builds the textures exactly as stock does: same DDS header parser (KSP's DDSHeader), same Texture2D
    /// format/size/mip flag, same pixel bytes (LoadRawTextureData from the pinned buffer), same Apply flags, same
    /// TextureInfo and names, added to GameDatabase.databaseTexture in file order. The stock loop then finds them already
    /// loaded and skips them; anything this path does not handle (DXT3, DX10, uncompressed, errors) is left to stock.
    /// PNG files are decoded with Texture2D.LoadImage from the same bytes and go through the same steps as stock
    /// DatabaseLoaderTexture_PNG (normal maps, flags, icons, compression); "png = verify" rebuilds every PNG after a stock
    /// load and compares size, format, mip count and pixel bytes (631 of 631 identical on a stock install).
    /// pngCache (on by default): each PNG's final texture is kept in PluginData/PngCache (see PngCache) and later launches
    /// read it with the DDS files, skipping the decode and the compression; "png = verify" also checks every cache file
    /// against the texture stock built.
    /// The stock asset loop is then driven by this class: when a loader (models, remaining textures, mods' loaders) finishes
    /// inside StartCoroutine, the loop continues in the same frame instead of waiting for the next one; measured on a stock
    /// install, textures cost ~2.8 frames each and models ~4 frames each before this change.
    /// Sounds: stock DatabaseLoaderAudio loads them one after the other (one UnityWebRequestMultimedia each, at least a
    /// frame per file) while the decoding itself runs on Unity's threads. Here the same requests (same URL, same
    /// AudioType.UNKNOWN) are started ahead, a few dozen at a time, while the textures load; when the stock loop reaches a
    /// sound, its loader takes the finished request and does what stock does with it (DownloadHandlerAudioClip.GetContent,
    /// same success test and warning). A sound without a request goes the stock way.
    /// Only the very first load is accelerated (an in-game database reload runs fully stock). The main thread yields a
    /// frame whenever its budget is spent, so the loading screen stays alive. Stands down with KSPCommunityFixes
    /// (which has its own loader) or when another mod registers a DDS loader.
    /// </summary>
    internal sealed partial class FastLoader : Subsystem
    {
        private sealed class Item
        {
            public UrlDir.UrlFile File;
            public byte[] Buffer;
            public int Length;
            public Exception Error;
            public string ReadPath;              // a PNG cache file, or null: the file itself
            public long SourceLength, SourceTicks;
            // Direct DDS path (FastLoader.Direct): only the header was read; the rest goes straight into the texture.
            public bool HeaderOnly;
            public long FileLength;
            public Texture2D Texture;
            public bool IsNormalMap, KeepReadable, InfoReadable, InfoCompressed;
            public long DataOffset;
            public int Frame, Slot;
            public DirectState State;
            public Unity.IO.LowLevel.Unsafe.ReadHandle Handle;
            public IntPtr Pixels;
            public long PixelLength;
            public int PixelState;               // 0 reading, 1 done, 2 failed (pixel threads)
            public Exception PixelError;
        }

        // Wraps a DatabaseLoader.Load coroutine to tell whether it finished during StartCoroutine itself.
        private sealed class SyncProbe : IEnumerator
        {
            private readonly IEnumerator _inner;
            private bool _started;

            public SyncProbe(IEnumerator inner) { _inner = inner; }

            public object Current => _inner.Current;

            public bool MoveNext()
            {
                bool more = _inner.MoveNext();
                if (!_started)
                {
                    _started = true;
                    if (!more)
                        s_lastLoaderFinishedSync = true;
                }
                return more;
            }

            public void Reset() => _inner.Reset();
        }

        private const int MaxInFlight = 16;
        // PNG files decode on Unity's worker threads; each finished one is then turned into a texture on the main thread,
        // all of them in the same frame when they finish together.
        private static int s_maxPngRequests = 48;
        private static bool s_audioPrefetch = true;
        private static int s_maxAudioRequests = 32;
        private static readonly Dictionary<string, UnityWebRequest> s_audioRequests = new Dictionary<string, UnityWebRequest>(StringComparer.Ordinal);
        private static readonly List<string> s_audioPending = new List<string>();
        private static int s_audioNext, s_audioUsed;
        private static Action<DatabaseLoader<AudioClip>, AudioClip> s_setAudioObj;
        private static Action<DatabaseLoader<AudioClip>, bool> s_setAudioOk;

        private static bool s_driving, s_lastLoaderFinishedSync;
        private static bool s_preloadPending, s_holdStock, s_inLoop;
        private static int s_frame = -1;
        private static float s_frameStart;
        private static float s_lastPreloadProgress;
        private const float HoldWatchdogSeconds = 30f;
        private static AccessTools.FieldRef<object, object> s_iteratorCurrent;
        private int _skipped;

        // PartLoader compile loops (parts, IVA props, IVA spaces): their "yield return null" only refresh the loading screen.
        private static readonly Dictionary<Type, AccessTools.FieldRef<object, object>> s_compileCurrent = new Dictionary<Type, AccessTools.FieldRef<object, object>>();
        private static bool s_inCompileLoop;
        private static int s_compileFrame = -1;
        private static float s_compileFrameStart;
        private int _compileSkipped;
        private int _patchedLoaders;

        private static FastLoader s_instance;
        private static FieldInfo s_lastLoadTime;
        private static AccessTools.FieldRef<GameDatabase, string> s_progressTitle;
        private static AccessTools.FieldRef<GameDatabase, float> s_progressFraction;

        private float _frameBudgetSeconds = 0.05f;

        // Set by the loading animation: main-thread time per frame that keeps it at the display refresh rate.
        internal static float FrameBudgetOverride = -1f;

        // Main-thread time spent in loading work per frame (our preload, the stock asset loop and the part compilation
        // loops), so the animation can tell its own frame cost apart from indivisible loading steps.
        private static int s_workFrame = -1, s_prevWorkFrame = -2;
        private static long s_workTicks, s_prevWorkTicks, s_loTick, s_compileTick;

        internal static float WorkSecondsInFrame(int frame)
        {
            long ticks = frame == s_workFrame ? s_workTicks : frame == s_prevWorkFrame ? s_prevWorkTicks : 0;
            return (float)(ticks / (double)Stopwatch.Frequency);
        }

        private static void AddWork(long ticks)
        {
            int frame = Time.frameCount;
            if (frame != s_workFrame)
            {
                s_prevWorkFrame = s_workFrame;
                s_prevWorkTicks = s_workTicks;
                s_workFrame = frame;
                s_workTicks = 0;
            }
            s_workTicks += ticks;
        }
        private float Budget => FrameBudgetOverride > 0f ? FrameBudgetOverride : _frameBudgetSeconds;
        private static bool s_fastNormalMaps;
        private string _pngMode = "on";   // off | verify | on
        private static bool s_pngCache = true;
        private static bool s_pngCacheAsk;   // pngCache = ask (default): the player is asked once (PngCachePrompt)
        private static bool s_promptTest;    // developer: the question shown even in a benchmark, captured, answered yes
        private static int s_readerThreads;
        private static float s_pngCacheMinFreeGB = 4f;

        // Reader thread <-> main thread
        private readonly object _lock = new object();
        private readonly Queue<Item> _ready = new Queue<Item>();
        private readonly Stack<byte[]> _freeBuffers = new Stack<byte[]>();
        private int _inFlight;
        private bool _readerDone, _abort;
        // Several reader threads (files claimed in order); a file read is queued only once every file before it is.
        private Item[] _slots;
        private int _claim, _publish, _readersRunning;

        public override string Name => "FastLoader";

        // Another mod that replaces one of the game methods patched here (a transpiler, or a prefix that can skip it) keeps
        // its version: this subsystem stands down (reviewed from the start of the load, see SubsystemHost.ReviewSharedTargets).
        public override bool YieldOnSharedTargets => true;

        public override string[] IncompatibleAssemblies => new[] { "KSPCommunityFixes", "WarmStart", "DDSLoader" };

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _frameBudgetSeconds = Mathf.Clamp(settings.GetFloat("frameBudgetMs", 50f), 10f, 500f) / 1000f;
            _pngMode = settings.GetString("png", _pngMode).Trim().ToLowerInvariant();
            string cacheMode = settings.GetString("pngCache", "true").Trim().ToLowerInvariant();
            s_pngCacheAsk = cacheMode == "ask";
            s_pngCache = cacheMode != "false" && cacheMode != "ask";
            s_promptTest = settings.GetBool("pngCachePromptTest", false);
            s_readerThreads = Mathf.Clamp(settings.GetInt("readerThreads", 0), 0, 32);
            s_pngCacheMinFreeGB = Mathf.Clamp(settings.GetFloat("pngCacheMinFreeGB", s_pngCacheMinFreeGB), 0f, 1000f);
            s_maxPngRequests = Mathf.Clamp(settings.GetInt("pngRequests", s_maxPngRequests), 1, 256);
            s_audioPrefetch = settings.GetBool("audio", s_audioPrefetch);
            s_maxAudioRequests = Mathf.Clamp(settings.GetInt("audioRequests", s_maxAudioRequests), 1, 256);
            LoadDirectSettings(settings);
        }

        protected internal override void OnEnable()
        {
            s_lastLoadTime = AccessTools.Field(typeof(GameDatabase), "lastLoadTime");
            if (s_lastLoadTime == null || s_lastLoadTime.FieldType != typeof(DateTime))
                throw new SubsystemUnavailableException("GameDatabase.lastLoadTime not found (game version mismatch?)");
            // Loading-screen text and bar (cosmetic): optional.
            try
            {
                s_progressTitle = AccessTools.FieldRefAccess<GameDatabase, string>("progressTitle");
                s_progressFraction = AccessTools.FieldRefAccess<GameDatabase, float>("progressFraction");
            }
            catch (Exception)
            {
                s_progressTitle = null;
                s_progressFraction = null;
            }
            s_instance = this;
            // GameDatabase.LoadObjects is already running when mods are loaded (it loads them): patch its state machine.
            Type iterator = AccessTools.FirstInner(typeof(GameDatabase), t => t.Name.Contains("LoadObjects"));
            MethodInfo moveNext = iterator != null ? AccessTools.Method(iterator, "MoveNext") : null;
            FieldInfo current = iterator != null ? AccessTools.Field(iterator, "<>2__current") : null;
            if (moveNext == null || current == null)
                throw new SubsystemUnavailableException("GameDatabase.LoadObjects state machine not found (game version mismatch?)");
            s_iteratorCurrent = AccessTools.FieldRefAccess<object, object>(current);
            Patch(moveNext, prefix: nameof(MoveNextPrefix), postfix: nameof(MoveNextPostfix));
            PatchLoaders();
            PatchCompileLoops();
            s_fastNormalMaps = NormalMapSelfTest();
            if (s_fastNormalMaps)
                Patch(AccessTools.Method(typeof(GameDatabase), nameof(GameDatabase.BitmapToUnityNormalMap)), prefix: nameof(BitmapToUnityNormalMapPrefix));
            if (s_audioPrefetch)
            {
                MethodInfo audioLoad = AccessTools.DeclaredMethod(typeof(DatabaseLoaderAudio), "Load", new[] { typeof(UrlDir.UrlFile), typeof(FileInfo) });
                MethodInfo setObj = AccessTools.PropertySetter(typeof(DatabaseLoader<AudioClip>), "obj");
                MethodInfo setOk = AccessTools.PropertySetter(typeof(DatabaseLoader<AudioClip>), "successful");
                string otherAudio = null;
                if (setObj != null && setOk != null)
                {
                    s_setAudioObj = AccessTools.MethodDelegate<Action<DatabaseLoader<AudioClip>, AudioClip>>(setObj);
                    s_setAudioOk = AccessTools.MethodDelegate<Action<DatabaseLoader<AudioClip>, bool>>(setOk);
                }
                else
                    audioLoad = null;
                if (audioLoad != null && !OtherLoaderFor(typeof(DatabaseLoader<AudioClip>), typeof(DatabaseLoaderAudio), new[] { "wav", "ogg" }, out otherAudio))
                    Patch(audioLoad, prefix: nameof(AudioLoadPrefix));
                else
                {
                    s_audioPrefetch = false;
                    Log.Info("FastLoader: sounds left to stock" + (audioLoad == null ? " (loader not found)" : " (" + otherAudio + " loads them)"));
                }
            }
            GameDatabase db = GameDatabase.Instance;
            s_preloadPending = db != null && db.databaseTexture.Count == 0 && !db.IsReady();
            Log.Info("FastLoader: first database load: DDS textures read ahead on a background thread, " + _patchedLoaders
                     + " asset loaders continue in the same frame when they finish at once; fast normal maps "
                     + (s_fastNormalMaps ? "verified and on" : "off"));
        }

        protected internal override void OnDisable()
        {
            lock (_lock)
            {
                _abort = true;
                Monitor.PulseAll(_lock);
            }
            s_instance = null;
            s_holdStock = false;
            s_preloadPending = false;
            ReleaseAudioRequests();
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU)
                ReleaseAudioRequests();
        }

        // Runs before each step of the stock asset loop.
        private static bool MoveNextPrefix(object __instance, ref bool __result)
        {
            if (!s_inLoop)
                s_loTick = Stopwatch.GetTimestamp();
            FastLoader self = s_instance;
            if (self != null && s_preloadPending && !s_inLoop)
            {
                // First step after mods were loaded: GameDatabase.root has been rebuilt by now.
                s_preloadPending = false;
                GameDatabase db = GameDatabase.Instance;
                if (db != null && db.root != null)
                {
                    StartAudioPrefetch(db);
                    s_holdStock = true;
                    s_lastPreloadProgress = Time.realtimeSinceStartup;
                    db.StartCoroutine(self.PreloadRunner(db));
                }
            }
            if (s_holdStock && Time.realtimeSinceStartup - s_lastPreloadProgress > HoldWatchdogSeconds)
            {
                s_holdStock = false;   // the preload stopped without handing back: never leave the game loader waiting
                Log.Warn("FastLoader: preload made no progress for " + HoldWatchdogSeconds + " s, resuming the stock loader");
            }
            if (s_holdStock)
            {
                // Stock loop waits (one empty frame per step) while the preload fills the texture database.
                s_iteratorCurrent(__instance) = null;
                __result = true;
                return false;
            }
            s_lastLoaderFinishedSync = false;
            s_driving = true;
            return true;
        }

        // After each step: if the step only waited for a loader that already finished, or for a loading-screen frame,
        // run the next steps right away while this frame's budget lasts. Anything else is left for Unity to wait on.
        private static void MoveNextPostfix(object __instance, ref bool __result)
        {
            bool outer = !s_inLoop;
            try { MoveNextPostfixInner(__instance, ref __result); }
            finally { if (outer) AddWork(Stopwatch.GetTimestamp() - s_loTick); }
        }

        private static void MoveNextPostfixInner(object __instance, ref bool __result)
        {
            s_driving = false;
            FastLoader self = s_instance;
            if (self == null || s_inLoop || s_holdStock || !__result)
                return;
            float now = Time.realtimeSinceStartup;
            if (Time.frameCount != s_frame)
            {
                s_frame = Time.frameCount;
                s_frameStart = now;
            }
            s_inLoop = true;
            try
            {
                var iterator = (IEnumerator)__instance;
                while (__result && Time.realtimeSinceStartup - s_frameStart < self.Budget)
                {
                    object cur = s_iteratorCurrent(__instance);
                    if (!(cur == null || (cur is Coroutine && s_lastLoaderFinishedSync)))
                        break;
                    self._skipped++;
                    s_lastLoaderFinishedSync = false;
                    s_driving = true;
                    __result = iterator.MoveNext();   // nested call: prefix/postfix run with s_inLoop set
                    s_driving = false;
                }
            }
            finally
            {
                s_inLoop = false;
                s_driving = false;
            }
            if (!__result)
            {
                Log.Info("FastLoader: asset loop finished; " + self._skipped + " frame waits skipped"
                         + (s_audioPrefetch ? "; " + s_audioUsed + " sounds from requests started ahead" : "") + "; " + AudioFingerprint());
                ReleaseAudioRequests();
                if (self._pngMode == "verify" && GameDatabase.Instance != null)
                    GameDatabase.Instance.StartCoroutine(self.VerifyPng(GameDatabase.Instance));
            }
        }

        private IEnumerator PreloadRunner(GameDatabase db)
        {
            IEnumerator pre = null;
            try
            {
                if (OtherDdsLoader(out string other))
                    Log.Warn("FastLoader: another DDS loader is installed (" + other + "), leaving textures to it");
                else
                    pre = Preload(db);
            }
            catch (Exception e)
            {
                Log.Warn("FastLoader: setup failed, stock loading: " + e.Message);
            }
            while (pre != null)
            {
                bool more;
                try
                {
                    long t0 = Stopwatch.GetTimestamp();
                    more = pre.MoveNext();
                    AddWork(Stopwatch.GetTimestamp() - t0);
                }
                catch (Exception e)
                {
                    Log.Error("FastLoader: failed, the stock loader takes over for the remaining textures: " + e);
                    StopReader();
                    break;
                }
                if (!more)
                    break;
                yield return pre.Current;
            }
            s_holdStock = false;
        }

        private void PatchCompileLoops()
        {
            foreach (string name in new[] { "CompileParts", "CompileInternalProps", "CompileInternalSpaces" })
            {
                Type iterator = AccessTools.FirstInner(typeof(PartLoader), t => t.Name.Contains("<" + name + ">"));
                MethodInfo moveNext = iterator != null ? AccessTools.Method(iterator, "MoveNext") : null;
                FieldInfo current = iterator != null ? AccessTools.Field(iterator, "<>2__current") : null;
                if (moveNext == null || current == null)
                {
                    Log.Warn("FastLoader: PartLoader." + name + " state machine not found, left stock");
                    continue;
                }
                s_compileCurrent[iterator] = AccessTools.FieldRefAccess<object, object>(current);
                Patch(moveNext, prefix: nameof(CompileMoveNextPrefix), postfix: nameof(CompileMoveNextPostfix));
            }
        }

        private static void CompileMoveNextPrefix()
        {
            if (!s_inCompileLoop)
                s_compileTick = Stopwatch.GetTimestamp();
        }

        private static void CompileMoveNextPostfix(object __instance, ref bool __result)
        {
            bool outer = !s_inCompileLoop;
            try { CompileMoveNextPostfixInner(__instance, ref __result); }
            finally { if (outer) AddWork(Stopwatch.GetTimestamp() - s_compileTick); }
        }

        private static void CompileMoveNextPostfixInner(object __instance, ref bool __result)
        {
            FastLoader self = s_instance;
            if (self == null || s_inCompileLoop || !__result || !s_compileCurrent.TryGetValue(__instance.GetType(), out AccessTools.FieldRef<object, object> current))
                return;
            if (Time.frameCount != s_compileFrame)
            {
                s_compileFrame = Time.frameCount;
                s_compileFrameStart = Time.realtimeSinceStartup;
            }
            s_inCompileLoop = true;
            try
            {
                var iterator = (IEnumerator)__instance;
                while (__result && current(__instance) == null && Time.realtimeSinceStartup - s_compileFrameStart < self.Budget)
                {
                    self._compileSkipped++;
                    __result = iterator.MoveNext();
                }
            }
            finally
            {
                s_inCompileLoop = false;
            }
            if (!__result && self._compileSkipped > 0)
                Log.Info("FastLoader: " + __instance.GetType().Name + " finished; " + self._compileSkipped + " loading-screen frames skipped so far in part compilation");
        }

        // Every concrete DatabaseLoader (stock and mods) gets a transparent probe around its Load coroutine.
        private void PatchLoaders()
        {
            Type[] bases = { typeof(DatabaseLoader<GameDatabase.TextureInfo>), typeof(DatabaseLoader<GameObject>), typeof(DatabaseLoader<AudioClip>) };
            Type[] signature = { typeof(UrlDir.UrlFile), typeof(FileInfo) };
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                Type[] types;
                try { types = loaded.assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (Type t in types)
                {
                    if (t == null || t.IsAbstract || t.ContainsGenericParameters)
                        continue;
                    bool isLoader = false;
                    foreach (Type b in bases)
                        isLoader |= b.IsAssignableFrom(t) && t != b;
                    if (!isLoader)
                        continue;
                    MethodInfo load = AccessTools.DeclaredMethod(t, "Load", signature);
                    if (load == null || load.IsAbstract)
                        continue;
                    Patch(load, postfix: nameof(LoaderLoadPostfix));
                    _patchedLoaders++;
                }
            }
        }

        private static void LoaderLoadPostfix(ref IEnumerator __result)
        {
            if (s_driving && __result != null)
                __result = new SyncProbe(__result);
        }

        // ---------------------------------------------------------------- sounds requested ahead

        private static void StartAudioPrefetch(GameDatabase db)
        {
            if (!s_audioPrefetch)
                return;
            s_audioPending.Clear();
            s_audioNext = 0;
            s_audioUsed = 0;
            foreach (UrlDir.UrlFile f in db.root.GetFiles(UrlDir.FileType.Audio))
            {
                string ext = f.fileExtension;
                if (ext == "wav" || ext == "ogg")
                    s_audioPending.Add(f.fullPath);
            }
            KeepAudioRequests();
            Log.Info("FastLoader: " + s_audioPending.Count + " sounds requested ahead, up to " + s_maxAudioRequests + " at a time");
        }

        private static string AudioKey(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception) { return path; }
        }

        // Keeps up to audioRequests stock audio requests in flight or finished and not yet taken by the stock loop.
        private static void KeepAudioRequests()
        {
            while (s_audioNext < s_audioPending.Count && s_audioRequests.Count < s_maxAudioRequests)
            {
                string path = s_audioPending[s_audioNext++];
                string key = AudioKey(path);
                if (s_audioRequests.ContainsKey(key))
                    continue;
                UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(KSPUtil.ApplicationFileProtocol + path, AudioType.UNKNOWN);
                request.SendWebRequest();
                s_audioRequests[key] = request;
            }
        }

        private static void ReleaseAudioRequests()
        {
            if (s_audioRequests.Count > 0)
                Log.Info("FastLoader: " + s_audioRequests.Count + " sounds requested ahead were not used by the game (another loader?), released");
            foreach (KeyValuePair<string, UnityWebRequest> entry in s_audioRequests)
                entry.Value.Dispose();
            s_audioRequests.Clear();
            s_audioPending.Clear();
            s_audioNext = 0;
        }

        private static bool AudioLoadPrefix(DatabaseLoaderAudio __instance, UrlDir.UrlFile urlFile, FileInfo file, ref IEnumerator __result)
        {
            if (file == null || s_audioRequests.Count == 0)
                return true;
            string key = AudioKey(file.FullName);
            if (!s_audioRequests.TryGetValue(key, out UnityWebRequest request))
                return true;
            s_audioRequests.Remove(key);
            __result = LoadRequestedAudio(__instance, urlFile, request);
            return false;
        }

        // Stock DatabaseLoaderAudio.Load from its web request on.
        private static IEnumerator LoadRequestedAudio(DatabaseLoaderAudio loader, UrlDir.UrlFile urlFile, UnityWebRequest request)
        {
            while (!request.isDone)
                yield return null;
            s_audioUsed++;
            KeepAudioRequests();
            try
            {
                if (!request.isNetworkError && !request.isHttpError)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    s_setAudioObj(loader, clip);
                    s_setAudioOk(loader, clip != null);
                }
                else
                {
                    UnityEngine.Debug.LogWarning("Audio file: " + urlFile.name + " load error: " + request.error);
                    s_setAudioObj(loader, null);
                    s_setAudioOk(loader, false);
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        // Equal fingerprints with audio = false and true: the same sounds, decoded the same way.
        private static string AudioFingerprint()
        {
            GameDatabase db = GameDatabase.Instance;
            if (db == null || db.databaseAudio == null)
                return "no audio database";
            long samples = 0, rate = 0, channels = 0;
            int count = 0;
            foreach (AudioClip clip in db.databaseAudio)
            {
                if (clip == null)
                    continue;
                count++;
                samples += clip.samples;
                rate += clip.frequency;
                channels += clip.channels;
            }
            return "audio fingerprint: " + count + " clips, " + samples + " samples, frequency sum " + rate + ", channel sum " + channels;
        }

        private static bool OtherLoaderFor(Type loaderBase, Type stockLoader, string[] extensions, out string name)
        {
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                Type[] types;
                try { types = loaded.assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (Type t in types)
                {
                    if (t == null || t == stockLoader || t.IsAbstract || !loaderBase.IsAssignableFrom(t))
                        continue;
                    foreach (object attr in t.GetCustomAttributes(typeof(DatabaseLoaderAttrib), true))
                    {
                        if (!(attr is DatabaseLoaderAttrib a) || a.extensions == null)
                            continue;
                        foreach (string ext in extensions)
                        {
                            if (Array.IndexOf(a.extensions, ext) >= 0)
                            {
                                name = t.FullName + " (" + loaded.name + ")";
                                return true;
                            }
                        }
                    }
                }
            }
            name = null;
            return false;
        }

        private static bool OtherDdsLoader(out string name)
        {
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                Type[] types;
                try { types = loaded.assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (Type t in types)
                {
                    if (t == null || t == typeof(DatabaseLoaderTexture_DDS) || t.IsAbstract || !typeof(DatabaseLoader<GameDatabase.TextureInfo>).IsAssignableFrom(t))
                        continue;
                    foreach (object attr in t.GetCustomAttributes(typeof(DatabaseLoaderAttrib), true))
                    {
                        if (attr is DatabaseLoaderAttrib a && a.extensions != null && Array.IndexOf(a.extensions, "dds") >= 0)
                        {
                            name = t.FullName + " (" + loaded.name + ")";
                            return true;
                        }
                    }
                }
            }
            name = null;
            return false;
        }

        private IEnumerator Preload(GameDatabase db)
        {
            // A texture sharing its URL with another texture file (same name, other extension) stays with stock, so the
            // database keeps stock order between the two.
            bool png = _pngMode == "on";
            var urlCount = new Dictionary<string, int>();
            var all = new List<UrlDir.UrlFile>();
            foreach (UrlDir.UrlFile f in db.root.GetFiles(UrlDir.FileType.Texture))
            {
                all.Add(f);
                urlCount.TryGetValue(f.url, out int n);
                urlCount[f.url] = n + 1;
            }
            var files = new List<UrlDir.UrlFile>(all.Count);
            foreach (UrlDir.UrlFile f in all)
            {
                bool handled = f.fileExtension == "dds" || (png && f.fileExtension == "png");
                if (handled && urlCount[f.url] == 1 && !db.ExistsTexture(f.url))
                    files.Add(f);
            }
            if (files.Count == 0)
                yield break;

            // DDS: read by our thread. PNG: decoded on Unity's worker threads by the same web request stock uses, many in
            // flight at once; only the post-processing (compression, mipmaps) runs on the main thread.
            var pngFiles = new List<UrlDir.UrlFile>();
            var ddsFiles = new List<UrlDir.UrlFile>();
            var readPaths = new List<string>();   // per ddsFiles entry: a PNG cache file, or null
            if (png && s_pngCacheAsk)
            {
                // Not decided yet: during a developer benchmark the cache is used as before; otherwise the player is asked,
                // with an estimate for the PNGs not cached yet (none: nothing to ask about yet).
                bool bench = File.Exists(Path.Combine(Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"), "autobench.cfg")) && !s_promptTest;
                var notCached = new List<UrlDir.UrlFile>();
                if (!bench)
                {
                    PngCache.Init(HyperdriveRuntime.ModDirectory, s_pngCacheMinFreeGB);
                    foreach (UrlDir.UrlFile f in files)
                    {
                        if (f.fileExtension != "png")
                            continue;
                        string existing = PngCache.Directory != null ? PngCache.PathFor(f.url) : null;
                        if (existing == null || !File.Exists(existing) || s_promptTest)
                            notCached.Add(f);
                    }
                }
                if (bench)
                    s_pngCache = true;
                else if (notCached.Count > 0)
                {
                    double megapixels = PngCachePrompt.Megapixels(notCached);
                    if (megapixels >= 5.0)
                    {
                        PngCachePrompt.Show(megapixels).Test = s_promptTest;
                        while (PngCachePrompt.Answer == null)
                            yield return null;
                        s_pngCache = PngCachePrompt.Answer == true;
                        s_pngCacheAsk = false;
                        if (!s_pngCache)
                            PngCache.DeleteAll();
                    }
                }
            }
            bool cache = s_pngCache && png;
            if (cache)
            {
                PngCache.Init(HyperdriveRuntime.ModDirectory, s_pngCacheMinFreeGB);
                var keep = new HashSet<string>(StringComparer.Ordinal);
                foreach (UrlDir.UrlFile f in all)
                    if (f.fileExtension == "png" && PngCache.Directory != null)
                        keep.Add(PngCache.PathFor(f.url));
                PngCache.Prune(keep);
            }
            int cachedQueued = 0, cachedLoaded = 0;
            foreach (UrlDir.UrlFile f in files)
            {
                if (f.fileExtension != "png")
                {
                    ddsFiles.Add(f);
                    readPaths.Add(null);
                    continue;
                }
                string cached = cache ? PngCache.PathFor(f.url) : null;
                if (cached != null && File.Exists(cached))
                {
                    ddsFiles.Add(f);   // read with the DDS files
                    readPaths.Add(cached);
                    cachedQueued++;
                }
                else
                    pngFiles.Add(f);
            }
            var pngActive = new List<KeyValuePair<UrlDir.UrlFile, UnityWebRequest>>();
            int pngNext = 0;
            bool ddsDone = ddsFiles.Count == 0;

            var clock = Stopwatch.StartNew();
            _directActive = ddsFiles.Count > 0 && DirectAvailable();
            DirectQueue direct = _directActive ? new DirectQueue() : null;
            lock (_lock)
            {
                _ready.Clear();
                _inFlight = 0;
                _readerDone = false;
                _abort = false;
                _slots = new Item[ddsFiles.Count];
                _claim = 0;
                _publish = 0;
            }
            if (!ddsDone)
            {
                // Half the cores minus one, 1 to 6 (measured on a Mac M4: 4 readers beat 8, the disk is saturated), or readerThreads.
                int readers = s_readerThreads > 0 ? s_readerThreads : Math.Max(1, Math.Min(6, Environment.ProcessorCount / 2 - 1));
                lock (_lock)
                    _readersRunning = readers;
                for (int r = 0; r < readers; r++)
                {
                    var reader = new Thread(() => ReadFiles(ddsFiles, readPaths)) { IsBackground = true, Name = "Hyperdrive.FastLoader" };
                    reader.Start();
                }
            }

            int loaded = 0, leftToStock = 0, frames = 0;
            long bytes = 0;
            float frameEnd = Time.realtimeSinceStartup + Budget;
            var slowest = new List<KeyValuePair<double, string>>();
            var itemClock = new Stopwatch();
            double pngMainMs = 0, ddsMainMs = 0;
            while (true)
            {
                KeepAudioRequests();
                // Keep the PNG requests queue full.
                while (pngActive.Count < s_maxPngRequests && pngNext < pngFiles.Count)
                {
                    UrlDir.UrlFile f = pngFiles[pngNext++];
                    UnityWebRequest request = UnityWebRequestTexture.GetTexture(KSPUtil.ApplicationFileProtocol + f.fullPath);
                    request.SendWebRequest();
                    pngActive.Add(new KeyValuePair<UrlDir.UrlFile, UnityWebRequest>(f, request));
                }

                bool worked = false;
                for (int k = 0; k < pngActive.Count; k++)
                {
                    if (!pngActive[k].Value.isDone)
                        continue;
                    UrlDir.UrlFile f = pngActive[k].Key;
                    UnityWebRequest request = pngActive[k].Value;
                    pngActive.RemoveAt(k);
                    itemClock.Reset();
                    itemClock.Start();
                    bool ok = FinishPngRequest(db, f, request);
                    itemClock.Stop();
                    pngMainMs += itemClock.Elapsed.TotalMilliseconds;
                    request.Dispose();
                    if (itemClock.Elapsed.TotalMilliseconds > 15.0)
                        slowest.Add(new KeyValuePair<double, string>(itemClock.Elapsed.TotalMilliseconds, f.url + ".png"));
                    if (ok) loaded++; else leftToStock++;
                    worked = true;
                    break;
                }

                if (direct != null && direct.Items.Count > 0)
                {
                    itemClock.Reset();
                    itemClock.Start();
                    if (PumpDirect(db, direct))
                        worked = true;
                    itemClock.Stop();
                    ddsMainMs += itemClock.Elapsed.TotalMilliseconds;
                    if (direct.RetryPng.Count > 0)
                    {
                        pngFiles.AddRange(direct.RetryPng);
                        direct.RetryPng.Clear();
                    }
                }

                if (!ddsDone && (direct == null || !direct.Full))
                {
                    Item item = null;
                    lock (_lock)
                    {
                        if (_ready.Count > 0)
                            item = _ready.Dequeue();
                        else if (_readerDone || _abort)
                            ddsDone = true;
                        else if (!worked && pngActive.Count == 0 && (direct == null || direct.Items.Count == 0))
                            Monitor.Wait(_lock, 5);   // nothing else to do: wait for the disk
                    }
                    if (item != null && item.HeaderOnly)
                    {
                        itemClock.Reset();
                        itemClock.Start();
                        bool fromCache = item.ReadPath != null;
                        bool begun = item.Error == null && (fromCache ? BeginDirectCached(db, item, direct) : BeginDirect(db, item, direct));
                        itemClock.Stop();
                        ddsMainMs += itemClock.Elapsed.TotalMilliseconds;
                        if (!begun && fromCache)
                            pngFiles.Add(item.File);   // stale or damaged cache file: the PNG is decoded as before
                        else if (!begun)
                        {
                            leftToStock++;
                            if (item.Error != null)
                                Log.Warn("FastLoader: could not read " + item.File.url + " (" + item.Error.Message + "), stock loader will handle it");
                        }
                        Release(item);   // the header's buffer; the queue keeps the item
                        worked = true;
                    }
                    else if (item != null && direct != null && direct.Items.Count > 0 && item.Error == null && item.ReadPath != null)
                    {
                        // A cached PNG behind direct DDS still being read: built in its turn, so file order is kept.
                        item.State = DirectState.Cached;
                        direct.Items.Enqueue(item);
                        direct.Bytes += item.FileLength;
                        worked = true;
                    }
                    else if (item != null)
                    {
                        itemClock.Reset();
                        itemClock.Start();
                        bool fromCache = item.ReadPath != null;
                        bool built = item.Error == null && (fromCache ? BuildCached(db, item) : Build(db, item));
                        itemClock.Stop();
                        ddsMainMs += itemClock.Elapsed.TotalMilliseconds;
                        if (itemClock.Elapsed.TotalMilliseconds > 15.0)
                            slowest.Add(new KeyValuePair<double, string>(itemClock.Elapsed.TotalMilliseconds, item.File.url + "." + item.File.fileExtension));
                        if (built)
                        {
                            loaded++;
                            bytes += item.Length;
                            if (fromCache)
                                cachedLoaded++;
                        }
                        else if (fromCache)
                        {
                            // Stale or damaged cache file: the PNG is decoded as before (and cached again).
                            pngFiles.Add(item.File);
                        }
                        else
                        {
                            leftToStock++;
                            if (item.Error != null)
                                Log.Warn("FastLoader: could not read " + item.File.url + " (" + item.Error.Message + "), stock loader will handle it");
                        }
                        Release(item);
                        worked = true;
                    }
                }

                int directCount = direct != null ? direct.Items.Count : 0;
                if (ddsDone && directCount == 0 && pngNext >= pngFiles.Count && pngActive.Count == 0)
                    break;
                if (worked)
                {
                    s_lastPreloadProgress = Time.realtimeSinceStartup;
                    if (s_progressFraction != null)
                        s_progressFraction(db) = (float)(loaded + leftToStock + (direct != null ? direct.Loaded + direct.LeftToStock : 0)) / files.Count;
                }

                // Requests and direct reads progress between frames: when only waiting for them, give the frame back at once.
                if (Time.realtimeSinceStartup > frameEnd || (!worked && (ddsDone || pngActive.Count > 0 || directCount > 0)))
                {
                    frames++;
                    yield return null;
                    frameEnd = Time.realtimeSinceStartup + Budget;
                }
            }

            lock (_lock)
                _freeBuffers.Clear();
            if (direct != null)
            {
                StopPixelThreads();
                loaded += direct.Loaded;
                bytes += direct.LoadedBytes;
                leftToStock += direct.LeftToStock;
                cachedLoaded += direct.CachedLoaded;
            }

            // Textures already in the database are skipped by the stock loop only if they are not newer than the last load.
            s_lastLoadTime.SetValue(db, KSPUtil.SystemDateTime.DateTimeNow());
            Log.Info("FastLoader: " + loaded + " textures (" + (bytes / (1024 * 1024)) + " MB) loaded in " + clock.Elapsed.TotalSeconds.ToString("F2")
                     + " s over " + (frames + 1) + " frames; " + leftToStock + " left to the stock loader; main thread "
                     + (ddsMainMs / 1000).ToString("F2") + " s building DDS, " + (pngMainMs / 1000).ToString("F2") + " s finishing PNG"
                     + (cache ? "; PNG cache: " + cachedLoaded + " of " + cachedQueued + " cached textures used" : ""));
            if (direct != null)
                Log.Info(DirectSummary(direct));
            if (slowest.Count > 0)
            {
                slowest.Sort((a, b) => b.Key.CompareTo(a.Key));
                var sb = new System.Text.StringBuilder("FastLoader: " + slowest.Count + " textures took more than 15 ms on the main thread; slowest:");
                for (int i = 0; i < slowest.Count && i < 12; i++)
                    sb.Append(' ').Append(slowest[i].Value).Append(' ').Append(slowest[i].Key.ToString("F0")).Append(" ms;");
                Log.Info(sb.ToString());
            }
        }

        // Main thread: same steps as stock DatabaseLoaderTexture_DDS, without the extra copies.
        private static bool Build(GameDatabase db, Item item)
        {
            UrlDir.UrlFile file = item.File;
            string url = file.url;
            if (!ParseDds(item, out DDSHeader header, out long dataOffset, out TextureFormat format, out bool mipChain, out bool isNormalMap))
                return false;

            UnityEngine.Debug.Log("Load(Texture): " + url);
            if (s_progressTitle != null)
                s_progressTitle(db) = url;

            Texture2D texture = null;
            GCHandle handle = default;
            try
            {
                texture = new Texture2D((int)header.dwWidth, (int)header.dwHeight, format, mipChain);
                handle = GCHandle.Alloc(item.Buffer, GCHandleType.Pinned);
                texture.LoadRawTextureData(new IntPtr(handle.AddrOfPinnedObject().ToInt64() + dataOffset), (int)(item.Length - dataOffset));
                handle.Free();
                handle = default;
                texture.Apply(false, true);
            }
            catch (Exception e)
            {
                if (handle.IsAllocated)
                    handle.Free();
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
                Log.Warn("FastLoader: " + url + " failed (" + e.Message + "), stock loader will handle it");
                return false;
            }

            var info = new GameDatabase.TextureInfo(file, texture, isNormalMap, false, true);
            info.name = url;
            texture.name = url;
            db.databaseTexture.Add(info);
            return true;
        }

        // A PNG's texture from its cache file (PngCache): the bytes, format, size, mips and flags it had when it was decoded.
        private static bool BuildCached(GameDatabase db, Item item)
        {
            UrlDir.UrlFile file = item.File;
            GameDatabase.TextureInfo info = CreateCachedInfo(file, item.Buffer, item.Length, item.SourceLength, item.SourceTicks);
            if (info == null)
                return false;
            UnityEngine.Debug.Log("Load(Texture): " + file.url);
            if (s_progressTitle != null)
                s_progressTitle(db) = file.url;
            info.name = file.url;
            info.texture.name = file.url;
            db.databaseTexture.Add(info);
            return true;
        }

        private static GameDatabase.TextureInfo CreateCachedInfo(UrlDir.UrlFile file, byte[] buffer, int length, long sourceLength, long sourceTicks)
        {
            if (!PngCache.TryParse(buffer, length, file.url, sourceLength, sourceTicks, out PngCache.Header h, out int offset))
                return null;
            Texture2D texture = null;
            GCHandle handle = default;
            try
            {
                texture = new Texture2D(h.Width, h.Height, (TextureFormat)h.Format, h.MipCount > 1);
                if (texture.mipmapCount != h.MipCount || texture.format != (TextureFormat)h.Format)
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }
                handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                texture.LoadRawTextureData(new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset), h.DataLength);
                handle.Free();
                handle = default;
                texture.Apply(false, (h.Flags & PngCache.FlagTextureReadable) == 0);
            }
            catch (Exception e)
            {
                if (handle.IsAllocated)
                    handle.Free();
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
                Log.Debug("FastLoader: PNG cache of " + file.url + " not used (" + e.Message + ")");
                return null;
            }
            return new GameDatabase.TextureInfo(file, texture, (h.Flags & PngCache.FlagNormal) != 0, (h.Flags & PngCache.FlagInfoReadable) != 0,
                                                (h.Flags & PngCache.FlagCompressed) != 0);
        }

        // ---------------------------------------------------------------- normal maps

        // Stock GameDatabase.BitmapToUnityNormalMap reads and writes every pixel with GetPixel/SetPixel (two native calls
        // per pixel: about a second for a 2048x2048 map). Same result with one array transfer each way: the output pixel is
        // (g, g, g, r) of the input, in an RGBA32 texture with mipmaps, repeat wrapping, applied non-readable, input destroyed.
        private static bool BitmapToUnityNormalMapPrefix(Texture2D tex, ref Texture2D __result)
        {
            if (tex == null || !s_fastNormalMaps)
                return true;
            try
            {
                Color32[] pixels = tex.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 p = pixels[i];
                    pixels[i] = new Color32(p.g, p.g, p.g, p.r);
                }
                var normal = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
                normal.SetPixels32(pixels);
                normal.Apply(true, true);
                UnityEngine.Object.Destroy(tex);
                __result = normal;
                return false;
            }
            catch (Exception)
            {
                return true;   // e.g. a non-readable input: let stock report it
            }
        }

        // Proof on this machine before use: every (r, g) byte pair through the stock per-pixel path and through the
        // array path must give the same bytes, for both 8-bit source layouts.
        private static bool NormalMapSelfTest()
        {
            try
            {
                foreach (TextureFormat format in new[] { TextureFormat.ARGB32, TextureFormat.RGBA32 })
                {
                    var src = new Texture2D(256, 256, format, false);
                    var input = new Color32[256 * 256];
                    for (int y = 0; y < 256; y++)
                        for (int x = 0; x < 256; x++)
                            input[y * 256 + x] = new Color32((byte)x, (byte)y, (byte)(x ^ y), (byte)(255 - x));
                    src.SetPixels32(input);
                    src.Apply(false, false);

                    var stock = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                    Color white = Color.white;
                    for (int x = 0; x < 256; x++)
                        for (int y = 0; y < 256; y++)
                        {
                            Color pixel = src.GetPixel(x, y);
                            white.r = pixel.g; white.g = pixel.g; white.b = pixel.g; white.a = pixel.r;
                            stock.SetPixel(x, y, white);
                        }
                    Color32[] expected = stock.GetPixels32();
                    Color32[] actual = src.GetPixels32();
                    UnityEngine.Object.Destroy(src);
                    UnityEngine.Object.Destroy(stock);
                    for (int i = 0; i < actual.Length; i++)
                    {
                        Color32 p = actual[i], e = expected[i];
                        if (e.r != p.g || e.g != p.g || e.b != p.g || e.a != p.r)
                        {
                            Log.Warn("FastLoader: fast normal map conversion differs from stock on this machine (" + format + "), stock kept");
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("FastLoader: normal map self-test failed (" + e.Message + "), stock conversion kept");
                return false;
            }
        }

        // ---------------------------------------------------------------- PNG (same steps as stock DatabaseLoaderTexture_PNG)

        // Stock DatabaseLoaderTexture_PNG from the completed web request on: same content, same steps.
        private static bool FinishPngRequest(GameDatabase db, UrlDir.UrlFile file, UnityWebRequest request)
        {
            if (request.error != null || request.isNetworkError || request.isHttpError)
                return false;   // stock retries it and reports the error itself
            string url = file.url;
            UnityEngine.Debug.Log("Load(Texture): " + url);
            if (s_progressTitle != null)
                s_progressTitle(db) = url;
            GameDatabase.TextureInfo info;
            try
            {
                Texture2D content = DownloadHandlerTexture.GetContent(request);
                if (content == null)
                    return false;
                info = FinishPng(file, content, () => request.downloadHandler.data);
            }
            catch (Exception e)
            {
                Log.Warn("FastLoader: " + url + " failed (" + e.Message + "), stock loader will handle it");
                return false;
            }
            info.name = url;
            info.texture.name = url;
            db.databaseTexture.Add(info);
            if (s_pngCache)
                PngCache.Save(file, info);
            return true;
        }

        // Mirrors DatabaseLoaderTexture_PNG after its web request: decoded image, normal map conversion for *NRM files,
        // full-quality copy for Flags, uncompressed ARGB32 copy for Icons/Tutorials/SimpleIcons, DXT compression when the
        // size allows it, mipmaps, readable. Returns null when the image cannot be decoded (stock then reports it).
        private static GameDatabase.TextureInfo CreatePngInfo(UrlDir.UrlFile file, byte[] bytes)
        {
            var content = new Texture2D(2, 2, TextureFormat.ARGB32, false);   // the web request decodes without mipmaps
            if (!content.LoadImage(bytes, false))
            {
                UnityEngine.Object.Destroy(content);
                return null;
            }
            return FinishPng(file, content, () => bytes);
        }

        private static GameDatabase.TextureInfo FinishPng(UrlDir.UrlFile file, Texture2D content, Func<byte[]> fileBytes)
        {
            string path = KSPUtil.ApplicationFileProtocol + file.fullPath;
            if (Path.GetFileNameWithoutExtension(file.fullPath).EndsWith("NRM"))
            {
                Texture2D normal = GameDatabase.BitmapToUnityNormalMap(content);   // destroys content
                return new GameDatabase.TextureInfo(file, normal, true, false, true);
            }

            Texture2D texture = content;
            char sep = Path.DirectorySeparatorChar;
            bool icon = path.Contains(sep + "Icons" + sep) || path.Contains(sep + "Tutorials" + sep) || path.Contains(sep + "SimpleIcons" + sep);
            bool highQuality = false;
            if (path.Contains(sep + "Flags" + sep))
            {
                var flag = new Texture2D(texture.width, texture.height, texture.format, true);
                flag.LoadImage(fileBytes());
                UnityEngine.Object.Destroy(texture);
                texture = flag;
                highQuality = true;
            }
            if (icon)
            {
                var copy = new Texture2D(texture.width, texture.height, TextureFormat.ARGB32, false);
                copy.SetPixels32(texture.GetPixels32());
                UnityEngine.Object.Destroy(texture);
                texture = copy;
            }
            if (texture.width % 4 == 0 && texture.height % 4 == 0)
                texture.Compress(highQuality);
            else
                UnityEngine.Debug.LogWarning("Texture resolution is not valid for compression: '" + file.fullPath + "' - consider changing the image's width and height to enable compression");
            texture.Apply(true);
            return new GameDatabase.TextureInfo(file, texture, false, true, true);
        }

        // Developer check (png = verify): after the stock loop, rebuild every PNG with the path above and compare it with
        // what stock loaded: size, format, mip count, flags, and the pixel bytes of readable textures.
        private IEnumerator VerifyPng(GameDatabase db)
        {
            var clock = Stopwatch.StartNew();
            int same = 0, different = 0, skipped = 0, cacheChecked = 0;
            if (s_pngCache && PngCache.Directory == null)
                PngCache.Init(HyperdriveRuntime.ModDirectory, s_pngCacheMinFreeGB);
            var details = new List<string>();
            var formats = new Dictionary<string, int>();
            foreach (UrlDir.UrlFile f in db.root.GetFiles(UrlDir.FileType.Texture))
            {
                if (f.fileExtension != "png")
                    continue;
                GameDatabase.TextureInfo stock = db.GetTextureInfo(f.url);
                if (stock == null || stock.file == null || stock.file.fullPath != f.fullPath)
                {
                    skipped++;
                    continue;
                }
                GameDatabase.TextureInfo mine = null;
                string why = null;
                try
                {
                    mine = CreatePngInfo(f, File.ReadAllBytes(f.fullPath));
                    why = Compare(stock, mine);
                    string cacheFile = s_pngCache && PngCache.Directory != null ? PngCache.PathFor(f.url) : null;
                    if (why == null && cacheFile != null && File.Exists(cacheFile))
                    {
                        byte[] cached = File.ReadAllBytes(cacheFile);
                        var source = new FileInfo(f.fullPath);
                        GameDatabase.TextureInfo fromCache = CreateCachedInfo(f, cached, cached.Length, source.Length, source.LastWriteTimeUtc.Ticks);
                        why = fromCache == null ? "cache file not usable" : Compare(stock, fromCache);
                        if (why != null)
                            why = "cache: " + why;
                        else
                            cacheChecked++;
                        if (fromCache != null && fromCache.texture != null)
                            UnityEngine.Object.Destroy(fromCache.texture);
                    }
                }
                catch (Exception e)
                {
                    why = "exception " + e.Message;
                }
                Texture2D st = stock.texture;
                string key = st.format + (stock.isNormalMap ? " normal" : "") + " mips " + (st.mipmapCount > 1);
                formats.TryGetValue(key, out int n);
                formats[key] = n + 1;
                if (why == null) same++;
                else
                {
                    different++;
                    if (details.Count < 15)
                        details.Add(f.url + ": " + why);
                }
                if (mine != null && mine.texture != null)
                    UnityEngine.Object.Destroy(mine.texture);
                if ((same + different) % 40 == 0)
                    yield return null;
            }
            var sb = new System.Text.StringBuilder();
            sb.Append(Log.Prefix).Append("FastLoader PNG verify: ").Append(same).Append(" identical, ").Append(different).Append(" different, ")
              .Append(skipped).Append(" skipped (").Append(cacheChecked).Append(" cache files identical too), in ").Append(clock.Elapsed.TotalSeconds.ToString("F1")).Append(" s. Stock formats:");
            foreach (KeyValuePair<string, int> kv in formats)
                sb.Append(" [").Append(kv.Key).Append("] ").Append(kv.Value);
            foreach (string d in details)
                sb.Append("\n  ").Append(d);
            UnityEngine.Debug.Log(sb.ToString());
        }

        private static string Compare(GameDatabase.TextureInfo a, GameDatabase.TextureInfo b)
        {
            if (b == null)
                return "not decoded";
            Texture2D ta = a.texture, tb = b.texture;
            if (ta.width != tb.width || ta.height != tb.height)
                return "size " + ta.width + "x" + ta.height + " vs " + tb.width + "x" + tb.height;
            if (ta.format != tb.format)
                return "format " + ta.format + " vs " + tb.format;
            if (ta.mipmapCount != tb.mipmapCount)
                return "mips " + ta.mipmapCount + " vs " + tb.mipmapCount;
            if (a.isNormalMap != b.isNormalMap || a.isReadable != b.isReadable || a.isCompressed != b.isCompressed)
                return "flags";
            if (ta.isReadable && tb.isReadable)
            {
                byte[] ra = ta.GetRawTextureData(), rb = tb.GetRawTextureData();
                if (ra.Length != rb.Length)
                    return "data length " + ra.Length + " vs " + rb.Length;
                for (int i = 0; i < ra.Length; i++)
                    if (ra[i] != rb[i])
                        return "pixel bytes differ at " + i + " of " + ra.Length;
            }
            return null;
        }

        // ---------------------------------------------------------------- reader thread (pure C#, no Unity API)

        private void ReadFiles(List<UrlDir.UrlFile> files, List<string> readPaths)
        {
            while (true)
            {
                byte[] buffer;
                int i;
                lock (_lock)
                {
                    while (_inFlight >= MaxInFlight && !_abort)
                        Monitor.Wait(_lock);
                    if (_abort || _claim >= files.Count)
                        break;
                    i = _claim++;
                    _inFlight++;
                    buffer = _freeBuffers.Count > 0 ? _freeBuffers.Pop() : null;
                }

                var item = new Item { File = files[i], ReadPath = readPaths[i] };
                try
                {
                    if (item.ReadPath != null)
                    {
                        // PNG cache file: the PNG's size and time, to check the cache against on the main thread.
                        var source = new FileInfo(files[i].fullPath);
                        item.SourceLength = source.Length;
                        item.SourceTicks = source.LastWriteTimeUtc.Ticks;
                    }
                    using (var fs = new FileStream(item.ReadPath ?? files[i].fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                    {
                        long length = fs.Length;
                        if (length > int.MaxValue)
                            throw new IOException("file too large");
                        item.FileLength = length;
                        if (_directActive)
                        {
                            // Direct path (FastLoader.Direct): the header only; the pixels are read into the texture itself.
                            int probe = (int)Math.Min(length, item.ReadPath == null ? HeaderBytes : CacheProbeBytes);
                            if (buffer == null)
                                buffer = new byte[item.ReadPath == null ? HeaderBytes : CacheProbeBytes];
                            int n = ReadFully(fs, buffer, probe);
                            if (item.ReadPath == null || PngCache.HeaderFits(buffer, n))
                            {
                                item.HeaderOnly = true;
                                item.Length = n;
                            }
                            else
                                fs.Position = 0;
                        }
                        if (!item.HeaderOnly)
                        {
                            if (buffer == null || buffer.Length < length)
                                buffer = new byte[RoundUp((int)length)];
                            item.Length = ReadFully(fs, buffer, (int)length);
                        }
                    }
                }
                catch (Exception e)
                {
                    item.Error = e;
                }
                item.Buffer = buffer;

                lock (_lock)
                {
                    _slots[i] = item;
                    // In file order: the main thread builds the textures in the order stock loads them.
                    while (_publish < _slots.Length && _slots[_publish] != null)
                    {
                        _ready.Enqueue(_slots[_publish]);
                        _slots[_publish] = null;
                        _publish++;
                    }
                    Monitor.PulseAll(_lock);
                }
            }
            lock (_lock)
            {
                if (--_readersRunning <= 0)
                    _readerDone = true;
                Monitor.PulseAll(_lock);
            }
        }

        private static int ReadFully(FileStream fs, byte[] buffer, int length)
        {
            int read = 0;
            while (read < length)
            {
                int n = fs.Read(buffer, read, length - read);
                if (n <= 0)
                    throw new EndOfStreamException();
                read += n;
            }
            return read;
        }

        private void Release(Item item)
        {
            lock (_lock)
            {
                if (item.Buffer != null && item.Buffer.Length >= 1 << 20)
                    _freeBuffers.Push(item.Buffer);
                item.Buffer = null;
                _inFlight--;
                Monitor.PulseAll(_lock);
            }
        }

        private void StopReader()
        {
            lock (_lock)
            {
                _abort = true;
                Monitor.PulseAll(_lock);
            }
        }

        private static int RoundUp(int length) => (length + 0xFFFFF) & ~0xFFFFF;   // whole MB, so buffers are reused
    }
}
