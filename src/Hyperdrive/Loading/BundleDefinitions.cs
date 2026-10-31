using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;
using UnityEngine.Networking;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// First load: KSPAssets' AssetLoader.LoadDefinitionsAsync opens every asset bundle of GameData one after the other
    /// (226 in a stock install) to read its definition: one UnityWebRequestAssetBundle per file, at least one frame of
    /// waiting each, ~5.6 s on a Mac. Here the same requests (same URI, same call) are sent ahead, a few at a time, while
    /// the loop works through the list in its own order; when the loop reaches a file it takes the request already sent,
    /// and a step that only waited for something already finished goes on within the same frame (frame budget kept).
    /// Everything else is the loop's own code: same bundles read in the same order, same definitions, same logs. A
    /// request sent ahead that failed is dropped and the loop sends its own at its turn, as stock.
    /// The loop also writes some bundles' platform dependency next to them (File.Open read-write, no sharing, e.g.
    /// Squad/squadcorefx.ksp). On Windows that write fails while a request sent ahead holds the same file open: the
    /// coroutine dies and the load never ends (PC, without KSPCF). The write goes through OpenForWrite, which closes our
    /// request on that file first; files written this way are never sent ahead again (kept in PluginData across starts).
    /// </summary>
    internal sealed class BundleDefinitions : Subsystem
    {
        private sealed class Ahead
        {
            public UnityWebRequest Request;
            public UnityWebRequestAsyncOperation Operation;
        }

        private static BundleDefinitions s_instance;
        private static int s_maxAhead = 64;   // 16 -> 64: definitions 2.05-2.28 -> 1.85 s on the heavy test modpack
        private static float s_fallbackBudget = 0.008f;
        private static readonly Dictionary<string, Ahead> s_ahead = new Dictionary<string, Ahead>(StringComparer.Ordinal);
        private static readonly Dictionary<UnityWebRequest, UnityWebRequestAsyncOperation> s_handed = new Dictionary<UnityWebRequest, UnityWebRequestAsyncOperation>();
        private static AccessTools.FieldRef<object, object> s_current;
        private static AccessTools.FieldRef<object, List<FileInfo>> s_files;
        private static AccessTools.FieldRef<object, int> s_index;
        private static AccessTools.FieldRef<object, UnityWebRequest> s_request;
        private static MethodInfo s_getAssetBundle, s_send, s_getHelper, s_sendHelper, s_open, s_openHelper;
        // Files the loop writes (dependencies), compared without case: never sent ahead. Kept across starts.
        private static readonly HashSet<string> s_written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> s_pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static int s_writtenAtStart, s_released;
        private const string WrittenFile = "BundleDefinitions.txt";
        private static int s_replaced;
        private static bool s_inLoop, s_done;
        private static int s_frame = -1;
        private static float s_frameStart;
        private static int s_prefetchedUsed, s_prefetchedFailed, s_skipped, s_steps;
        private static readonly Stopwatch s_clock = new Stopwatch();

        public override string Name => "BundleDefinitions";

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_maxAhead = Mathf.Clamp(settings.GetInt("requestsAhead", s_maxAhead), 1, 64);
        }

        protected internal override void OnEnable()
        {
            if (HighLogic.LoadedScene != GameScenes.LOADING)
                throw new SubsystemUnavailableException("only acts on the first load");
            Type loader = AccessTools.TypeByName("KSPAssets.Loaders.AssetLoader");
            Type machine = loader != null ? AccessTools.FirstInner(loader, t => t.Name.Contains("<LoadDefinitionsAsync>")) : null;
            MethodInfo moveNext = machine != null ? AccessTools.Method(machine, "MoveNext") : null;
            FieldInfo current = machine != null ? AccessTools.Field(machine, "<>2__current") : null;
            FieldInfo files = machine != null ? AccessTools.Field(machine, "<files>5__7") : null;
            FieldInfo index = machine != null ? AccessTools.Field(machine, "<i>5__8") : null;
            FieldInfo request = machine != null ? AccessTools.Field(machine, "<uwr>5__11") : null;
            s_getAssetBundle = AccessTools.Method(typeof(UnityWebRequestAssetBundle), nameof(UnityWebRequestAssetBundle.GetAssetBundle), new[] { typeof(string) });
            s_send = AccessTools.Method(typeof(UnityWebRequest), nameof(UnityWebRequest.SendWebRequest));
            s_getHelper = AccessTools.Method(typeof(BundleDefinitions), nameof(GetAssetBundle));
            s_sendHelper = AccessTools.Method(typeof(BundleDefinitions), nameof(Send));
            s_open = AccessTools.Method(typeof(File), nameof(File.Open), new[] { typeof(string), typeof(FileMode), typeof(FileAccess) });
            s_openHelper = AccessTools.Method(typeof(BundleDefinitions), nameof(OpenForWrite));
            if (moveNext == null || current == null || files == null || index == null || request == null || s_getAssetBundle == null || s_send == null || s_open == null
                || files.FieldType != typeof(List<FileInfo>) || index.FieldType != typeof(int) || request.FieldType != typeof(UnityWebRequest))
                throw new SubsystemUnavailableException("AssetLoader.LoadDefinitionsAsync not as expected (game version mismatch?)");
            s_current = AccessTools.FieldRefAccess<object, object>(current);
            s_files = AccessTools.FieldRefAccess<object, List<FileInfo>>(files);
            s_index = AccessTools.FieldRefAccess<object, int>(index);
            s_request = AccessTools.FieldRefAccess<object, UnityWebRequest>(request);

            LoadWritten();
            s_replaced = 0;
            Patch(moveNext, prefix: nameof(MoveNextPrefix), postfix: nameof(MoveNextPostfix), transpiler: nameof(Transpiler));
            if (s_replaced != 3)
                throw new InvalidOperationException("expected one GetAssetBundle, one SendWebRequest and one File.Open call in LoadDefinitionsAsync, replaced " + s_replaced);
            s_instance = this;
            Log.Info("BundleDefinitions: asset bundle definitions read with up to " + s_maxAhead + " requests sent ahead");
        }

        protected internal override void OnDisable()
        {
            s_instance = null;
            Release();
            FlushPending();
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU)
            {
                Release();
                FlushPending();
                s_instance = null;
            }
        }

        // IL: "call UnityWebRequestAssetBundle::GetAssetBundle(string)" and "callvirt UnityWebRequest::SendWebRequest()":
        // replaced by static helpers with the same stack shape ([string] -> request, [request] -> operation).
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo m)
                {
                    if (m == s_getAssetBundle)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = s_getHelper;
                        s_replaced++;
                    }
                    else if (m == s_send)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = s_sendHelper;
                        s_replaced++;
                    }
                    else if (m == s_open)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = s_openHelper;
                        s_replaced++;
                    }
                }
                yield return instruction;
            }
        }

        private static UnityWebRequest GetAssetBundle(string uri)
        {
            if (s_instance != null && uri != null && s_ahead.TryGetValue(uri, out Ahead ahead))
            {
                s_ahead.Remove(uri);
                // A request that already failed (a bundle with the same files was open at the time) is dropped: the loop
                // sends its own now, at its stock turn.
                if (!(ahead.Request.isDone && (ahead.Request.isNetworkError || ahead.Request.isHttpError || !string.IsNullOrEmpty(ahead.Request.error))))
                {
                    s_handed[ahead.Request] = ahead.Operation;
                    s_prefetchedUsed++;
                    return ahead.Request;
                }
                ahead.Request.Dispose();
                s_prefetchedFailed++;
            }
            return UnityWebRequestAssetBundle.GetAssetBundle(uri);
        }

        // The loop's File.Open(path, mode, access) for a dependency it writes: our request on that file, if any, is closed
        // first (its bundle unloaded, which closes the file at once), then the stock call, unchanged.
        private static FileStream OpenForWrite(string path, FileMode mode, FileAccess access)
        {
            if (s_instance != null && path != null)
            {
                string uri = "file://" + Path.GetFullPath(path);
                s_written.Add(uri);
                string held = null;
                foreach (string key in s_ahead.Keys)
                    if (string.Equals(key, uri, StringComparison.OrdinalIgnoreCase))
                    {
                        held = key;
                        break;
                    }
                if (held != null)
                {
                    UnityWebRequest request = s_ahead[held].Request;
                    s_ahead.Remove(held);
                    try
                    {
                        if (request.isDone && string.IsNullOrEmpty(request.error))
                            DownloadHandlerAssetBundle.GetContent(request)?.Unload(true);
                        else if (!request.isDone)
                            request.Abort();
                    }
                    catch (Exception) { }
                    request.Dispose();
                    s_released++;
                }
            }
            try
            {
                return File.Open(path, mode, access);
            }
            catch (IOException e) when (s_instance != null && path != null && File.Exists(path))
            {
                // Still held: a request sent ahead that had not finished keeps the file open until Unity's loading thread is
                // done with it, and the coroutine would die here (first start on a fresh install, Windows, without KSPCF).
                // The loop writes a side file instead, copied over the real one once the requests are gone. The game writes
                // the same platform dependency at every start, and this file is never sent ahead again (s_written).
                string side = path + ".hyperdrive-pending";
                s_pending[path] = side;
                Log.Warn("BundleDefinitions: " + Path.GetFileName(path) + " still open (" + e.Message + "), written next to it and copied over it later");
                return File.Open(side, FileMode.Create, access);
            }
        }

        // Side files written while their bundle was still open: copied over the real file (and compared, for the log).
        private static void FlushPending()
        {
            if (s_pending.Count == 0)
                return;
            foreach (KeyValuePair<string, string> entry in new List<KeyValuePair<string, string>>(s_pending))
            {
                try
                {
                    bool same = File.Exists(entry.Key) && SameBytes(entry.Key, entry.Value);
                    File.Copy(entry.Value, entry.Key, true);
                    File.Delete(entry.Value);
                    s_pending.Remove(entry.Key);
                    Log.Info("BundleDefinitions: " + Path.GetFileName(entry.Key) + " written from its side file (" + (same ? "identical to the file read this start" : "differed from the file read this start") + ")");
                }
                catch (Exception e) { Log.Warn("BundleDefinitions: " + Path.GetFileName(entry.Key) + " not written yet (" + e.Message + "), tried again later"); }
            }
        }

        private static bool SameBytes(string a, string b)
        {
            byte[] x = File.ReadAllBytes(a), y = File.ReadAllBytes(b);
            if (x.Length != y.Length)
                return false;
            for (int i = 0; i < x.Length; i++)
                if (x[i] != y[i])
                    return false;
            return true;
        }

        private static void LoadWritten()
        {
            s_written.Clear();
            try
            {
                string file = Path.Combine(Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"), WrittenFile);
                if (File.Exists(file))
                    foreach (string line in File.ReadAllLines(file))
                        if (line.StartsWith("file://", StringComparison.Ordinal))
                            s_written.Add(line.Trim());
            }
            catch (Exception e) { Log.Warn("BundleDefinitions: could not read " + WrittenFile + " (" + e.Message + ")"); }
            s_writtenAtStart = s_written.Count;
        }

        private static void SaveWritten()
        {
            if (s_written.Count == s_writtenAtStart)
                return;
            try
            {
                string dir = Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData");
                Directory.CreateDirectory(dir);
                var lines = new List<string> { "// Hyperdrive: asset bundles the game writes while reading definitions; never read ahead. Safe to delete." };
                lines.AddRange(s_written);
                File.WriteAllLines(Path.Combine(dir, WrittenFile), lines.ToArray());
            }
            catch (Exception e) { Log.Warn("BundleDefinitions: could not write " + WrittenFile + " (" + e.Message + ")"); }
        }

        private static UnityWebRequestAsyncOperation Send(UnityWebRequest request)
        {
            if (request != null && s_handed.TryGetValue(request, out UnityWebRequestAsyncOperation operation))
            {
                s_handed.Remove(request);
                return operation;
            }
            return request.SendWebRequest();
        }

        // Keeps up to requestsAhead requests sent for the files after the one the loop is on (the list may grow while it
        // runs: it is read again at every step).
        private static void SendAhead(object machine)
        {
            List<FileInfo> files = s_files(machine);
            if (files == null)
                return;
            int i = s_index(machine);
            for (int k = Math.Max(0, i + 1); k < files.Count && s_ahead.Count < s_maxAhead; k++)
            {
                FileInfo file = files[k];
                if (file == null)
                    continue;
                string uri = "file://" + file.FullName;
                if (s_ahead.ContainsKey(uri) || s_written.Contains(uri))
                    continue;
                UnityWebRequest request = UnityWebRequestAssetBundle.GetAssetBundle(uri);
                s_ahead[uri] = new Ahead { Request = request, Operation = request.SendWebRequest() };
            }
        }

        private static bool MoveNextPrefix(object __instance)
        {
            if (s_instance == null || s_inLoop)
                return true;
            if (!s_clock.IsRunning)
                s_clock.Start();
            try { SendAhead(__instance); }
            catch (Exception e) { Log.Warn("BundleDefinitions: requests ahead stopped (" + e.Message + ")"); Release(); s_instance = null; }
            return true;
        }

        // A step that only waits for a request or an asset load already finished, or for the request it just took, goes on
        // in the same frame while the frame budget lasts.
        private static void MoveNextPostfix(object __instance, ref bool __result)
        {
            if (s_instance == null || s_inLoop)
            {
                if (!__result && !s_inLoop)
                    Finish();
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (Time.frameCount != s_frame)
            {
                s_frame = Time.frameCount;
                s_frameStart = now;
            }
            float budget = FastLoader.FrameBudgetOverride > 0f ? FastLoader.FrameBudgetOverride : s_fallbackBudget;
            s_inLoop = true;
            try
            {
                var iterator = (System.Collections.IEnumerator)__instance;
                while (__result && Time.realtimeSinceStartup - s_frameStart < budget)
                {
                    object cur = s_current(__instance);
                    bool ready;
                    if (cur == null)
                    {
                        UnityWebRequest request = s_request(__instance);
                        ready = request == null || request.isDone;
                    }
                    else
                        ready = cur is AsyncOperation op && op.isDone;
                    if (!ready)
                        break;
                    s_skipped++;
                    SendAhead(__instance);
                    __result = iterator.MoveNext();
                }
            }
            finally
            {
                s_inLoop = false;
            }
            s_steps++;
            if (!__result)
                Finish();
        }

        private static void Finish()
        {
            if (s_done)
                return;
            s_done = true;
            s_clock.Stop();
            Log.Info("BundleDefinitions: definitions read in " + s_clock.Elapsed.TotalSeconds.ToString("F2") + " s over " + s_steps + " frames; "
                     + s_prefetchedUsed + " requests sent ahead used, " + s_prefetchedFailed + " failed and sent again, " + s_skipped + " waits skipped; "
                     + s_written.Count + " files written by the game (never read ahead), " + s_released + " released before its write");
            SaveWritten();
            Release();
            FlushPending();
        }

        private static void Release()
        {
            foreach (KeyValuePair<string, Ahead> entry in s_ahead)
                entry.Value.Request.Dispose();
            s_ahead.Clear();
            s_handed.Clear();
        }
    }
}
