using System;
using System.Collections.Generic;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// GameDatabase.GetModelPrefab / GetModel / ExistsModel find a model by scanning every loaded model and reading each
    /// GameObject's name (a native call that also allocates the string) until one matches. Part compilation asks for
    /// thousands of models: measured 3,140 GetModelPrefab calls at 0.92 ms each (2.9 s) and 993 GetModel calls (0.9 s)
    /// with 4,028 models. Here a name -> index table answers instead. Same answer as the stock scan: the table keeps the
    /// first model of each name, is rebuilt whenever the model list changes size or instance, every hit is checked against
    /// the model's current name, and a name the table does not know goes through the stock scan. Main thread only.
    /// Textures likewise (GetTexture, GetTextureIn, GetTextureInfo, GetTextureInfoIn, ExistsTexture: a case-insensitive scan
    /// of every loaded texture per call, thousands of textures in a modpack): first texture of each name, ignoring case as
    /// the scan does; a miss runs the stock method (which also handles the flag swaps). Idea also in KSPCF's GameDatabasePerf.
    /// </summary>
    internal sealed class DatabaseIndex : Subsystem
    {
        private static readonly Dictionary<string, int> s_index = new Dictionary<string, int>(4096, StringComparer.Ordinal);
        private static List<GameObject> s_list;
        private static int s_count = -1;
        private static long s_hits, s_rebuilds;
        private static readonly Dictionary<string, int> s_textures = new Dictionary<string, int>(8192, StringComparer.OrdinalIgnoreCase);
        private static List<GameDatabase.TextureInfo> s_textureList;
        private static int s_textureCount = -1;
        private static long s_textureHits, s_textureRebuilds;

        public override string Name => "DatabaseIndex";

        // Another mod that replaces one of the game methods patched here (a transpiler, or a prefix that can skip it) keeps
        // its version: this subsystem stands down (reviewed from the start of the load, see SubsystemHost.ReviewSharedTargets).
        public override bool YieldOnSharedTargets => true;

        public override string[] IncompatibleAssemblies => new[] { "KSPCommunityFixes", "WarmStart" };

        protected internal override void OnEnable()
        {
            Patch(AccessTools.Method(typeof(GameDatabase), nameof(GameDatabase.GetModelPrefab), new[] { typeof(string) }), prefix: nameof(GetModelPrefabPrefix));
            Patch(AccessTools.Method(typeof(GameDatabase), nameof(GameDatabase.GetModel), new[] { typeof(string) }), prefix: nameof(GetModelPrefix));
            Patch(AccessTools.Method(typeof(GameDatabase), nameof(GameDatabase.ExistsModel), new[] { typeof(string) }), prefix: nameof(ExistsModelPrefix));
            Patch(AccessTools.Method(typeof(GameDatabase), nameof(GameDatabase.GetTexture), new[] { typeof(string), typeof(bool) }), prefix: nameof(GetTexturePrefix));
            Patch(AccessTools.Method(typeof(GameDatabase), nameof(GameDatabase.GetTextureIn), new[] { typeof(string), typeof(string), typeof(bool) }), prefix: nameof(GetTextureInPrefix));
            Patch(AccessTools.Method(typeof(GameDatabase), nameof(GameDatabase.GetTextureInfo), new[] { typeof(string) }), prefix: nameof(GetTextureInfoPrefix));
            Patch(AccessTools.Method(typeof(GameDatabase), nameof(GameDatabase.GetTextureInfoIn), new[] { typeof(string), typeof(string) }), prefix: nameof(GetTextureInfoInPrefix));
            Patch(AccessTools.Method(typeof(GameDatabase), nameof(GameDatabase.ExistsTexture), new[] { typeof(string) }), prefix: nameof(ExistsTexturePrefix));
        }

        protected internal override void OnDisable()
        {
            s_index.Clear();
            s_list = null;
            s_count = -1;
            s_textures.Clear();
            s_textureList = null;
            s_textureCount = -1;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU && (s_hits > 0 || s_textureHits > 0))
                Log.Info("DatabaseIndex: " + s_hits + " model lookups and " + s_textureHits + " texture lookups answered from the index ("
                         + s_rebuilds + " + " + s_textureRebuilds + " index builds)");
        }

        private static bool GetModelPrefabPrefix(GameDatabase __instance, string url, ref GameObject __result)
        {
            GameObject model = Find(__instance, url);
            if (model == null)
                return true;
            __result = model;
            return false;
        }

        private static bool GetModelPrefix(GameDatabase __instance, string url, ref GameObject __result)
        {
            GameObject model = Find(__instance, url);
            if (model == null)
                return true;
            __result = UnityEngine.Object.Instantiate(model);
            return false;
        }

        private static bool ExistsModelPrefix(GameDatabase __instance, string url, ref bool __result)
        {
            if (Find(__instance, url) == null)
                return true;
            __result = true;
            return false;
        }

        // The model the stock scan would return, or null to let the stock scan run.
        private static GameObject Find(GameDatabase db, string url)
        {
            List<GameObject> list = db != null ? db.databaseModel : null;
            if (list == null || url == null)
                return null;
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (attempt > 0 || !ReferenceEquals(list, s_list) || list.Count != s_count)
                        Rebuild(list);
                    if (!s_index.TryGetValue(url, out int i) || i >= list.Count)
                        return null;
                    GameObject model = list[i];
                    if (model != null && model.name == url)
                    {
                        s_hits++;
                        return model;
                    }
                }
            }
            catch (Exception)
            {
                // Whatever went wrong, the stock scan gives the stock answer (or the stock exception).
                s_list = null;
            }
            return null;
        }

        // The list only grows while models load (lookups come in between): only the new entries are added then.
        private static void Rebuild(List<GameObject> list)
        {
            int from = ReferenceEquals(list, s_list) && s_count >= 0 && list.Count > s_count ? s_count : 0;
            if (from == 0)
                s_index.Clear();
            for (int i = from; i < list.Count; i++)
            {
                GameObject model = list[i];
                if (model == null)
                    continue;
                string name = model.name;
                if (name != null && !s_index.ContainsKey(name))
                    s_index.Add(name, i);
            }
            s_list = list;
            s_count = list.Count;
            s_rebuilds++;
        }

        // ------------------------------------------------------------------ textures

        private static bool GetTexturePrefix(GameDatabase __instance, string url, bool asNormalMap, ref Texture2D __result)
        {
            GameDatabase.TextureInfo info = FindTexture(__instance, url);
            if (info == null)
                return true;
            __result = asNormalMap ? info.normalMap : info.texture;
            return false;
        }

        // As stock: the folder of url, then the texture's name.
        private static bool GetTextureInPrefix(GameDatabase __instance, string url, string textureName, bool asNormalMap, ref Texture2D __result)
        {
            if (url == null || textureName == null)
                return true;
            GameDatabase.TextureInfo info = FindTexture(__instance, url.Substring(0, url.LastIndexOf('/') + 1) + textureName);
            if (info == null)
                return true;
            __result = asNormalMap ? info.normalMap : info.texture;
            return false;
        }

        private static bool GetTextureInfoPrefix(GameDatabase __instance, string url, ref GameDatabase.TextureInfo __result)
        {
            GameDatabase.TextureInfo info = FindTexture(__instance, url);
            if (info == null)
                return true;
            __result = info;
            return false;
        }

        private static bool GetTextureInfoInPrefix(GameDatabase __instance, string url, string textureName, ref GameDatabase.TextureInfo __result)
        {
            if (url == null || textureName == null)
                return true;
            GameDatabase.TextureInfo info = FindTexture(__instance, url.Substring(0, url.LastIndexOf('/') + 1) + textureName);
            if (info == null)
                return true;
            __result = info;
            return false;
        }

        private static bool ExistsTexturePrefix(GameDatabase __instance, string url, ref bool __result)
        {
            if (FindTexture(__instance, url) == null)
                return true;
            __result = true;
            return false;
        }

        // The texture the stock scan would return (first one whose name equals url ignoring case), or null to let it run.
        private static GameDatabase.TextureInfo FindTexture(GameDatabase db, string url)
        {
            List<GameDatabase.TextureInfo> list = db != null ? db.databaseTexture : null;
            if (list == null || url == null)
                return null;
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (attempt > 0 || !ReferenceEquals(list, s_textureList) || list.Count != s_textureCount)
                        RebuildTextures(list);
                    if (!s_textures.TryGetValue(url, out int i) || i >= list.Count)
                        return null;
                    GameDatabase.TextureInfo info = list[i];
                    if (info != null && string.Equals(info.name, url, StringComparison.OrdinalIgnoreCase))
                    {
                        s_textureHits++;
                        return info;
                    }
                }
            }
            catch (Exception)
            {
                s_textureList = null;
            }
            return null;
        }

        private static void RebuildTextures(List<GameDatabase.TextureInfo> list)
        {
            int from = ReferenceEquals(list, s_textureList) && s_textureCount >= 0 && list.Count > s_textureCount ? s_textureCount : 0;
            if (from == 0)
                s_textures.Clear();
            for (int i = from; i < list.Count; i++)
            {
                GameDatabase.TextureInfo info = list[i];
                if (info == null || info.name == null)
                    continue;
                if (!s_textures.ContainsKey(info.name))
                    s_textures.Add(info.name, i);
            }
            s_textureList = list;
            s_textureCount = list.Count;
            s_textureRebuilds++;
        }
    }
}
