using System;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;

namespace Hyperdrive.Patching
{
    internal enum SubsystemState
    {
        NotStarted,
        Active,
        DisabledByConfig,
        StoodDown,
        Failed
    }

    /// <summary>
    /// Thrown from <see cref="Subsystem.OnEnable"/> when the feature cannot run on this machine or game setup
    /// (missing native API, unsupported platform...). The host stands the subsystem down without logging an error.
    /// </summary>
    internal sealed class SubsystemUnavailableException : Exception
    {
        public SubsystemUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// One independently switchable feature. Each subsystem owns its own Harmony instance
    /// ("Hyperdrive.&lt;Name&gt;") so that a failure can roll back exactly its patches and nothing else.
    /// Lifecycle hooks are called by <see cref="SubsystemHost"/> inside try/catch: any exception
    /// disables the subsystem instead of propagating into KSP.
    /// </summary>
    internal abstract class Subsystem
    {
        public abstract string Name { get; }

        public virtual bool EnabledByDefault => true;

        /// <summary>
        /// Assembly names (as listed by KSP's AssemblyLoader) that make this subsystem stand down,
        /// e.g. "KSPCommunityFixes" for a feature that KSPCF already provides.
        /// </summary>
        public virtual string[] IncompatibleAssemblies => Array.Empty<string>();

        /// <summary>
        /// Optional KSPCommunityFixes patches (names as in its Settings.cfg) that do this subsystem's job: when KSPCF has
        /// one of them enabled, this subsystem stays off and leaves the job to it (see OtherOptimizers).
        /// </summary>
        public virtual string[] KspcfEquivalents => Array.Empty<string>();

        /// <summary>
        /// Stand down when another optimization mod also patches one of this subsystem's methods (checked once every mod
        /// has patched): for fixes and physics shortcuts whose result was only verified against stock code.
        /// </summary>
        public virtual bool YieldOnSharedTargets => false;

        /// <summary>
        /// Only observes (logs, measures, shows): changes nothing in how the game runs. Read-only subsystems keep running on
        /// an untested game version and during a prudent start; the others stand down there.
        /// </summary>
        public virtual bool ReadOnly => false;

        /// <summary>Stood down because KSPCF may have an equivalent enabled: decided again once its settings are known.</summary>
        internal bool WaitingForKspcf { get; set; }

        public SubsystemState State { get; internal set; }

        /// <summary>Why the subsystem is not active (config, incompatible mod, exception).</summary>
        public string StateReason { get; internal set; }

        public Harmony Harmony { get; private set; }

        public string HarmonyId => SubsystemHost.HarmonyIdPrefix + Name;

        internal void AttachHarmony() => Harmony = new Harmony(HarmonyId);

        /// <summary>Read this subsystem's settings. Called before <see cref="OnEnable"/>.</summary>
        protected internal virtual void LoadSettings(SettingsSection settings) { }

        /// <summary>Apply patches and start services. Throwing here rolls the subsystem back.</summary>
        protected internal abstract void OnEnable();

        /// <summary>Undo side effects other than Harmony patches (those are removed by the host).</summary>
        protected internal virtual void OnDisable() { }

        /// <summary>Called every frame on the main thread while active. Must not allocate.</summary>
        protected internal virtual void OnUpdate() { }

        /// <summary>Called on the main thread when a scene is loaded and its GUI is ready.</summary>
        protected internal virtual void OnSceneReady(GameScenes scene) { }

        /// <summary>
        /// Patch <paramref name="original"/> with static methods of this subsystem's class, looked up by name.
        /// Throws if anything is missing so that the host rolls the whole subsystem back.
        /// </summary>
        protected void Patch(MethodBase original, string prefix = null, string postfix = null, string transpiler = null, string finalizer = null)
        {
            if (original == null)
                throw new MissingMethodException(Name + ": target method not found (game or mod version mismatch?)");

            Harmony.Patch(original,
                prefix: PatchMethod(prefix),
                postfix: PatchMethod(postfix),
                transpiler: PatchMethod(transpiler),
                finalizer: PatchMethod(finalizer));
        }

        private HarmonyMethod PatchMethod(string name)
        {
            if (name == null)
                return null;

            MethodInfo method = AccessTools.Method(GetType(), name);
            if (method == null || !method.IsStatic)
                throw new MissingMethodException(Name + ": patch method '" + name + "' not found or not static");
            return new HarmonyMethod(method);
        }
    }
}
