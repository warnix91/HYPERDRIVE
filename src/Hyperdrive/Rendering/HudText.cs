using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using KSP.UI.Screens;
using KSP.UI.Screens.Flight;
using TMPro;

namespace Hyperdrive.Rendering
{
    /// <summary>
    /// Flight HUD texts written again every frame although they rarely change:
    /// - The stage groups' delta-v heading (StageGroup.SetDeltaVHeading_OnUpdate, every stage group, every frame): the
    ///   number is written, then the units appended, so the text changes twice per frame and TextMesh Pro lays it out
    ///   again each time, plus two strings of garbage. Here the same final text (same number formatting, units appended
    ///   below 5 characters) is computed only when the stage's delta-v value changes and written only if it differs.
    /// The flight speed display (SpeedDisplay.LateUpdate) formats its text every frame: a localized number, a string
    /// concatenation and a StringBuilder copy, ~1.1 KB of garbage per frame for a text that only changes when the speed
    /// rounded to 0.1 changes. Here the text is formatted again only when that rounded speed, the multiplier, format or
    /// units (statics other mods may set), or the text object changed, or when something else wrote the text; otherwise
    /// the text already shown is exactly what stock would write. Stands down when another mod patches the same method.
    /// </summary>
    internal sealed class HudText : Subsystem
    {
        private static double s_value = double.NaN, s_multiplier = double.NaN;
        private static string s_format, s_units, s_shown;
        private static TextMeshProUGUI s_text;

        private sealed class Heading
        {
            public float Value = float.NaN;
            public string Units, Shown;
        }

        private static readonly Dictionary<StageGroup, Heading> s_headings = new Dictionary<StageGroup, Heading>();
        private static AccessTools.FieldRef<StageGroup, DeltaVStageInfo> s_stage;
        private static AccessTools.FieldRef<StageGroup, TextMeshProUGUI> s_headingText;
        private static FieldInfo s_unitsField;
        private static bool s_headingsPatched;

        public override string Name => "HudText";
        public override bool YieldOnSharedTargets => true;

        /// <summary>Dev A/B (AutoBench): stock formatting every frame while true.</summary>
        internal static bool Bypass;

        protected internal override void OnEnable()
        {
            MethodInfo lateUpdate = AccessTools.Method(typeof(SpeedDisplay), "LateUpdate");
            if (lateUpdate == null)
                throw new SubsystemUnavailableException("SpeedDisplay.LateUpdate not found (game version mismatch?)");
            Patch(lateUpdate, prefix: nameof(SpeedPrefix), postfix: nameof(SpeedPostfix));
            s_headingsPatched = false;
            MethodInfo heading = AccessTools.Method(typeof(StageGroup), "SetDeltaVHeading_OnUpdate");
            s_unitsField = AccessTools.Field(typeof(StageGroup), "cacheAutoLOC_180095");
            if (heading != null && s_unitsField != null && s_unitsField.IsStatic && s_unitsField.FieldType == typeof(string)
                && AccessTools.Field(typeof(StageGroup), "stage")?.FieldType == typeof(DeltaVStageInfo)
                && AccessTools.Field(typeof(StageGroup), "DeltaVHeadingText")?.FieldType == typeof(TextMeshProUGUI))
            {
                s_stage = AccessTools.FieldRefAccess<StageGroup, DeltaVStageInfo>("stage");
                s_headingText = AccessTools.FieldRefAccess<StageGroup, TextMeshProUGUI>("DeltaVHeadingText");
                Patch(heading, prefix: nameof(HeadingPrefix));
                s_headingsPatched = true;
            }
            else
                Log.Info("HudText: StageGroup delta-v heading members not found, left to stock");
            Log.Info("HudText: flight speed text" + (s_headingsPatched ? " and stage delta-v headings" : "") + " formatted only when they change");
        }

        protected internal override void OnDisable() => Forget();

        protected internal override void OnSceneReady(GameScenes scene) => Forget();

        private static void Forget()
        {
            s_value = s_multiplier = double.NaN;
            s_format = s_units = s_shown = null;
            s_text = null;
            s_headings.Clear();
        }

        // Stock: text = deltaV.ToString("0"); if (text.Length < 5) text += units. Same final text, written once when it changes.
        private static bool HeadingPrefix(StageGroup __instance)
        {
            if (Bypass || HighLogic.LoadedSceneIsEditor)
                return true;
            DeltaVStageInfo stage = s_stage(__instance);
            TextMeshProUGUI text = s_headingText(__instance);
            if (stage == null || text == null)
                return true;
            float value = stage.GetSituationDeltaV(DeltaVGlobals.DeltaVAppValues.situation);
            string units = (string)s_unitsField.GetValue(null);
            if (!s_headings.TryGetValue(__instance, out Heading kept))
            {
                kept = new Heading();
                s_headings[__instance] = kept;
            }
            string shown = text.text;
            if (value.Equals(kept.Value) && ReferenceEquals(units, kept.Units) && kept.Shown != null && ReferenceEquals(shown, kept.Shown))
                return false;   // the text shown is the one stock would write again
            string number = value.ToString("0");
            string final = number.Length >= 5 ? number : number + units;
            if (!string.Equals(shown, final, StringComparison.Ordinal))
                text.text = final;
            kept.Value = value;
            kept.Units = units;
            kept.Shown = text.text;
            return false;
        }

        // Same value as stock computes: Math.Round(GetDisplaySpeed() * speedMultiplier, 1).
        private static bool SpeedPrefix(SpeedDisplay __instance, out bool __state)
        {
            __state = false;
            if (!FlightGlobals.ready || Bypass)
                return true;
            TextMeshProUGUI text = __instance.textSpeed;
            double value = Math.Round(FlightGlobals.GetDisplaySpeed() * SpeedDisplay.speedMultiplier, 1);
            if (text != null && value == s_value && SpeedDisplay.speedMultiplier == s_multiplier && ReferenceEquals(text, s_text)
                && ReferenceEquals(SpeedDisplay.format, s_format) && ReferenceEquals(SpeedDisplay.units, s_units)
                && s_shown != null && ReferenceEquals(text.text, s_shown))
                return false;   // the text shown is the one stock would write again
            s_value = value;
            s_multiplier = SpeedDisplay.speedMultiplier;
            s_format = SpeedDisplay.format;
            s_units = SpeedDisplay.units;
            s_text = text;
            __state = true;
            return true;
        }

        private static void SpeedPostfix(SpeedDisplay __instance, bool __state)
        {
            if (__state)
                s_shown = __instance.textSpeed != null ? __instance.textSpeed.text : null;
        }
    }
}
