using System;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;

namespace Hyperdrive.Fixes
{
    /// <summary>
    /// A config file write (ConfigNode.Save: the part database, saves, mods' settings) that fails only because another
    /// program holds the file for a moment (antivirus or Windows indexing scanning the file just written: Windows errors
    /// 32, 33 and 1175 "unable to remove the file to be replaced") is tried again a few times, 40 to 200 ms apart (0.6 s at
    /// most). Seen on the PC: KSPCommunityFixes' loader saving PartDatabase.cfg died on error 1175 and the first load never
    /// ended. Only that failure is touched; any other error, and a write that keeps failing, goes on as before. A finalizer
    /// (an addition, not a replacement): it runs after every other mod's patches on the method and never changes a write
    /// that succeeded.
    /// </summary>
    internal sealed class SaveRetry : Subsystem
    {
        private const int Attempts = 5;
        [ThreadStatic] private static int s_depth;
        private static int s_recovered, s_gaveUp;

        public override string Name => "SaveRetry";

        protected internal override void OnEnable()
        {
            MethodInfo save = AccessTools.Method(typeof(ConfigNode), nameof(ConfigNode.Save), new[] { typeof(string), typeof(string) });
            if (save == null)
                throw new SubsystemUnavailableException("ConfigNode.Save(string, string) not found (game version mismatch?)");
            Patch(save, finalizer: nameof(SaveFinalizer));
            Log.Info("SaveRetry: config file writes blocked for a moment by another program (antivirus, indexing) are tried again");
        }

        // Windows codes of "another program has the file": sharing violation, lock violation, replace failures.
        private static bool HeldByAnother(IOException e)
        {
            int code = e.HResult & 0xFFFF;
            if (code == 32 || code == 33 || code == 1175 || code == 1176 || code == 1177)
                return true;
            string m = e.Message ?? "";
            return m.IndexOf("Sharing violation", StringComparison.OrdinalIgnoreCase) >= 0
                   || m.IndexOf("returned 1175", StringComparison.Ordinal) >= 0
                   || m.IndexOf("returned 1176", StringComparison.Ordinal) >= 0
                   || m.IndexOf("returned 32.", StringComparison.Ordinal) >= 0;
        }

        private static Exception SaveFinalizer(ConfigNode __instance, string fileFullName, string header, Exception __exception)
        {
            if (!(__exception is IOException io) || s_depth > 0 || __instance == null || !HeldByAnother(io))
                return __exception;
            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                Thread.Sleep(40 * attempt);
                s_depth++;
                try
                {
                    __instance.Save(fileFullName, header);
                    s_recovered++;
                    Log.Info("SaveRetry: " + Path.GetFileName(fileFullName) + " written at attempt " + (attempt + 1) + " (it was held by another program: "
                             + io.Message + "); " + s_recovered + " write(s) recovered so far");
                    return null;
                }
                catch (IOException again) when (HeldByAnother(again))
                {
                    io = again;
                }
                catch (Exception other)
                {
                    return other;   // a different failure: reported as it is
                }
                finally
                {
                    s_depth--;
                }
            }
            s_gaveUp++;
            Log.Warn("SaveRetry: " + Path.GetFileName(fileFullName) + " still held by another program after " + (Attempts + 1) + " attempts: " + io.Message);
            return io;
        }
    }
}
