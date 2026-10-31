using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using KSP.Localization;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// While the game compiles its parts, Localizer.Format(template) is asked for the same texts again and again (module
    /// fields, part infos: ~720 asks per part, 130,800 for 182 parts on a stock install). Without parameters its answer only
    /// depends on the template and on the language's tag table, which is complete and fixed by then; the answer given for
    /// a template is kept and given again until the main menu, where everything kept is dropped. The first 2,000 answers
    /// from what is kept are also asked of the localizer and compared: on any difference the cache turns itself off.
    /// Off while KSP's localization debug modes are on (they log or show keys on every ask). Idea seen in Warm Start; own code.
    /// </summary>
    internal sealed class LocalizerCache : Subsystem
    {
        private const int Checks = 2000;
        private static readonly Dictionary<string, string> s_cache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool s_active, s_broken, s_inOriginal;
        private static int s_hits, s_misses, s_checked;
        private static Func<bool> s_showKeys, s_debugMissing;

        public override string Name => "LocalizerCache";

        // Another mod that replaces one of the game methods patched here (a transpiler, or a prefix that can skip it) keeps
        // its version: this subsystem stands down (reviewed from the start of the load, see SubsystemHost.ReviewSharedTargets).
        public override bool YieldOnSharedTargets => true;

        // Warm Start keeps its own localized-text cache during the same compilation.
        public override string[] IncompatibleAssemblies => new[] { "WarmStart" };

        protected internal override void OnEnable()
        {
            if (HighLogic.LoadedScene != GameScenes.LOADING)
                throw new SubsystemUnavailableException("only during the game's first load");
            MethodInfo format = AccessTools.Method(typeof(Localizer), nameof(Localizer.Format), new[] { typeof(string) });
            MethodInfo compile = AccessTools.Method(typeof(PartLoader), "CompileAll");
            if (format == null || compile == null)
                throw new SubsystemUnavailableException("Localizer.Format(string) or PartLoader.CompileAll not found");
            s_showKeys = Getter("ShowKeysOnScreen");
            s_debugMissing = Getter("debugWriteMissingKeysToLog");
            Patch(format, prefix: nameof(FormatPrefix), postfix: nameof(FormatPostfix));
            Patch(compile, prefix: nameof(CompilePrefix));
            Log.Info("LocalizerCache: localized texts kept while the parts compile");
        }

        private static Func<bool> Getter(string property)
        {
            PropertyInfo p = AccessTools.Property(typeof(Localizer), property);
            MethodInfo get = p?.GetGetMethod(true);
            if (get == null || !get.IsStatic || get.ReturnType != typeof(bool))
                return () => true;   // unknown: treated as on, so the cache stays off
            return (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), get);
        }

        protected internal override void OnDisable() => Stop();

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene != GameScenes.MAINMENU || (s_hits + s_misses) == 0)
                return;
            Log.Info("LocalizerCache: " + s_hits + " texts given from the cache, " + s_misses + " asked of the localizer (" + s_cache.Count
                     + " kept, " + s_checked + " checked" + (s_broken ? ", turned off after a difference" : ", all the same") + "), dropped at the main menu");
            Stop();
        }

        private static void Stop()
        {
            s_active = false;
            s_cache.Clear();
        }

        // The parts compile once the configs, and the localization tags with them, are loaded.
        private static void CompilePrefix()
        {
            if (s_broken)
                return;
            bool debug;
            try { debug = s_showKeys() || s_debugMissing(); }
            catch (Exception) { debug = true; }
            s_active = !debug;
        }

        private static bool FormatPrefix(string template, ref string __result, out bool __state)
        {
            __state = false;
            if (!s_active || s_inOriginal || template == null)
                return true;
            if (!s_cache.TryGetValue(template, out string kept))
            {
                __state = true;   // store the stock answer
                s_misses++;
                return true;
            }
            if (s_checked < Checks)
            {
                s_checked++;
                string fresh;
                s_inOriginal = true;
                try { fresh = Localizer.Format(template); }
                finally { s_inOriginal = false; }
                if (!string.Equals(fresh, kept, StringComparison.Ordinal))
                {
                    s_broken = true;
                    Stop();
                    Log.Warn("LocalizerCache: '" + template + "' gave '" + fresh + "' after '" + kept + "': cache turned off");
                    __result = fresh;
                    return false;
                }
            }
            s_hits++;
            __result = kept;
            return false;
        }

        private static void FormatPostfix(string template, string __result, bool __state)
        {
            if (__state && s_active && template != null && __result != null)
                s_cache[template] = __result;
        }
    }
}
