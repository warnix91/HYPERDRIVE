using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Hyperdrive.Core
{
    /// <summary>
    /// First code KSP runs from Hyperdrive. Deliberately free of any Harmony type so that it can
    /// detect a missing or outdated HarmonyKSP and disable the mod cleanly instead of crashing.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class HyperdriveBootstrap : MonoBehaviour
    {
        private static readonly Version MinimumHarmony = new Version(2, 2);

        private void Awake()
        {
            try
            {
                DontDestroyOnLoad(gameObject);

                string problem = CheckHarmony();
                if (problem != null)
                {
                    Log.Error(problem + " Hyperdrive is disabled.");
                    return;
                }

                Launch(gameObject);
            }
            catch (Exception e)
            {
                Log.Exception("Bootstrap failed, Hyperdrive is disabled", e);
            }
        }

        // Kept out of Awake so that the JIT only resolves Harmony-dependent types after the check passed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Launch(GameObject host) => host.AddComponent<HyperdriveRuntime>();

        private static string CheckHarmony()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                AssemblyName name = assembly.GetName();
                if (name.Name != "0Harmony")
                    continue;

                return name.Version >= MinimumHarmony
                    ? null
                    : "0Harmony " + name.Version + " is too old (HarmonyKSP 2.2+ required).";
            }
            return "HarmonyKSP (GameData/000_Harmony/0Harmony.dll) is not installed.";
        }
    }
}
