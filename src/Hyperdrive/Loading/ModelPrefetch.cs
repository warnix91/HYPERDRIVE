using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// First load: the stock model loader (PartReader.Read, behind DatabaseLoaderModel_MU) parses each .mu file straight
    /// from disk through a BinaryReader on File.Open, thousands of small reads per model, all on the main thread. Here up
    /// to four worker threads read the .mu files ahead, in the order the game loads them, into memory (at most maxMegabytes
    /// waiting), and PartReader.Read gets a MemoryStream over those bytes instead of the file: the same bytes, parsed by
    /// the same code. A file not read yet is opened from disk as before. Only the first load; what is
    /// left when it ends is dropped. Idea seen in Warm Start (models read ahead); own code. Stands down with KSPCF, whose
    /// loader reads the models its own way.
    /// </summary>
    internal sealed class ModelPrefetch : Subsystem
    {
        private static readonly MethodInfo FileOpen = AccessTools.Method(typeof(File), nameof(File.Open), new[] { typeof(string), typeof(FileMode) });
        private static readonly object s_lock = new object();
        private static readonly Dictionary<string, byte[]> s_ready = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static long s_held, s_limit = 128L * 1024 * 1024;
        private static volatile bool s_stop;
        private static int s_replaced, s_hits, s_misses;
        private static bool s_started;

        public override string Name => "ModelPrefetch";

        public override string[] IncompatibleAssemblies => new[] { "KSPCommunityFixes", "WarmStart" };

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_limit = (long)UnityEngine.Mathf.Clamp(settings.GetInt("maxMegabytes", 128), 16, 2048) * 1024 * 1024;
        }

        protected internal override void OnEnable()
        {
            if (HighLogic.LoadedScene != GameScenes.LOADING || GameDatabase.Instance == null || GameDatabase.Instance.IsReady())
                throw new SubsystemUnavailableException("only during the game's first load");
            Type partReader = AccessTools.TypeByName("PartReader");   // internal to the game
            MethodInfo read = partReader != null ? AccessTools.Method(partReader, "Read", new[] { typeof(UrlDir.UrlFile) }) : null;
            if (read == null || FileOpen == null)
                throw new SubsystemUnavailableException("PartReader.Read not found (game version mismatch?)");
            s_replaced = 0;
            Patch(read, transpiler: nameof(Transpiler));
            if (s_replaced != 1)
                throw new InvalidOperationException("PartReader.Read: " + s_replaced + " File.Open calls replaced, 1 expected (game version mismatch?)");
            Log.Info("ModelPrefetch: .mu models read ahead on a worker thread, up to " + (s_limit / (1024 * 1024)) + " MB waiting");
        }

        protected internal override void OnDisable() => Drop();

        protected internal override void OnUpdate()
        {
            // The model list exists once the game database has its files; models load after the textures.
            if (s_started || GameDatabase.Instance == null || GameDatabase.Instance.root == null || HighLogic.LoadedScene != GameScenes.LOADING)
                return;
            var paths = new List<string>();
            try
            {
                foreach (UrlDir.UrlFile f in GameDatabase.Instance.root.GetFiles(UrlDir.FileType.Model))
                    if (string.Equals(f.fileExtension, "mu", StringComparison.OrdinalIgnoreCase))
                        paths.Add(f.fullPath);
            }
            catch (Exception)
            {
                return;   // tree not built yet
            }
            if (paths.Count == 0)
                return;
            s_started = true;
            s_stop = false;
            s_next = -1;
            int readers = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));
            for (int r = 0; r < readers; r++)
                new Thread(() => ReadAll(paths)) { IsBackground = true, Name = "Hyperdrive.ModelPrefetch." + r }.Start();
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU)
            {
                if (s_hits + s_misses > 0)
                    Log.Info("ModelPrefetch: " + s_hits + " models parsed from memory, " + s_misses + " opened from disk");
                Drop();
            }
        }

        private static void Drop()
        {
            s_stop = true;
            lock (s_lock)
            {
                s_ready.Clear();
                s_held = 0;
                Monitor.PulseAll(s_lock);
            }
        }

        private static int s_next;

        // Worker threads: files claimed in load order, plain file reads, never more than the limit waiting.
        private static void ReadAll(List<string> paths)
        {
            while (true)
            {
                int i = Interlocked.Increment(ref s_next);
                if (i >= paths.Count || s_stop)
                    return;
                string path = paths[i];
                byte[] bytes;
                try { bytes = File.ReadAllBytes(path); }
                catch (Exception) { continue; }   // left to the game, which reports any error itself
                lock (s_lock)
                {
                    while (s_held > 0 && s_held + bytes.Length > s_limit && !s_stop)
                        Monitor.Wait(s_lock, 200);
                    if (s_stop)
                        return;
                    s_ready[path] = bytes;
                    s_held += bytes.Length;
                }
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo open = AccessTools.Method(typeof(ModelPrefetch), nameof(OpenModel));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo m && m == FileOpen)
                {
                    instruction.operand = open;   // same arguments; the result goes straight into new BinaryReader(Stream)
                    s_replaced++;
                }
                yield return instruction;
            }
        }

        // The bytes read ahead when there are any for this file, else the file itself (File.Open as stock).
        private static Stream OpenModel(string path, FileMode mode)
        {
            byte[] bytes = null;
            if (mode == FileMode.Open)
            {
                lock (s_lock)
                {
                    if (s_ready.TryGetValue(path, out bytes))
                    {
                        s_ready.Remove(path);
                        s_held -= bytes.Length;
                        Monitor.PulseAll(s_lock);
                    }
                }
            }
            if (bytes != null)
            {
                s_hits++;
                return new MemoryStream(bytes, 0, bytes.Length, false, true);
            }
            s_misses++;
            return File.Open(path, mode);
        }
    }
}
