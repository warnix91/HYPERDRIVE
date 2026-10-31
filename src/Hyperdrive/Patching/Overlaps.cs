using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Hyperdrive.Core;
using UnityEngine;

namespace Hyperdrive.Patching
{
    /// <summary>
    /// Jobs done both by Hyperdrive and by KSPCommunityFixes (one of its patches): only one of the two runs, the faster
    /// one on this install. For each overlap:
    /// - the player's choice in Compatibility { PREFER { Id = ours | theirs | auto } } wins;
    /// - with auto (default when a neutral measure exists), the install learns: one overlap at a time, successive starts
    ///   alternate Hyperdrive's and KSPCF's version, a neutral measure is taken (start to main menu, main menu to space
    ///   centre, or flight physics scripts time per part and physics step), and once both sides have enough samples the
    ///   faster one is kept (the measured default when they are within the noise). What is learned goes with this
    ///   install's mods and machine (Core.Modpack) in PluginData/Overlaps.txt and is learned again when they change;
    /// - until then, and without a neutral measure, the default measured by the Hyperdrive authors is used.
    /// The side not kept is turned off: KSPCF's patch through its own settings (OtherOptimizers), or Hyperdrive's
    /// subsystem (it stands down). A Hyperdrive side that is not running (off in the settings, prudent start) never turns
    /// KSPCF's off.
    /// </summary>
    internal static class Overlaps
    {
        internal enum Side { Ours, Theirs }
        private enum Measure { None, Load, GameLoad, Flight }

        private sealed class Entry
        {
            public string Id;            // the KSPCF patch name (Settings.cfg)
            public Side Default;
            public Measure Measure;
            public string Evidence;
            public Side Effective;
            public string How;
            public bool Decided;
            public readonly List<Sample> Ours = new List<Sample>(), Theirs = new List<Sample>();
        }

        // A measured value and the situation it was taken in (flight: surface / atmo / space; others: all). Only samples
        // of the same situation are compared: a flight in orbit costs less than one in the atmosphere, whoever runs.
        private struct Sample
        {
            public double Value;
            public string Bucket;
        }

        private static readonly Entry[] s_table =
        {
            new Entry { Id = "ExpansionBundlePreload", Default = Side.Ours, Measure = Measure.None,
                        Evidence = "measured, far beyond any machine difference: LZ4 copies opened in 0.2-0.3 s instead of the LZMA bundles in 10-13 s (start 36 -> 23 s)" },
            new Entry { Id = "SceneLoadSpeedBoost", Default = Side.Theirs, Measure = Measure.GameLoad, Evidence = "mature KSPCF version, not measured head to head" },
            new Entry { Id = "FlightIntegratorPerf", Default = Side.Theirs, Measure = Measure.None,
                        Evidence = "measured on the PC: same physics time (2.79 vs 2.76 ms/frame, stock 3.16); Hyperdrive's temperatures are stock's, KSPCF's hottest skin is 0.16 K lower" },
            new Entry { Id = "OnDemandPartBuoyancy", Default = Side.Theirs, Measure = Measure.None, Evidence = "measured on the PC with FlightIntegratorPerf: same physics time" },
            new Entry { Id = "FasterEditorPartList", Default = Side.Theirs, Measure = Measure.None, Evidence = "no neutral measure in play: tools/overlaps face-to-face runs" },
            new Entry { Id = "PQSCoroutineLeak", Default = Side.Theirs, Measure = Measure.None, Evidence = "game bug fix: KSPCF's domain" },
            new Entry { Id = "PQSOnlyStartOnce", Default = Side.Theirs, Measure = Measure.None, Evidence = "game bug fix: KSPCF's domain" },
        };

        /// <summary>One line per job shared with KSPCF and who keeps it (for the panel's compatibility page).</summary>
        internal static readonly List<string> Lines = new List<string>();

        private const string FileName = "Overlaps.txt";
        private static string s_path;
        private static Entry s_trial;
        private static Side s_trialSide;
        private static bool s_trialValid = true;

        // Measures.
        private static bool s_menuSeen;
        private static float s_gameLoadStart = -1f;
        private static float s_flightReadyAt = -1f, s_nextPartCount;
        private static int s_parts;
        private static long s_lastTicks, s_lastSteps;
        private static double s_accTicks, s_accPartSteps, s_accSeconds;
        private static int s_surfaceFrames, s_atmoFrames, s_spaceFrames;

        /// <summary>
        /// Decides every overlap for this start; returns the KSPCF patches Hyperdrive takes over (turned off in KSPCF).
        /// </summary>
        /// <param name="kspcfHas">Whether KSPCF has (or, before its settings, may have) a patch enabled.</param>
        internal static HashSet<string> Decide(SettingsSection settings, IEnumerable<string> legacyKeepOurs, Func<string, bool> kspcfHas,
                                               bool flightMeasure, string modDirectory)
        {
            var ours = new HashSet<string>(StringComparer.Ordinal);
            var prefer = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in legacyKeepOurs)
                prefer[name] = "ours";
            ConfigNode[] nodes = settings.GetNodes("PREFER");   // the player's first, then the shipped defaults
            for (int i = nodes.Length - 1; i >= 0; i--)
                foreach (ConfigNode.Value v in nodes[i].values)
                    prefer[v.name.Trim()] = v.value.Trim().ToLowerInvariant();
            bool learn = settings.GetBool("learnOverlaps", true);

            s_path = Path.Combine(Path.Combine(modDirectory, "PluginData"), FileName);
            Load();

            foreach (Entry e in s_table)
            {
                prefer.TryGetValue(e.Id, out string choice);
                if (choice == "ours" || choice == "theirs")
                {
                    e.Effective = choice == "ours" ? Side.Ours : Side.Theirs;
                    e.How = "your setting (Compatibility PREFER)";
                }
                else if (e.Decided && e.Measure != Measure.None)   // a job no longer learned goes back to its default
                    e.How = "learned on this install: " + Means(e);
                else if (e.Measure == Measure.None || !learn || choice == "default")
                {
                    e.Effective = e.Default;
                    e.How = "default: " + e.Evidence;
                }
                else if (s_trial == null && kspcfHas(e.Id) && (e.Measure != Measure.Flight || flightMeasure))
                {
                    s_trial = e;
                    // The side with fewer samples; the measured default first.
                    s_trialSide = e.Ours.Count == e.Theirs.Count ? e.Default : (e.Ours.Count < e.Theirs.Count ? Side.Ours : Side.Theirs);
                    e.Effective = s_trialSide;
                    e.How = "learning on this install (" + e.Ours.Count + " + " + e.Theirs.Count + " samples of " + Needed(e) + " + " + Needed(e) + "), "
                            + (s_trialSide == Side.Ours ? "Hyperdrive" : "KSPCF") + " this start";
                }
                else
                {
                    e.Effective = e.Default;
                    e.How = "default until learned: " + e.Evidence;
                }
                if (e.Effective == Side.Ours)
                    ours.Add(e.Id);
                if (kspcfHas(e.Id) || ours.Contains(e.Id))
                {
                    Log.Info("Overlaps: " + e.Id + " -> " + (e.Effective == Side.Ours ? "Hyperdrive" : "KSPCommunityFixes") + " (" + e.How + ")");
                    Lines.Add(e.Id + " → " + (e.Effective == Side.Ours ? "Hyperdrive" : "KSPCF") + " (" + e.How + ")");
                }
            }
            return ours;
        }

        /// <summary>
        /// Once KSPCF has read its settings: the trial only counts if its side really runs (Hyperdrive's subsystem running and
        /// KSPCF's patch off, or KSPCF's patch on).
        /// </summary>
        internal static void ConfirmTrial(Func<string, bool> kspcfEnabled, Func<string, string> covering)
        {
            if (s_trial == null)
                return;
            s_trialValid = s_trialSide == Side.Ours ? covering(s_trial.Id) != null && !kspcfEnabled(s_trial.Id) : kspcfEnabled(s_trial.Id);
            if (!s_trialValid)
                Log.Info("Overlaps: " + s_trial.Id + " not measured this start (the " + (s_trialSide == Side.Ours ? "Hyperdrive" : "KSPCF") + " side is not running)");
        }

        internal static void SceneRequested(GameScenes scene)
        {
            GameScenes from = HighLogic.LoadedScene;
            if (from == GameScenes.MAINMENU && scene == GameScenes.SPACECENTER && s_gameLoadStart < 0f)
                s_gameLoadStart = Time.realtimeSinceStartup;
            if (from == GameScenes.FLIGHT)
                EndFlight();
        }

        internal static void SceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU && !s_menuSeen)
            {
                s_menuSeen = true;
                Record(Measure.Load, Time.realtimeSinceStartup, "all");
            }
            if (scene == GameScenes.SPACECENTER && s_gameLoadStart >= 0f)
            {
                Record(Measure.GameLoad, Time.realtimeSinceStartup - s_gameLoadStart, "all");
                s_gameLoadStart = -2f;   // the first game load of the session only
            }
            s_flightReadyAt = scene == GameScenes.FLIGHT ? Time.realtimeSinceStartup : -1f;
            Diagnostics.FrameTimeline t = Diagnostics.FrameTimeline.Active;
            if (t != null)
            {
                s_lastTicks = t.LearnFixedTicks;
                s_lastSteps = t.LearnSteps;
            }
        }

        internal static void Quitting() => EndFlight();

        // Every frame (allocation-free): flight physics scripts time per part and physics step, in steady flight only.
        internal static void Update()
        {
            if (s_trial == null || s_trial.Measure != Measure.Flight || !s_trialValid || s_flightReadyAt < 0f)
                return;
            Diagnostics.FrameTimeline t = Diagnostics.FrameTimeline.Active;
            if (t == null)
                return;
            long ticks = t.LearnFixedTicks, steps = t.LearnSteps;
            long dTicks = ticks - s_lastTicks, dSteps = steps - s_lastSteps;
            s_lastTicks = ticks;
            s_lastSteps = steps;
            float now = Time.realtimeSinceStartup;
            if (now >= s_nextPartCount)
            {
                s_nextPartCount = now + 1f;
                s_parts = 0;
                List<Vessel> loaded = FlightGlobals.VesselsLoaded;
                if (loaded != null)
                    for (int i = 0; i < loaded.Count; i++)
                        if (loaded[i] != null && !loaded[i].packed)
                            s_parts += loaded[i].parts.Count;
            }
            if (now - s_flightReadyAt < 10f || !FlightGlobals.ready || FlightDriver.Pause || TimeWarp.CurrentRate > 1.01f || s_parts < 20 || dSteps <= 0 || dTicks < 0)
                return;
            s_accTicks += dTicks;
            s_accPartSteps += (double)dSteps * s_parts;
            s_accSeconds += Time.unscaledDeltaTime;
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
                return;
            if (active.LandedOrSplashed || active.situation == Vessel.Situations.PRELAUNCH)
                s_surfaceFrames++;
            else if (active.mainBody != null && active.mainBody.atmosphere && active.altitude < active.mainBody.atmosphereDepth)
                s_atmoFrames++;
            else
                s_spaceFrames++;
        }

        private static void EndFlight()
        {
            if (s_accSeconds >= 60.0 && s_accPartSteps > 0)
            {
                string bucket = s_surfaceFrames >= s_atmoFrames && s_surfaceFrames >= s_spaceFrames ? "surface" : (s_atmoFrames >= s_spaceFrames ? "atmo" : "space");
                Record(Measure.Flight, s_accTicks / System.Diagnostics.Stopwatch.Frequency * 1e6 / s_accPartSteps, bucket);
            }
            s_accTicks = s_accPartSteps = s_accSeconds = 0;
            s_surfaceFrames = s_atmoFrames = s_spaceFrames = 0;
        }

        private static int Needed(Entry e) => e.Measure == Measure.Flight ? 3 : 2;

        private static void Record(Measure measure, double value, string bucket)
        {
            Entry e = s_trial;
            if (e == null || e.Measure != measure || !s_trialValid || e.Decided || value <= 0)
                return;
            (s_trialSide == Side.Ours ? e.Ours : e.Theirs).Add(new Sample { Value = value, Bucket = bucket });
            Log.Info("Overlaps: " + e.Id + " measured with " + (s_trialSide == Side.Ours ? "Hyperdrive" : "KSPCF") + ": " + Format(e, value) + " (" + bucket + ", "
                     + e.Ours.Count + " + " + e.Theirs.Count + " samples)");
            TryDecide(e);
            Save();
        }

        // The situation with the most samples on both sides, once both have enough there: medians compared.
        private static void TryDecide(Entry e)
        {
            int need = Needed(e), best = 0;
            string bucket = null;
            foreach (Sample a in e.Ours)
            {
                int o = Count(e.Ours, a.Bucket), t = Count(e.Theirs, a.Bucket);
                if (o >= need && t >= need && Math.Min(o, t) > best)
                {
                    best = Math.Min(o, t);
                    bucket = a.Bucket;
                }
            }
            if (bucket == null)
                return;
            double ours = Median(e.Ours, bucket), theirs = Median(e.Theirs, bucket), noise = e.Measure == Measure.Flight ? 0.05 : 0.03;
            bool close = Math.Abs(ours - theirs) <= noise * Math.Min(ours, theirs);
            e.Decided = true;
            e.Effective = close ? e.Default : (ours < theirs ? Side.Ours : Side.Theirs);
            Log.Info("Overlaps: " + e.Id + " learned: " + (e.Effective == Side.Ours ? "Hyperdrive" : "KSPCommunityFixes") + " kept from the next start ("
                     + bucket + ": Hyperdrive " + Format(e, ours) + " vs KSPCF " + Format(e, theirs) + (close ? ", within the noise: default" : "") + ")");
        }

        private static int Count(List<Sample> samples, string bucket)
        {
            int n = 0;
            foreach (Sample x in samples)
                if (x.Bucket == bucket)
                    n++;
            return n;
        }

        private static double Median(List<Sample> samples, string bucket)
        {
            var values = new List<double>();
            foreach (Sample x in samples)
                if (x.Bucket == bucket)
                    values.Add(x.Value);
            values.Sort();
            int n = values.Count;
            return n == 0 ? 0 : (n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2);
        }

        private static double Median(List<Sample> samples)
        {
            var values = new List<double>();
            foreach (Sample x in samples)
                values.Add(x.Value);
            values.Sort();
            int n = values.Count;
            return n == 0 ? 0 : (n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2);
        }

        private static string Means(Entry e) => "Hyperdrive " + Format(e, Median(e.Ours)) + " vs KSPCF " + Format(e, Median(e.Theirs));

        private static string Format(Entry e, double v) => e.Measure == Measure.Flight
            ? v.ToString("F3", CultureInfo.InvariantCulture) + " us per part step"
            : v.ToString("F1", CultureInfo.InvariantCulture) + " s";

        private static void Load()
        {
            try
            {
                if (!File.Exists(s_path))
                    return;
                string[] lines = File.ReadAllLines(s_path);
                string stored = null;
                foreach (string line in lines)
                    if (line.StartsWith("modpack ", StringComparison.Ordinal))
                        stored = line.Substring(8).Trim();
                if (stored != Modpack.Fingerprint)
                {
                    Log.Info("Overlaps: mods or machine changed since the last learning (" + (stored ?? "none") + " -> " + Modpack.Fingerprint + "): learned again");
                    return;
                }
                foreach (string line in lines)
                {
                    string[] p = line.Split(' ');
                    if (p.Length < 3)
                        continue;
                    Entry e = Find(p[1]);
                    if (e == null)
                        continue;
                    if (p[0] == "sample" && p.Length >= 4 && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        (p[2] == "ours" ? e.Ours : e.Theirs).Add(new Sample { Value = v, Bucket = p.Length >= 5 ? p[4] : "all" });
                    else if (p[0] == "decided")
                    {
                        e.Decided = true;
                        e.Effective = p[2] == "ours" ? Side.Ours : Side.Theirs;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Overlaps: learning file not read (" + ex.Message + ")");
            }
        }

        private static void Save()
        {
            try
            {
                var sb = new StringBuilder(512);
                sb.Append("// Hyperdrive: which of Hyperdrive and KSPCommunityFixes is faster on this install, for each job both do.\n")
                  .Append("// Learned over a few starts; learned again when the mods or the machine change. Safe to delete.\n")
                  .Append("modpack ").Append(Modpack.Fingerprint).Append('\n');
                foreach (Entry e in s_table)
                {
                    foreach (Sample x in e.Ours)
                        sb.Append("sample ").Append(e.Id).Append(" ours ").Append(x.Value.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(x.Bucket).Append('\n');
                    foreach (Sample x in e.Theirs)
                        sb.Append("sample ").Append(e.Id).Append(" theirs ").Append(x.Value.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(x.Bucket).Append('\n');
                    if (e.Decided)
                        sb.Append("decided ").Append(e.Id).Append(e.Effective == Side.Ours ? " ours" : " theirs").Append('\n');
                }
                File.WriteAllText(s_path, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Debug("Overlaps: learning file not written (" + ex.Message + ")");
            }
        }

        private static Entry Find(string id)
        {
            foreach (Entry e in s_table)
                if (e.Id == id)
                    return e;
            return null;
        }
    }
}
