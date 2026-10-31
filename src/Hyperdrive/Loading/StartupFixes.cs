using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using KSP.Localization;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// Stock inefficiencies of the frozen first frame of loading, fixed with the same calls in the same order:
    ///  - GameDatabase.translateLoadedNodes localizes every config right after the config tree is built with
    ///    "for (i &lt; AllConfigs.Count()) TranslateBranch(AllConfigs.ElementAt(i).config)". AllConfigs is a lazy recursive
    ///    iterator, so each ElementAt(i) walks the whole tree again from the start: quadratic, ~7 s with 14,000 configs.
    ///    Here the tree is walked once into a list, then each config is translated in that order. TranslateBranch only
    ///    rewrites values, so the list cannot change under the loop: same result.
    /// </summary>
    internal sealed class StartupFixes : Subsystem
    {
        private bool _onePassTranslation = true;
        private bool _verifyHash;

        public override string Name => "StartupFixes";

        // Another mod that replaces one of the game methods patched here (a transpiler, or a prefix that can skip it) keeps
        // its version: this subsystem stands down (reviewed from the start of the load, see SubsystemHost.ReviewSharedTargets).
        public override bool YieldOnSharedTargets => true;

        // KSPCF's loader (always on) replaces translateLoadedNodes with its own one-pass version.
        public override string[] IncompatibleAssemblies => new[] { "KSPCommunityFixes", "WarmStart" };

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _onePassTranslation = settings.GetBool("onePassTranslation", _onePassTranslation);
            _verifyHash = settings.GetBool("verifyHash", _verifyHash);
        }

        protected internal override void OnEnable()
        {
            var translate = AccessTools.Method(typeof(GameDatabase), "translateLoadedNodes");
            Patch(translate, prefix: _onePassTranslation ? nameof(TranslateLoadedNodesPrefix) : null,
                  postfix: _verifyHash ? nameof(TranslateLoadedNodesHash) : null);
        }

        // Developer check (verifyHash): SHA-256 of every config right after localization, to compare with the stock pass
        // (onePassTranslation = false). Slow; never on by default.
        private static void TranslateLoadedNodesHash(GameDatabase __instance)
        {
            using (var sha = SHA256.Create())
            {
                int count = 0;
                foreach (UrlDir.UrlConfig config in __instance.root.AllConfigs)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(config.url + "\n" + config.config);
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                    count++;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                Log.Info("StartupFixes: localized config tree hash " + BitConverter.ToString(sha.Hash).Replace("-", "") + " over " + count + " configs");
            }
        }

        private static bool TranslateLoadedNodesPrefix(GameDatabase __instance)
        {
            List<UrlDir.UrlConfig> configs;
            long start = Stopwatch.GetTimestamp();
            try
            {
                configs = new List<UrlDir.UrlConfig>(__instance.root.AllConfigs);
            }
            catch (Exception e)
            {
                Log.Exception("StartupFixes: could not list the configs, stock translateLoadedNodes runs instead", e);
                return true;
            }
            for (int i = 0; i < configs.Count; i++)
                Localizer.TranslateBranch(configs[i].config);
            Log.Info("StartupFixes: " + configs.Count + " configs localized in " + ((Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency)
                     + " ms (one pass over the config tree instead of one per config)");
            return false;
        }
    }
}
