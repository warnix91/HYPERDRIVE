using System;

namespace Hyperdrive.Core
{
    /// <summary>
    /// Prefixed logging. Not meant for per-frame use: every call allocates the message string.
    /// </summary>
    internal static class Log
    {
        public const string Prefix = "[Hyperdrive] ";

        public static bool DebugEnabled;

        public static void Info(string message) => UnityEngine.Debug.Log(Prefix + message);

        public static void Warn(string message) => UnityEngine.Debug.LogWarning(Prefix + message);

        public static void Error(string message) => UnityEngine.Debug.LogError(Prefix + message);

        public static void Exception(string context, Exception e) => UnityEngine.Debug.LogError(Prefix + context + ": " + e);

        public static void Debug(string message)
        {
            if (DebugEnabled)
                UnityEngine.Debug.Log(Prefix + "[debug] " + message);
        }
    }
}
