using System;
using System.Collections.Generic;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using Hyperdrive.Rendering;
using Hyperdrive.Throttle;
using Hyperdrive.UI.Panel;

namespace Hyperdrive.UI
{
    /// <summary>
    /// The pages of the Hyperdrive panel: General (main switches, the window's look),
    /// Optimizations (every subsystem by family, its state and why), Performance (a 10 s measurement of every mod),
    /// and Loading screens (random or always the same, which ones). Switches are written to Hyperdrive.user.cfg as they change and
    /// apply at the next start; the window's look applies at once.
    /// </summary>
    internal static class PanelModel
    {
        internal sealed class Hooks
        {
            internal Action Close, Changed, Rebuild;
        }

        internal static UiModel Build(Hooks h)
        {
            var m = new UiModel
            {
                Title = Brand.Name,
                Short = Brand.Name,
                Subtitle = "v" + HyperdriveRuntime.Version,
                Close = h.Close,
                Changed = h.Changed,
                Status = () => Lang.T("#HD_Win_NextStart"),
            };
            m.Style = (UiStyle)UnityEngine.Mathf.Clamp(PanelLook.Style, 0, 5);
            m.Layout = (UiLayout)UnityEngine.Mathf.Clamp(PanelLook.Layout, 0, 2);

            General(m, h);
            Optimizations(m);
            Performance(m, h);
            Screens(m);
            return m;
        }

        private static UiPage Page(UiModel m, string title, UiGlyph glyph)
        {
            var p = new UiPage { Title = title, Glyph = glyph };
            m.Pages.Add(p);
            return p;
        }

        private static void Section(UiPage p, string title) => p.Rows.Add(new UiRow { Kind = UiRowKind.Section, Label = title });

        private static void Toggle(UiPage p, string label, string hint, Func<bool> get, Action<bool> set)
            => p.Rows.Add(new UiRow { Kind = UiRowKind.Toggle, Label = label, Hint = hint, GetBool = get, SetBool = set });

        private static string[] List(string key) => Lang.T(key).Split(',');

        // ------------------------------------------------------------------ pages

        private static void General(UiModel m, Hooks h)
        {
            UiPage p = Page(m, Lang.T("#HD_P_General"), UiGlyph.General);
            Section(p, Lang.T("#HD_P_SecLoading"));
            Toggle(p, Lang.T("#HD_Set_Animations"), Lang.T("#HD_P_AnimHint"), () => UserConfig.Subsystem("WarpScreen", true), v =>
            {
                UserConfig.SetSubsystem("WarpScreen", v);
                UserConfig.SetSubsystem("SceneLoadScreen", v);
            });
            Toggle(p, Lang.T("#HD_Set_Intro"), Lang.T("#HD_P_IntroHint"), () => UserConfig.Bool("WarpScreen", "intro", true), v => UserConfig.SetBool("WarpScreen", "intro", v));
            Toggle(p, Lang.T("#HD_Set_Notice"), Lang.T("#HD_P_NoticeHint"), () => UserConfig.Bool("LoadProblems", "showOnLoadingScreen", true),
                   v => UserConfig.SetBool("LoadProblems", "showOnLoadingScreen", v));
            Toggle(p, Lang.T("#HD_Set_PngCache"), Lang.T("#HD_P_PngCacheHint"), () => UserConfig.Bool("FastLoader", "pngCache", true),
                   v => UserConfig.SetBool("FastLoader", "pngCache", v));
            Section(p, Lang.T("#HD_P_SecImage"));
            Toggle(p, Lang.T("#HD_Set_Upscaler"), Lang.T("#HD_P_UpscalerHint"), () => UserConfig.Subsystem("Upscaler", false), v => UserConfig.SetSubsystem("Upscaler", v));
            Section(p, Lang.T("#HD_P_SecSafety"));
            Toggle(p, Lang.T("#HD_Set_Prudent"), Lang.T("#HD_P_PrudentHint"), () => UserConfig.Bool("Compatibility", "prudentStart", true),
                   v => UserConfig.SetBool("Compatibility", "prudentStart", v));
            Toggle(p, Lang.T("#HD_Set_Learn"), Lang.T("#HD_P_LearnHint"), () => UserConfig.Bool("Compatibility", "learnOverlaps", true),
                   v => UserConfig.SetBool("Compatibility", "learnOverlaps", v));
            Section(p, Lang.T("#HD_P_SecWindow"));
            p.Rows.Add(new UiRow
            {
                Kind = UiRowKind.Choice, Label = Lang.T("#HD_P_Style"), Options = List("#HD_P_StyleNames"),
                GetChoice = () => PanelLook.Style, SetChoice = i => { PanelLook.Style = i; h.Rebuild?.Invoke(); }
            });
            p.Rows.Add(new UiRow
            {
                Kind = UiRowKind.Choice, Label = Lang.T("#HD_P_Layout"), Options = List("#HD_P_LayoutNames"),
                GetChoice = () => PanelLook.Layout, SetChoice = i => { PanelLook.Layout = i; h.Rebuild?.Invoke(); }
            });
        }

        private static readonly string[] Families = { "Loading", "Physics", "Rendering", "Editor", "Fixes", "Memory", "Throttle", "Diagnostics", "UI" };

        private static void Optimizations(UiModel m)
        {
            UiPage p = Page(m, Lang.T("#HD_P_Opt"), UiGlyph.Spark);
            SubsystemHost host = HyperdriveRuntime.Host;
            if (host == null)
                return;
            foreach (string family in Families)
            {
                bool any = false;
                foreach (Subsystem s in host.All)
                {
                    string ns = s.GetType().Namespace ?? "";
                    if (!ns.EndsWith("." + family, StringComparison.Ordinal))
                        continue;
                    if (!any)
                    {
                        string title = Lang.T("#HD_Cat_" + family);
                        Section(p, title == "#HD_Cat_" + family ? family : title);
                        any = true;
                    }
                    Subsystem sub = s;
                    Toggle(p, sub.Name, StateText(sub), () => UserConfig.Subsystem(sub.Name, sub.EnabledByDefault), v => UserConfig.SetSubsystem(sub.Name, v));
                }
            }
        }

        private static string StateText(Subsystem s)
        {
            string state;
            switch (s.State)
            {
                case SubsystemState.Active: state = Lang.T("#HD_Win_State_Active"); break;
                case SubsystemState.DisabledByConfig: state = Lang.T("#HD_Win_State_Off"); break;
                case SubsystemState.StoodDown: state = Lang.T("#HD_Win_State_StoodDown"); break;
                case SubsystemState.Failed: state = Lang.T("#HD_Win_State_Failed"); break;
                default: state = Lang.T("#HD_Win_State_NotStarted"); break;
            }
            bool reason = s.State != SubsystemState.Active && s.State != SubsystemState.DisabledByConfig && !string.IsNullOrEmpty(s.StateReason);
            return reason ? state + " — " + s.StateReason : state;
        }

        private static void Performance(UiModel m, Hooks h)
        {
            UiPage p = Page(m, Lang.T("#HD_Win_Tab_Perf"), UiGlyph.Gauge);
            ModProfiler profiler = ModProfiler.Instance;
            if (profiler == null)
            {
                p.Rows.Add(new UiRow { Kind = UiRowKind.Info, Label = Lang.T("#HD_Win_PerfUnavailable") });
                return;
            }
            p.Rows.Add(new UiRow
            {
                Kind = UiRowKind.Action, Label = Lang.T("#HD_P_Measure"), Hint = Lang.T("#HD_Win_PerfIntro"),
                Button = Lang.F("#HD_Win_Analyze", 10), Click = () => { if (!profiler.Sampling) profiler.BeginSampling(10f, true); },
                Available = () => !profiler.Sampling,
            });
            p.Rows.Add(new UiRow
            {
                Kind = UiRowKind.Info,
                Text = () => profiler.Sampling ? Lang.F("#HD_Win_Measuring", UnityEngine.Mathf.CeilToInt(profiler.Remaining)) : ""
            });
            List<KeyValuePair<string, double>> owners = ModProfiler.LastOwners;
            if (owners == null || owners.Count == 0)
            {
                p.Rows.Add(new UiRow { Kind = UiRowKind.Info, Text = () => Lang.T("#HD_Win_PerfNone") });
                return;
            }
            double frame = ModProfiler.LastFrameMs;
            Section(p, Lang.F("#HD_Win_PerfTotal", Lang.Number(ModProfiler.LastCallbacksMs, 1), Lang.Number(frame, 1), frame > 0 ? Lang.Number(1000.0 / frame, 0) : "-"));
            for (int i = 0; i < owners.Count && i < 20; i++)
            {
                string value = Lang.Number(owners[i].Value, 2) + " ms";
                p.Rows.Add(new UiRow { Kind = UiRowKind.Info, Label = owners[i].Key, Text = () => value });
            }
        }

        private static void Screens(UiModel m)
        {
            UiPage p = Page(m, Lang.T("#HD_Win_Tab_Screens"), UiGlyph.Screen);
            LoadingScreenChoice.State state = LoadingScreenChoice.Load();
            var names = new List<string>();
            foreach (string[] group in LoadingScreenChoice.Groups)
                names.AddRange(group);
            // Off: always the same screen, the first one switched on below (keeping one screen on also gives always that one).
            Toggle(p, Lang.T("#HD_Menu_Random"), Lang.T("#HD_P_RandomHint"), () => !state.Fixed, v =>
            {
                state.Fixed = !v;
                if (state.Fixed)
                {
                    state.FixedName = names[0];
                    foreach (string n in names)
                        if (!state.Excluded.Contains(n)) { state.FixedName = n; break; }
                }
                LoadingScreenChoice.Save(state);
            });
            for (int g = 0; g < LoadingScreenChoice.Groups.Length; g++)
            {
                Section(p, LoadingScreenChoice.GroupName(g));
                foreach (string name in LoadingScreenChoice.Groups[g])
                {
                    string n = name;
                    Toggle(p, LoadingScreenChoice.Label(n), null, () => !state.Excluded.Contains(n), v =>
                    {
                        if (v) state.Excluded.Remove(n); else state.Excluded.Add(n);
                        LoadingScreenChoice.Save(state);
                    });
                }
            }
        }
    }

    /// <summary>The panel's look (style and layout), kept in Hyperdrive.user.cfg; Hyperdrive (the mod's look) and Panel by default.</summary>
    internal static class PanelLook
    {
        private static int s_style = -1, s_layout = -1;

        internal static int Style
        {
            get
            {
                if (s_style < 0)
                    s_style = Read("uiStyle", 5);
                return s_style;
            }
            set
            {
                s_style = value;
                UserConfig.Set("HyperdriveWindow", "uiStyle", value.ToString());
            }
        }

        internal static int Layout
        {
            get
            {
                if (s_layout < 0)
                    s_layout = Read("uiLayout", 1);
                return s_layout;
            }
            set
            {
                s_layout = value;
                UserConfig.Set("HyperdriveWindow", "uiLayout", value.ToString());
            }
        }

        private static int Read(string key, int fallback)
        {
            HyperdriveSettings settings = HyperdriveRuntime.Settings;
            return settings != null ? settings.Section("HyperdriveWindow").GetInt(key, fallback) : fallback;
        }
    }
}
