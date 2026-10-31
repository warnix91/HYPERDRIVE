using System;

namespace Hyperdrive.API
{
    /// <summary>
    /// Entry point for mods that want to use Hyperdrive services.
    /// Treat Hyperdrive as a soft dependency: check that the "Hyperdrive.API" assembly is loaded
    /// (AssemblyLoader.loadedAssemblies) before touching any type of this namespace, and always
    /// check <see cref="IsReady"/> before calling a service.
    /// </summary>
    public static class HyperdriveAPI
    {
        /// <summary>
        /// Feature level of this API. 1: IsReady, RuntimeVersion, IsSubsystemActive, Ready. 2: RequestGcHold.
        /// 3: RegisterLight/UnregisterLight, and the standalone helpers Pooling.ObjectPool, Pooling.GameObjectPool,
        /// Rendering.InstancedBatch.
        /// </summary>
        public const int ApiVersion = 3;

        /// <summary>
        /// ApiVersion of the Hyperdrive.API.dll actually installed. Check this one at runtime:
        /// <see cref="ApiVersion"/> is a const and gets baked into your mod when it is compiled.
        /// </summary>
        public static int InstalledApiVersion => ApiVersion;

        private static IHyperdriveRuntime s_runtime;

        /// <summary>True once the Hyperdrive runtime has started and registered itself.</summary>
        public static bool IsReady => s_runtime != null;

        /// <summary>Version of the running Hyperdrive plugin, or null if it is not running.</summary>
        public static string RuntimeVersion => s_runtime?.Version;

        /// <summary>Raised once, on the main thread, when the runtime becomes ready.</summary>
        public static event Action Ready;

        /// <summary>
        /// True if the named subsystem (e.g. "LogSpamGuard") is enabled and running.
        /// Returns false when Hyperdrive is not running, the subsystem is disabled in the config,
        /// stood down because of an incompatible mod, or failed.
        /// </summary>
        public static bool IsSubsystemActive(string name)
        {
            IHyperdriveRuntime runtime = s_runtime;
            return runtime != null && name != null && runtime.IsSubsystemActive(name);
        }

        /// <summary>
        /// Ask Hyperdrive to keep the garbage collector off for up to <paramref name="seconds"/> (capped by the
        /// user's config, typically 4 s), e.g. right before spawning a big explosion effect, so that no GC cycle
        /// lands in the middle of it. Call from the main thread. Allocation-free.
        /// Returns false if refused (GCWindows disabled, cooldown after a previous hold, scene loading, not the main thread).
        /// </summary>
        public static bool RequestGcHold(float seconds)
        {
            IHyperdriveRuntime runtime = s_runtime;
            return runtime != null && runtime.RequestGcHold(seconds);
        }

        /// <summary>
        /// Put a dynamic light (explosion flash, fire...) under Hyperdrive's light budget: only the most important
        /// registered lights (priority x intensity x range / distance) stay enabled at once. Main thread only.
        /// Unregister it before destroying or pooling it (destroyed lights are also cleaned up automatically).
        /// Returns false when the LightBudget subsystem is not running: then nothing changes for your light.
        /// </summary>
        public static bool RegisterLight(UnityEngine.Light light, float priority = 1f)
        {
            IHyperdriveRuntime runtime = s_runtime;
            return runtime != null && runtime.RegisterLight(light, priority);
        }

        /// <summary>Remove a light from the budget; it is re-enabled if the budget had switched it off.</summary>
        public static bool UnregisterLight(UnityEngine.Light light)
        {
            IHyperdriveRuntime runtime = s_runtime;
            return runtime != null && runtime.UnregisterLight(light);
        }

        internal static void Register(IHyperdriveRuntime runtime)
        {
            if (runtime == null || s_runtime != null)
                return;

            s_runtime = runtime;
            Action ready = Ready;
            Ready = null;
            if (ready == null)
                return;

            // One faulty subscriber must not prevent the others from being notified.
            foreach (Action handler in ready.GetInvocationList())
            {
                try { handler(); }
                catch (Exception e) { UnityEngine.Debug.LogError("[Hyperdrive] API Ready handler threw: " + e); }
            }
        }
    }

    internal interface IHyperdriveRuntime
    {
        string Version { get; }
        bool IsSubsystemActive(string name);
        bool RequestGcHold(float seconds);
        bool RegisterLight(UnityEngine.Light light, float priority);
        bool UnregisterLight(UnityEngine.Light light);
    }
}
