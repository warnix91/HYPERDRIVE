using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Hyperdrive.Core
{
    /// <summary>
    /// After a game session that ended abruptly (crash, killed process: no normal quit), the next start is a prudent one:
    /// the optimizations that work where the game stopped (loading, a scene change, flight, editor...) stay off for that
    /// start only, and the player is told so. Nothing is written to the player's settings: the start after that is a normal
    /// one again, whatever happens (a prudent start is never followed by another one, so a game that crashes for other
    /// reasons never keeps Hyperdrive off). Read-only subsystems (diagnostics, the window, the loading notice) always run.
    /// State in PluginData/Session.txt: one line, written at start, at each scene change and at quit.
    /// </summary>
    internal static class PrudentStart
    {
        private const string FileName = "Session.txt";

        // Optimizations at work in each place (the subsystem names of Hyperdrive.cfg). HeapPadder (a bigger heap) goes off
        // everywhere: an abrupt end can be a lack of memory.
        private static readonly Dictionary<string, string[]> s_byPlace = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "LOADING", new[] { "HeapPadder", "FastLoader", "ModelPrefetch", "ConfigPrefetch", "StartPacing", "DatabaseIndex", "StartupFixes",
                                 "LocalizerCache", "PartCompilePacing", "LoadingBoost", "AsyncExpansions", "BundleDefinitions", "LaunchSitePacing", "JitPrewarm", "QuietLoading", "WarpScreen" } },
            { "SCENECHANGE", new[] { "HeapPadder", "TerrainCoroutines", "LaunchTerrain", "SceneLoadScreen", "SavePrefetch", "LeakFixes", "GCWindows" } },
            { "FLIGHT", new[] { "HeapPadder", "BuoyancyGuard", "AeroIdleThrottle", "MassUpdateGuard", "ThermalCadence", "OcclusionCache", "ZeroForceGuard",
                                "CrashFixes", "HighlightIdle", "HudText", "StockFixes", "LightBudget", "ThrottleGuard", "GCWindows", "LeakFixes" } },
            { "EDITOR", new[] { "HeapPadder", "PartListPerf", "IconPrewarm", "HighlightIdle", "GCWindows", "LeakFixes" } },
            { "SPACECENTER", new[] { "HeapPadder", "StockFixes", "SavePrefetch", "GCWindows", "LeakFixes" } },
            { "TRACKSTATION", new[] { "HeapPadder", "StockFixes", "GCWindows", "LeakFixes" } },
            { "MAINMENU", new[] { "HeapPadder", "SavePrefetch", "GCWindows" } },
        };

        private static string s_path;
        private static HashSet<string> s_off;
        private static bool s_prudent, s_quitHooked;
        private static string s_place = "LOADING";

        /// <summary>True for this start when the previous session ended abruptly (and was not itself a prudent start).</summary>
        internal static bool Active => s_prudent;

        /// <summary>Where the previous session stopped (LOADING, SCENECHANGE, FLIGHT...), for the notice and the log.</summary>
        internal static string StoppedIn { get; private set; }

        /// <summary>Reads how the previous session ended and marks this one as running. Before any subsystem starts.</summary>
        internal static void Begin(string modDirectory, bool enabled)
        {
            try
            {
                s_path = Path.Combine(Path.Combine(modDirectory, "PluginData"), FileName);
                string previous = File.Exists(s_path) ? File.ReadAllText(s_path).Trim() : null;
                bool previousRunning = previous != null && previous.StartsWith("running", StringComparison.Ordinal);
                bool previousPrudent = previous != null && previous.IndexOf("prudent", StringComparison.Ordinal) >= 0;
                if (previousRunning && enabled)
                {
                    StoppedIn = Value(previous, "in") ?? "LOADING";
                    if (previousPrudent)
                        Log.Info("PrudentStart: the previous start (already prudent) also ended abruptly in " + StoppedIn + ": normal start, the cause is elsewhere");
                    else
                    {
                        s_prudent = true;
                        string key = StoppedIn.StartsWith("SCENECHANGE", StringComparison.Ordinal) ? "SCENECHANGE" : StoppedIn;
                        if (!s_byPlace.TryGetValue(key, out string[] names))
                        {
                            var all = new List<string>();
                            foreach (string[] list in s_byPlace.Values)
                                all.AddRange(list);
                            names = all.ToArray();
                        }
                        s_off = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
                        Log.Warn("PrudentStart: the previous session ended abruptly in " + StoppedIn + " (no normal quit): " + s_off.Count
                                 + " optimizations of that place stay off for this start only (" + string.Join(", ", names) + ")");
                    }
                }
                Write();
                if (!s_quitHooked)
                {
                    s_quitHooked = true;
                    Application.quitting += OnQuitting;
                }
            }
            catch (Exception e)
            {
                Log.Debug("PrudentStart: session state not available (" + e.Message + ")");
            }
        }

        /// <summary>Whether a subsystem stays off for this prudent start.</summary>
        internal static bool Skips(string name) => s_prudent && s_off != null && s_off.Contains(name);

        internal static void SceneRequested(GameScenes scene)
        {
            s_place = "SCENECHANGE->" + scene;
            Write();
        }

        internal static void SceneReady(GameScenes scene)
        {
            s_place = scene.ToString();
            Write();
        }

        private static void OnQuitting()
        {
            try
            {
                if (s_path != null)
                    File.WriteAllText(s_path, "ended normally");
            }
            catch (Exception) { }
        }

        private static void Write()
        {
            if (s_path == null)
                return;
            try { File.WriteAllText(s_path, "running in=" + s_place + (s_prudent ? " prudent" : "")); }
            catch (Exception) { }
        }

        private static string Value(string line, string key)
        {
            int i = line.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0)
                return null;
            int start = i + key.Length + 1, end = line.IndexOf(' ', start);
            return end < 0 ? line.Substring(start) : line.Substring(start, end - start);
        }
    }
}
