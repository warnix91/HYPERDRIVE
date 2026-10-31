using System;
using System.Collections.Generic;
using System.IO;
using Hyperdrive.Core;
using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>
    /// Which loading screen plays at the next start. The player's choice (main menu window) and the draw history live in
    /// PluginData/LoadingScreens.cfg. Random mode draws without replacement across starts: every allowed screen plays once
    /// before any comes back, and never the same one twice in a row (also across a refill). The first load and the scene
    /// changes each have their own draw (the star fields only exist at the first load: a shared draw used to drop them).
    /// The very first start of Hyperdrive (no history yet) plays the hyperspace jump.
    /// </summary>
    internal static class LoadingScreenChoice
    {
        private const string FileName = "LoadingScreens.cfg";
        private const string NodeName = "LOADINGSCREENS";

        // Families shown in the menu window (names as the styles and effects report them).
        internal static readonly string[][] Groups =
        {
            new[] { "hyperspace", "vortex", "nebula", "orbit" },
            new[] { "blueprint", "survey", "navball", "checklist", "launchpad", "mun", "ascent", "ksc", "aurora", "polar", "splashdown", "reentry", "staging", "jool" },
            new[] { "rain", "forest", "campfire", "ripples", "city", "lighthouse" },
            new[] { "struts", "rud", "rover", "boosters", "kraken", "bounce" },
            new[] { "flip", "twr", "chute", "runway", "debris" },
        };
        /// <summary>A family's name in the player's language (#HD_Group_&lt;index&gt;).</summary>
        internal static string GroupName(int index) => Lang.T("#HD_Group_" + index);

        /// <summary>A screen's name in the player's language (#HD_Screen_&lt;name&gt;), the name itself when missing.</summary>
        internal static string Label(string name)
        {
            string key = "#HD_Screen_" + name;
            string text = Lang.T(key);
            return text == key ? name : text;
        }

        internal sealed class State
        {
            public bool Fixed;                  // always FixedName instead of random
            public string FixedName = "";
            public readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<string> Bag = new List<string>();        // first load
            public string Last = "";
            public readonly List<string> SceneBag = new List<string>();   // scene changes
            public string SceneLast = "";
            public bool FromFile;
        }

        private static string FilePath => Path.Combine(Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"), FileName);

        internal static State Load()
        {
            var state = new State();
            try
            {
                if (!File.Exists(FilePath))
                    return state;
                ConfigNode node = ConfigNode.Load(FilePath)?.GetNode(NodeName);
                if (node == null)
                    return state;
                state.FromFile = true;
                state.Fixed = node.GetValue("mode") == "fixed";
                state.FixedName = node.GetValue("fixed") ?? "";
                foreach (string s in Split(node.GetValue("exclude")))
                    state.Excluded.Add(s);
                state.Bag.AddRange(Split(node.GetValue("bag")));
                state.Last = node.GetValue("last") ?? "";
                state.SceneBag.AddRange(Split(node.GetValue("sceneBag")));
                state.SceneLast = node.GetValue("sceneLast") ?? "";
                if (!node.HasValue("sceneBag"))
                    state.Bag.Clear();   // older file: one shared draw, which had lost the star fields; a new round brings them back
            }
            catch (Exception e)
            {
                Log.Warn("LoadingScreens: could not read " + FileName + " (" + e.Message + "), defaults used");
            }
            return state;
        }

        internal static void Save(State state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var root = new ConfigNode();
                ConfigNode node = root.AddNode(NodeName);
                node.AddValue("mode", state.Fixed ? "fixed" : "random");
                node.AddValue("fixed", state.FixedName);
                node.AddValue("exclude", string.Join(",", new List<string>(state.Excluded).ToArray()));
                node.AddValue("bag", string.Join(",", state.Bag.ToArray()));
                node.AddValue("last", state.Last);
                node.AddValue("sceneBag", string.Join(",", state.SceneBag.ToArray()));
                node.AddValue("sceneLast", state.SceneLast);
                root.Save(FilePath, "Hyperdrive loading screens: set from the main menu window (mode, fixed, exclude); bag/last (first load) and sceneBag/sceneLast (scene changes) are the draw history");
                state.FromFile = true;
            }
            catch (Exception e)
            {
                Log.Warn("LoadingScreens: could not write " + FileName + ": " + e.Message);
            }
        }

        /// <summary>
        /// The screen for this start. cfgStyle is Hyperdrive.cfg's WarpScreen style: a style name is used as is unless the
        /// menu window saved a choice; "random" and the window's random mode draw from the bag.
        /// </summary>
        internal const string FirstStartScreen = "hyperspace";

        internal static string Pick(IList<string> names, string cfgStyle, out string how, bool sceneChange = false)
        {
            State state = Load();
            List<string> history = sceneChange ? state.SceneBag : state.Bag;
            string last = sceneChange ? state.SceneLast : state.Last;
            if (state.Fixed && names.Contains(state.FixedName))
            {
                how = "chosen in the menu";
                Remember(state, state.FixedName, sceneChange);
                return state.FixedName;
            }
            if (!state.FromFile && !sceneChange && cfgStyle == "random" && names.Contains(FirstStartScreen))
            {
                how = "first start of Hyperdrive";
                Remember(state, FirstStartScreen, false);
                return FirstStartScreen;
            }
            if (!state.FromFile && cfgStyle != "random" && cfgStyle != "nyankerbal" && names.Contains(cfgStyle))
            {
                how = "set in Hyperdrive.cfg";
                return cfgStyle;
            }

            var allowed = new List<string>(names.Count);
            foreach (string n in names)
                if (!state.Excluded.Contains(n))
                    allowed.Add(n);
            if (allowed.Count == 0)
                allowed.AddRange(names);

            var bag = new List<string>(allowed.Count);
            foreach (string n in history)
                if (allowed.Contains(n) && n != last && !bag.Contains(n))
                    bag.Add(n);
            bool refilled = false;
            if (bag.Count == 0)
            {
                refilled = true;
                foreach (string n in allowed)
                    if (n != last || allowed.Count == 1)
                        bag.Add(n);
            }
            string pick = bag[new System.Random(Environment.TickCount).Next(bag.Count)];
            bag.Remove(pick);
            history.Clear();
            history.AddRange(bag);
            Remember(state, pick, sceneChange);
            how = "random, " + bag.Count + " left before a repeat" + (refilled ? " (new round)" : "")
                  + (state.Excluded.Count > 0 ? ", " + (names.Count - allowed.Count) + " excluded" : "");
            return pick;
        }

        private static void Remember(State state, string shown, bool sceneChange)
        {
            if (sceneChange)
                state.SceneLast = shown;
            else
                state.Last = shown;
            Save(state);
        }

        private static IEnumerable<string> Split(string list)
        {
            if (string.IsNullOrEmpty(list))
                yield break;
            foreach (string part in list.Split(','))
            {
                string s = part.Trim();
                if (s.Length > 0)
                    yield return s;
            }
        }
    }
}
