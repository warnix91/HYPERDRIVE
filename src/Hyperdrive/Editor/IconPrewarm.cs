using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using KSP.UI.Screens;
using UnityEngine;

namespace Hyperdrive.Editor
{
    /// <summary>
    /// The editor's part list creates a part's icon (an instance of the part's icon model) the first time that part is
    /// shown, all of them in the frame of the refresh: the first search or category showing hundreds of parts freezes the
    /// editor. Here, once the editor is open, the icons of the parts the list can show (its exclude filters and its
    /// "amount available" filter, as EditorPartList.RefreshPartList takes them) are created ahead, a few milliseconds per
    /// frame, exactly as the list creates them (same prefab, EditorPartIcon.Create with the list's sizes, same cache entry)
    /// and stored hidden as the list stores the icons it hides. The list then finds them in its cache. Coexists with
    /// KSPCommunityFixes (which has nothing for this).
    /// </summary>
    internal sealed class IconPrewarm : Subsystem
    {
        private static readonly AccessTools.FieldRef<EditorPartList, Dictionary<string, EditorPartIcon>> s_cache =
            SafeRef<Dictionary<string, EditorPartIcon>>("iconCache");
        private static readonly AccessTools.FieldRef<EditorPartList, GameObject> s_storage = SafeRef<GameObject>("partIconStorage");

        private float _budgetSeconds = 0.003f;
        private float _delaySeconds = 1f;
        private readonly List<AvailablePart> _pending = new List<AvailablePart>();
        private int _next;
        private float _startAt = -1f;
        private int _created;
        private readonly Stopwatch _clock = new Stopwatch();
        private double _spentMs;

        public override string Name => "IconPrewarm";

        private static AccessTools.FieldRef<EditorPartList, T> SafeRef<T>(string field)
        {
            try { return AccessTools.FieldRefAccess<EditorPartList, T>(field); }
            catch (Exception) { return null; }
        }

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _budgetSeconds = Mathf.Clamp(settings.GetFloat("budgetMs", 3f), 0.5f, 20f) / 1000f;
            _delaySeconds = Mathf.Clamp(settings.GetFloat("delaySeconds", 1f), 0f, 30f);
        }

        protected internal override void OnEnable()
        {
            if (s_cache == null || s_storage == null)
                throw new SubsystemUnavailableException("EditorPartList icon cache not found (game version mismatch?)");
            Log.Info("IconPrewarm: editor part icons created ahead, " + (_budgetSeconds * 1000f).ToString("F1") + " ms per frame");
        }

        protected internal override void OnDisable() => _pending.Clear();

        protected internal override void OnSceneReady(GameScenes scene)
        {
            _pending.Clear();
            _next = 0;
            _created = 0;
            _spentMs = 0;
            _startAt = scene == GameScenes.EDITOR ? Time.realtimeSinceStartup + _delaySeconds : -1f;
        }

        protected internal override void OnUpdate()
        {
            if (_startAt < 0f || Time.realtimeSinceStartup < _startAt)
                return;
            EditorPartList list = EditorPartList.Instance;
            if (list == null || list.partPrefab == null || HighLogic.LoadedScene != GameScenes.EDITOR)
                return;
            if (_pending.Count == 0 && _next == 0)
            {
                // The parts the list can show, as it lists them (the rest of the stock filters only narrow this down).
                if (list.ExcludeFilters == null || list.AmountAvailableFilter == null)
                    return;
                List<AvailablePart> parts = list.ExcludeFilters.GetFilteredList(PartLoader.LoadedPartsList);
                EditorPartListFilter<AvailablePart>.FilterList(parts, list.AmountAvailableFilter.FilterCriteria);
                _pending.AddRange(parts);
                if (_pending.Count == 0)
                {
                    _startAt = -1f;
                    return;
                }
            }

            Dictionary<string, EditorPartIcon> cache = s_cache(list);
            GameObject storage = s_storage(list);
            if (cache == null || storage == null)
                return;
            _clock.Reset();
            _clock.Start();
            while (_next < _pending.Count && _clock.Elapsed.TotalSeconds < _budgetSeconds)
            {
                AvailablePart part = _pending[_next++];
                if (part == null || cache.ContainsKey(part.name))
                    continue;
                EditorPartIcon icon = null;
                try
                {
                    // As EditorPartList.UpdatePartIcons creates a missing icon, then stored as ClearAllItems stores hidden ones.
                    icon = UnityEngine.Object.Instantiate(list.partPrefab);
                    icon.Create(list, part, list.iconSize, list.iconOverScale, list.iconOverSpin);
                    cache.Add(icon.partInfo.name, icon);
                    icon.gameObject.SetActive(false);
                    icon.transform.SetParent(storage.transform, false);
                    _created++;
                }
                catch (Exception e)
                {
                    if (icon != null)
                        UnityEngine.Object.Destroy(icon.gameObject);
                    Log.Debug("IconPrewarm: icon of " + part.name + " left to the list (" + e.Message + ")");
                }
            }
            _clock.Stop();
            _spentMs += _clock.Elapsed.TotalMilliseconds;
            if (_next >= _pending.Count)
            {
                Log.Info("IconPrewarm: " + _created + " part icons created ahead (" + (_spentMs / 1000.0).ToString("F1") + " s spread over the editor's frames)");
                _pending.Clear();
                _startAt = -1f;
            }
        }
    }
}
