using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// ModuleManager patches every config of GameData at each start unless its cache is up to date (same files, same
    /// assemblies, same versions: SHA-256 of all of them), and it writes no cache at all as soon as one patch of the modpack
    /// has an error. One faulty patch in a big modpack then costs the whole patching on every start (PC, RSS/RP-1 with 295
    /// mods: 22 errors from two mods, 400 s of patching each time). With the same files the patching gives the same configs
    /// on every start, errors included, so here the cache is written anyway, by ModuleManager's own CreateCache with what it
    /// just patched; ModuleManager's own check still decides when it is out of date (any file or assembly changed: it patches
    /// again). Its errors stay on screen: on a start that reads the cache they are shown as load problems, since
    /// ModuleManager prints them only when it patches. Second cause, as common: a mod that writes a cfg of GameData again on
    /// every start (KSP_PartVolume: partVolumes-v7.cfg, whose comments change from one start to the next) changes
    /// ModuleManager's SHA every time, so the cache never applies. ignoreComments (default): the files are hashed as KSP
    /// reads them, comments (from "//" to the end of the line, as ConfigNode strips them) and blank space at line ends
    /// left out; any change to what KSP reads still invalidates the cache. Nothing else of ModuleManager is changed; a
    /// version whose members are not found leaves this off. fingerprint = true (developer): a hash of every config of the game database at the main
    /// menu, to compare a start that patched with one that read the cache.
    /// </summary>
    internal sealed class ModuleManagerCache : Subsystem
    {
        private const string ErrorsFile = "ModuleManagerErrors.txt";
        private static FieldInfo s_errors, s_status, s_configSha;
        private static MethodInfo s_createCache;
        private static string s_cachePath;
        private static bool s_fingerprint, s_dump, s_ignoreComments = true;
        private static int s_shaSites, s_normalized;
        private static MethodInfo s_readAllBytes, s_readForSha;

        public override string Name => "ModuleManagerCache";

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_fingerprint = settings.GetBool("fingerprint", false);
            s_dump = settings.GetBool("fingerprintDump", false);   // developer: every config in PluginData, to diff two starts
            s_ignoreComments = settings.GetBool("ignoreComments", true);
        }

        protected internal override void OnEnable()
        {
            Type loader = AccessTools.TypeByName("ModuleManager.MMPatchLoader");
            Type paths = AccessTools.TypeByName("ModuleManager.FilePathRepository");
            MethodInfo run = loader != null ? AccessTools.Method(loader, "Run", Type.EmptyTypes) : null;
            s_errors = loader != null ? AccessTools.Field(loader, "errors") : null;
            s_status = loader != null ? AccessTools.Field(loader, "status") : null;
            s_configSha = loader != null ? AccessTools.Field(loader, "configSha") : null;
            s_createCache = loader != null ? AccessTools.Method(loader, "CreateCache") : null;
            FieldInfo cache = paths != null ? AccessTools.Field(paths, "cachePath") : null;
            s_cachePath = cache?.GetValue(null) as string;
            if (run == null || s_errors?.FieldType != typeof(string) || s_status?.FieldType != typeof(string) || s_configSha?.FieldType != typeof(string)
                || s_createCache == null || s_createCache.GetParameters().Length != 2 || string.IsNullOrEmpty(s_cachePath))
                throw new SubsystemUnavailableException("ModuleManager not installed, or its patch loader is not as expected");
            Patch(run, postfix: nameof(RunPostfix));
            MethodInfo upToDate = AccessTools.Method(loader, "IsCacheUpToDate");
            s_readAllBytes = AccessTools.Method(typeof(File), nameof(File.ReadAllBytes), new[] { typeof(string) });
            s_readForSha = AccessTools.Method(typeof(ModuleManagerCache), nameof(ReadForSha));
            if (s_ignoreComments && upToDate != null && s_readAllBytes != null)
            {
                s_shaSites = 0;
                Patch(upToDate, transpiler: nameof(ShaTranspiler));
                if (s_shaSites != 1)
                    Log.Warn("ModuleManagerCache: expected one file read in IsCacheUpToDate, found " + s_shaSites + " (comment changes still invalidate the cache)");
            }
            Log.Info("ModuleManagerCache: ModuleManager's cache is kept even when a patch of the modpack has errors (same files, same result)");
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU && s_fingerprint)
                Log.Info("ModuleManagerCache: config fingerprint " + Fingerprint());
        }

        // IL: "call File::ReadAllBytes(string)" -> [string] -> byte[]: the helper has the same shape.
        private static IEnumerable<CodeInstruction> ShaTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo m && m == s_readAllBytes)
                {
                    instruction.operand = s_readForSha;
                    s_shaSites++;
                }
                yield return instruction;
            }
        }

        // A cfg as KSP reads it, for ModuleManager's SHA: each line cut at "//" (ConfigNode drops the rest) and trimmed, blank
        // lines left out. Anything unexpected: the file's bytes as before.
        private static byte[] ReadForSha(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            try
            {
                string text = new UTF8Encoding(false).GetString(raw);
                if (text.Length > 0 && text[0] == '\uFEFF')
                    text = text.Substring(1);
                var sb = new StringBuilder(text.Length);
                int start = 0;
                while (start <= text.Length)
                {
                    int end = text.IndexOf('\n', start);
                    if (end < 0)
                        end = text.Length;
                    string line = text.Substring(start, end - start);
                    int comment = line.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0)
                        line = line.Substring(0, comment);
                    line = line.Trim();
                    if (line.Length > 0)
                        sb.Append(line).Append('\n');
                    start = end + 1;
                }
                s_normalized++;
                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            catch (Exception)
            {
                return raw;
            }
        }

        // Runs where ModuleManager's Run runs (its patching thread): files only, no Unity scene API.
        private static void RunPostfix(object __instance, object __result)
        {
            try
            {
                string errors = s_errors.GetValue(__instance) as string;
                string sha = s_configSha.GetValue(__instance) as string ?? "";
                string errorsPath = Path.Combine(Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"), ErrorsFile);
                if (string.IsNullOrEmpty(errors))
                {
                    // Read from the cache (or patched without errors): errors this cache was written with stay visible.
                    ShowCachedErrors(errorsPath, sha);
                    return;
                }
                if (__result == null || File.Exists(s_cachePath))
                    return;
                string status = s_status.GetValue(__instance) as string ?? "";
                Match m = Regex.Match(status, @"(\d+) patch");
                int patched = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                s_createCache.Invoke(__instance, new[] { __result, (object)patched });
                Directory.CreateDirectory(Path.GetDirectoryName(errorsPath));
                File.WriteAllText(errorsPath, "SHA = " + sha + "\n" + errors);
                Log.Info("ModuleManagerCache: ModuleManager's cache written despite patch errors (" + patched + " patches; the same files give the same "
                         + "configs on every start): the next start reads it instead of patching again. Errors:\n" + errors.TrimEnd());
            }
            catch (Exception e)
            {
                Log.Warn("ModuleManagerCache: could not write the cache (" + e.GetType().Name + ": " + e.Message + "), ModuleManager patches as before");
            }
        }

        private static void ShowCachedErrors(string errorsPath, string sha)
        {
            if (!File.Exists(errorsPath))
                return;
            string[] lines = File.ReadAllLines(errorsPath);
            if (lines.Length < 2 || lines[0] != "SHA = " + sha)
            {
                File.Delete(errorsPath);   // another set of files: those errors were for a cache that no longer applies
                return;
            }
            for (int i = 1; i < lines.Length; i++)
                if (lines[i].Length > 0)
                    UnityEngine.Debug.LogError("ModuleManager (cache kept by Hyperdrive): " + lines[i]);
        }

        // Every config of the game database (url and full text), in database order.
        internal static string Fingerprint()
        {
            using (SHA256 sha = SHA256.Create())
            {
                int nodes = 0;
                StringBuilder dump = s_dump ? new StringBuilder(1 << 24) : null;
                foreach (UrlDir.UrlConfig config in GameDatabase.Instance.root.AllConfigs)
                {
                    string text = config.url + "\n" + config.config + "\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                    dump?.Append(text);
                    nodes++;
                }
                if (dump != null)
                {
                    try
                    {
                        string file = Path.Combine(Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"), "mm_configs_" + DateTime.Now.ToString("HHmmss") + ".txt");
                        File.WriteAllText(file, dump.ToString());
                    }
                    catch (Exception e) { Log.Warn("ModuleManagerCache: dump failed (" + e.Message + ")"); }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash, 0, 8).Replace("-", "").ToLowerInvariant() + " (" + nodes + " configs)";
            }
        }
    }
}
