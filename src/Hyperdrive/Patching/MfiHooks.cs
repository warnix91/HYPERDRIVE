using System;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;

namespace Hyperdrive.Patching
{
    /// <summary>
    /// ModularFlightIntegrator (installed by FAR and Kopernicus) replaces each vessel's FlightIntegrator with a subclass
    /// whose overrides call a mod's delegate when one is registered for that step (static fields such as
    /// updateThermodynamicsOverride, filled at the space centre) and the stock method otherwise. So a Hyperdrive patch on
    /// a stock FlightIntegrator method still applies under MFI unless a mod took that step: then the stock method no
    /// longer runs and the patch simply has nothing to act on. This reads which steps are taken, and by whom.
    /// </summary>
    internal static class MfiHooks
    {
        private static Type s_type;
        private static bool s_searched;

        internal static bool Installed => Type != null;

        private static Type Type
        {
            get
            {
                if (!s_searched)
                {
                    s_searched = true;
                    foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
                    {
                        if (loaded?.assembly == null || !string.Equals(loaded.name, "ModularFlightIntegrator", StringComparison.OrdinalIgnoreCase))
                            continue;
                        s_type = loaded.assembly.GetType("ModularFI.ModularFlightIntegrator");
                        break;
                    }
                }
                return s_type;
            }
        }

        /// <summary>
        /// The assembly of the mod whose delegate is registered in MFI's static field <paramref name="field"/>, null when
        /// MFI is absent or nobody took that step. "?" when the field cannot be read (another MFI version): treated as taken.
        /// </summary>
        internal static string Owner(string field)
        {
            Type type = Type;
            if (type == null)
                return null;
            try
            {
                FieldInfo f = AccessTools.Field(type, field);
                if (f == null || !f.IsStatic)
                    return "?";
                Delegate d = f.GetValue(null) as Delegate;
                if (d == null)
                    return null;
                Delegate[] list = d.GetInvocationList();
                string owners = null;
                for (int i = 0; i < list.Length; i++)
                {
                    string name = list[i].Method?.DeclaringType?.Assembly.GetName().Name ?? "?";
                    owners = owners == null ? name : owners + ", " + name;
                }
                return owners;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>
        /// Logs, for one Hyperdrive subsystem, whether the MFI step its patch relies on is taken (then the patch does
        /// nothing there) or free (the stock method runs and the patch applies as without MFI).
        /// </summary>
        internal static void Report(string subsystem, string field, string stockMethod)
        {
            if (!Installed)
                return;
            string owner = Owner(field);
            if (owner == null)
                Log.Info(subsystem + ": ModularFlightIntegrator calls the stock " + stockMethod + " (no mod took it): active");
            else
                Log.Info(subsystem + ": ModularFlightIntegrator's " + stockMethod + " is taken by " + owner
                         + ": the stock method no longer runs, so " + subsystem + " has nothing to act on there");
        }
    }
}
