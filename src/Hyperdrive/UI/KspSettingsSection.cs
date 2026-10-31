using System;
using System.Collections.Generic;
using Hyperdrive.Core;
using KSP.Localization;
using UnityEngine;

namespace Hyperdrive.UI
{
    /// <summary>
    /// The Hyperdrive section of KSP's in-game settings (pause menu, Settings), built like KSP's own rows (a label, a switch
    /// showing KSP's own "Enabled" / "Disabled"): the main switches, kept when the player presses Apply or Accept (written
    /// to PluginData/Hyperdrive.user.cfg, used at the next start), and a button opening the Hyperdrive panel.
    /// </summary>
    internal static class KspSettingsSection
    {
        private sealed class Item
        {
            public string Label;
            public string Section, Key;          // Section "SUBSYSTEMS": Key is a subsystem (with Also, several)
            public string[] Also;
            public bool Default;
            public bool Value;
        }

        private static readonly List<Item> s_items = new List<Item>();
        private static bool s_dirty;

        private static void Build()
        {
            if (s_items.Count > 0)
                return;
            s_items.Add(new Item { Label = "#HD_Set_Animations", Section = "SUBSYSTEMS", Key = "WarpScreen", Also = new[] { "SceneLoadScreen" }, Default = true });
            s_items.Add(new Item { Label = "#HD_Set_Intro", Section = "WarpScreen", Key = "intro", Default = true });
            s_items.Add(new Item { Label = "#HD_Set_Notice", Section = "LoadProblems", Key = "showOnLoadingScreen", Default = true });
            s_items.Add(new Item { Label = "#HD_Set_PngCache", Section = "FastLoader", Key = "pngCache", Default = true });
            s_items.Add(new Item { Label = "#HD_Set_Upscaler", Section = "SUBSYSTEMS", Key = "Upscaler", Default = false });
            s_items.Add(new Item { Label = "#HD_Set_Prudent", Section = "Compatibility", Key = "prudentStart", Default = true });
            s_items.Add(new Item { Label = "#HD_Set_Learn", Section = "Compatibility", Key = "learnOverlaps", Default = true });
            foreach (Item item in s_items)
                item.Label = Lang.T(item.Label);
        }

        private static bool Current(Item item) => item.Section == "SUBSYSTEMS"
            ? UserConfig.Subsystem(item.Key, item.Default)
            : UserConfig.Bool(item.Section, item.Key, item.Default);

        internal static void Append(ref DialogGUIBase[] result)
        {
            try
            {
                Build();
                s_dirty = false;
                var list = new List<DialogGUIBase>(result ?? new DialogGUIBase[0]);
                list.Add(new DialogGUIBox("Hyperdrive", -1f, 18f, null));
                foreach (Item item in s_items)
                {
                    Item it = item;
                    it.Value = Current(it);
                    list.Add(new DialogGUIHorizontalLayout(TextAnchor.MiddleLeft,
                        new DialogGUILabel(() => it.Label, 150f),
                        new DialogGUIToggle(it.Value, () => Localizer.Format(it.Value ? "#autoLOC_6001072" : "#autoLOC_6001071"), b =>
                        {
                            it.Value = b;
                            s_dirty = true;
                        }, 150f),
                        new DialogGUIFlexibleSpace()));
                }
                list.Add(new DialogGUIHorizontalLayout(TextAnchor.MiddleLeft,
                    new DialogGUILabel(() => Lang.T("#HD_Set_NextStart"), 150f),
                    new DialogGUIButton(Lang.T("#HD_Set_Panel"), () => HyperdriveWindow.Instance?.OpenPanel(), 150f, 24f, false),
                    new DialogGUIFlexibleSpace()));
                result = list.ToArray();
            }
            catch (Exception e)
            {
                Log.Warn("Settings section not added (" + e.Message + "); KSP's settings are unchanged");
            }
        }

        internal static void Apply()
        {
            if (!s_dirty)
                return;
            s_dirty = false;
            foreach (Item item in s_items)
            {
                if (item.Value == Current(item))
                    continue;
                if (item.Section == "SUBSYSTEMS")
                {
                    UserConfig.SetSubsystem(item.Key, item.Value);
                    if (item.Also != null)
                        foreach (string other in item.Also)
                            UserConfig.SetSubsystem(other, item.Value);
                }
                else
                    UserConfig.SetBool(item.Section, item.Key, item.Value);
            }
        }
    }
}
