using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using KSP.UI.Screens;
using UnityEngine;

namespace Hyperdrive.Editor
{
    /// <summary>
    /// The editor's part list (VAB/SPH) on every refresh (search typed, category or sort changed):
    /// - BasePartCategorizer.PartMatchesSearch splits each part's whole tag string again for every part and every search
    ///   (BasePartCategorizer.SearchTagSplit: Trim, ToLowerInvariant, Split; ~15 tags per part). The split of a given tag string
    ///   is kept: same string in, same tags out (the array is only read).
    /// - EditorPartList.ClearAllItems moves every shown icon under a hidden storage object, and UpdatePartIcon moves the
    ///   ones still shown back into the grid: most of a refresh's time with many parts. Here an icon that was hidden
    ///   (deactivated, as stock does first) stays in the grid, and an icon shown again is moved to the end of the grid,
    ///   which keeps the requested order (the grid lays out its active children only); a new icon is parented as stock.
    /// - The same refresh also turns every shown icon off (ClearAllItems) and the ones still shown back on (UpdatePartIcon),
    ///   each time running every OnDisable/OnEnable of the icon (its model, highlighter...). Here ClearAllItems only lists
    ///   them; an icon shown again stays on; the ones not shown again are turned off when the refresh ends (same call, before
    ///   any frame is drawn), or right away when ClearAllItems is called from anywhere else. Same icons on and off at the end.
    /// - A refresh that turns on hundreds of icons (a short search) still turns them all on in one frame. The first
    ///   showFirst icons (the top of the list, what is on screen) are turned on at once, the rest a few dozen per frame
    ///   afterwards, in list order (they are further down, out of view); a new refresh cancels what is still waiting.
    /// Idea from KSPCommunityFixes' FasterEditorPartList (MIT); own code. With KSPCF's patch enabled, this is left to it.
    /// </summary>
    internal sealed class PartListPerf : Subsystem
    {
        private static readonly MethodInfo SetParent = AccessTools.Method(typeof(Transform), nameof(Transform.SetParent), new[] { typeof(Transform), typeof(bool) });
        private static readonly MethodInfo SetActive = AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive), new[] { typeof(bool) });
        private static readonly List<GameObject> s_toHide = new List<GameObject>();
        private static readonly HashSet<GameObject> s_toHideSet = new HashSet<GameObject>();
        private static int s_refreshDepth;
        private static readonly List<GameObject> s_deferred = new List<GameObject>();
        private static readonly HashSet<GameObject> s_deferredSet = new HashSet<GameObject>();
        private static int s_deferredNext, s_shownThisRefresh;
        private static int s_showFirst = 60, s_perFrame = 40;
        private static readonly MethodInfo TagSplit = AccessTools.Method(typeof(BasePartCategorizer), nameof(BasePartCategorizer.SearchTagSplit), new[] { typeof(string) });
        private static readonly Dictionary<string, string[]> s_tags = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static int s_replaced;

        public override string Name => "PartListPerf";

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_showFirst = Mathf.Clamp(settings.GetInt("showFirst", s_showFirst), 10, 100000);
            s_perFrame = Mathf.Clamp(settings.GetInt("iconsPerFrame", s_perFrame), 5, 100000);
        }

        // Icons waiting to be turned on, a few per frame, in list order.
        protected internal override void OnUpdate()
        {
            if (s_deferredNext >= s_deferred.Count)
                return;
            int end = Math.Min(s_deferred.Count, s_deferredNext + s_perFrame);
            for (; s_deferredNext < end; s_deferredNext++)
            {
                GameObject icon = s_deferred[s_deferredNext];
                if (icon != null && s_deferredSet.Remove(icon))
                    icon.SetActive(true);
            }
            if (s_deferredNext >= s_deferred.Count)
                ClearDeferred();
        }

        private static void ClearDeferred()
        {
            s_deferred.Clear();
            s_deferredSet.Clear();
            s_deferredNext = 0;
        }

        public override string[] KspcfEquivalents => new[] { "FasterEditorPartList" };
        public override bool YieldOnSharedTargets => true;

        protected internal override void OnEnable()
        {
            if (SetParent == null || TagSplit == null || SetActive == null)
                throw new SubsystemUnavailableException("Transform.SetParent or BasePartCategorizer.SearchTagSplit not found");
            PatchExactly(AccessTools.Method(typeof(BasePartCategorizer), "PartMatchesSearch"), nameof(SearchTranspiler), 1);
            PatchExactly(AccessTools.Method(typeof(EditorPartList), "ClearAllItems"), nameof(ClearTranspiler), 2);
            PatchExactly(AccessTools.Method(typeof(EditorPartList), "UpdatePartIcon"), nameof(ShowTranspiler), 2);
            Patch(AccessTools.Method(typeof(EditorPartList), "ClearAllItems"), postfix: nameof(ClearPostfix));
            MethodInfo refresh = AccessTools.Method(typeof(EditorPartList), "UpdatePartIcons");
            if (refresh == null)
                throw new SubsystemUnavailableException("EditorPartList.UpdatePartIcons not found");
            Patch(refresh, prefix: nameof(RefreshPrefix), finalizer: nameof(RefreshFinalizer));
            Log.Info("PartListPerf: part tags split once, part icons kept in the list's grid between refreshes");
        }

        protected internal override void OnDisable()
        {
            HidePending();
            for (int i = s_deferredNext; i < s_deferred.Count; i++)
                if (s_deferred[i] != null && s_deferredSet.Contains(s_deferred[i]))
                    s_deferred[i].SetActive(true);
            ClearDeferred();
            s_tags.Clear();
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene != GameScenes.EDITOR)
                s_tags.Clear();
            ClearDeferred();
            s_toHide.Clear();
            s_toHideSet.Clear();
            s_refreshDepth = 0;
        }

        // The call site must be found exactly as many times as expected, else the method is not what this was written for.
        private void PatchExactly(MethodBase method, string transpiler, int expected)
        {
            if (method == null)
                throw new SubsystemUnavailableException("editor part list method not found (game version mismatch?)");
            s_replaced = 0;
            Patch(method, transpiler: transpiler);
            if (s_replaced != expected)
                throw new InvalidOperationException(method.Name + ": " + s_replaced + " call(s) replaced, " + expected + " expected (game version mismatch?)");
        }

        private static IEnumerable<CodeInstruction> SearchTranspiler(IEnumerable<CodeInstruction> instructions)
            => Replace(instructions, TagSplit, AccessTools.Method(typeof(PartListPerf), nameof(CachedTagSplit)));

        private static IEnumerable<CodeInstruction> ClearTranspiler(IEnumerable<CodeInstruction> instructions)
            => Replace(Replace(instructions, SetParent, AccessTools.Method(typeof(PartListPerf), nameof(KeepInGrid))),
                       SetActive, AccessTools.Method(typeof(PartListPerf), nameof(HideLater)));

        private static IEnumerable<CodeInstruction> ShowTranspiler(IEnumerable<CodeInstruction> instructions)
            => Replace(Replace(instructions, SetParent, AccessTools.Method(typeof(PartListPerf), nameof(PlaceInGrid))),
                       SetActive, AccessTools.Method(typeof(PartListPerf), nameof(ShowNow)));

        // ClearAllItems: SetActive(false) becomes "turn off later" (the icon may be shown again by the same refresh).
        private static void HideLater(GameObject icon, bool active)
        {
            if (!active && s_deferredSet.Remove(icon))
                return;   // was still waiting to be turned on: stays off
            if (active || !icon.activeSelf)
            {
                icon.SetActive(active);
                return;
            }
            if (s_toHideSet.Add(icon))
                s_toHide.Add(icon);
        }

        // UpdatePartIcon: SetActive(true); an icon still on (listed by ClearAllItems) just stays on.
        private static void ShowNow(GameObject icon, bool active)
        {
            if (active && s_toHideSet.Remove(icon))
                return;
            if (active && !icon.activeSelf && s_refreshDepth > 0 && s_shownThisRefresh++ >= s_showFirst)
            {
                if (s_deferredSet.Add(icon))
                    s_deferred.Add(icon);
                return;
            }
            icon.SetActive(active);
        }

        // Called from a refresh: the refresh turns off what it did not show again. From anywhere else: now.
        private static void ClearPostfix()
        {
            if (s_refreshDepth == 0)
                HidePending();
        }

        private static void RefreshPrefix()
        {
            if (s_refreshDepth++ == 0)
            {
                s_shownThisRefresh = 0;
                if (s_deferredNext >= s_deferred.Count)
                    ClearDeferred();
            }
        }

        private static Exception RefreshFinalizer(Exception __exception)
        {
            if (s_refreshDepth > 0)
                s_refreshDepth--;
            if (s_refreshDepth == 0)
                HidePending();
            return __exception;
        }

        private static void HidePending()
        {
            for (int i = 0; i < s_toHide.Count; i++)
            {
                GameObject icon = s_toHide[i];
                if (icon != null && s_toHideSet.Contains(icon))
                    icon.SetActive(false);
            }
            s_toHide.Clear();
            s_toHideSet.Clear();
        }

        private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, MethodInfo from, MethodInfo to)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo m && m == from)
                {
                    instruction.opcode = OpCodes.Call;   // same stack: (transform, parent, bool) or (string)
                    instruction.operand = to;
                    s_replaced++;
                }
                yield return instruction;
            }
        }

        private static string[] CachedTagSplit(string tags)
        {
            if (tags == null)
                return BasePartCategorizer.SearchTagSplit(tags);   // stock behaviour, exception included
            if (!s_tags.TryGetValue(tags, out string[] split))
                s_tags[tags] = split = BasePartCategorizer.SearchTagSplit(tags);
            return split;
        }

        // ClearAllItems: the icon was just deactivated; it stays where it is instead of moving to the storage object.
        private static void KeepInGrid(Transform icon, Transform storage, bool worldPositionStays)
        {
            if (icon.parent == null)
                icon.SetParent(storage, worldPositionStays);
        }

        // UpdatePartIcon: an icon already in the grid goes to its end (the order the list asked for); others are parented.
        private static void PlaceInGrid(Transform icon, Transform grid, bool worldPositionStays)
        {
            if (icon.parent == grid)
                icon.SetAsLastSibling();
            else
                icon.SetParent(grid, worldPositionStays);
        }
    }
}
