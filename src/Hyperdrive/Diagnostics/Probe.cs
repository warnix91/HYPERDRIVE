using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Memory;
using Hyperdrive.Patching;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace Hyperdrive.Diagnostics
{
    /// <summary>
    /// Read-only diagnostics: logs the machine, graphics API, GC mode and memory at startup, then load time,
    /// GC count and a Harmony audit (who patches what) when the main menu is first reached.
    /// Everything we tune later (heap padding, instancing fallbacks...) depends on these numbers.
    /// </summary>
    internal sealed class Probe : Subsystem
    {
        private const float MB = 1024f * 1024f;

        // Mods whose presence changes what Hyperdrive should do.
        private static readonly string[] NotableAssemblies =
        {
            "KSPCommunityFixes", "ModuleManager", "Kopernicus", "Scatterer", "EVEManager", "Parallax",
            "Deferred", "TUFX", "MemGraph", "HeapPadder", "ReplayFX", "VolumetricExplosionFX", "GroundBlastFX"
        };

        private readonly SubsystemHost _host;
        private bool _mainMenuReported;
        private bool _auditHarmony;

        public Probe(SubsystemHost host) => _host = host;

        public override string Name => "Probe";
        public override bool ReadOnly => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _auditHarmony = settings.GetBool("auditHarmonyAtMainMenu", true);
        }

        protected internal override void OnEnable()
        {
            var sb = new StringBuilder(2048);
            sb.AppendLine("===== Probe: startup =====");
            sb.Append("Hyperdrive ").Append(HyperdriveRuntime.Version)
              .Append(" | KSP ").Append(Versioning.GetVersionStringFull())
              .Append(" | Unity ").AppendLine(Application.unityVersion);
            sb.Append("OS: ").Append(SystemInfo.operatingSystem)
              .Append(" | 64-bit process: ").Append(Environment.Is64BitProcess)
              .Append(" | Rosetta 2: ").AppendLine(RosettaStatus());
            sb.Append("CPU: ").Append(SystemInfo.processorType).Append(" x").Append(SystemInfo.processorCount)
              .Append(" | RAM: ").Append(SystemInfo.systemMemorySize).AppendLine(" MB");
            sb.Append("GPU: ").Append(SystemInfo.graphicsDeviceName)
              .Append(" | API: ").Append(SystemInfo.graphicsDeviceType).Append(" (").Append(SystemInfo.graphicsDeviceVersion).Append(')')
              .Append(" | VRAM: ").Append(SystemInfo.graphicsMemorySize).AppendLine(" MB");
            sb.Append("GPU caps: instancing=").Append(SystemInfo.supportsInstancing)
              .Append(" compute=").Append(SystemInfo.supportsComputeShaders)
              .Append(" maxTexture=").Append(SystemInfo.maxTextureSize)
              .Append(" shaderLevel=").Append(SystemInfo.graphicsShaderLevel)
              .Append(" multithreadedRendering=").Append(SystemInfo.graphicsMultiThreaded).AppendLine();
            sb.Append("GC: mode=").Append(GarbageCollector.GCMode)
              .Append(" incremental=").Append(GarbageCollector.isIncremental)
              .Append(" incrementalSlice=").Append(GarbageCollector.incrementalTimeSliceNanoseconds / 1000000.0).Append(" ms")
              .Append(" maxGeneration=").Append(GC.MaxGeneration).AppendLine();
            BoehmGC.TryInitialize();
            sb.Append("Boehm: ").AppendLine(BoehmGC.Describe());
            AppendMemory(sb);
            sb.Append("Harmony: ").Append(typeof(Harmony).Assembly.GetName().Version)
              .Append(" | Loaded plugin assemblies: ").Append(AssemblyLoader.loadedAssemblies.Count).AppendLine();
            sb.Append("Notable mods: ").Append(NotableModsSummary());
            Log.Info(sb.ToString());
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (_mainMenuReported || scene != GameScenes.MAINMENU)
                return;
            _mainMenuReported = true;

            var sb = new StringBuilder(4096);
            sb.AppendLine("===== Probe: main menu =====");
            sb.Append("Load time to main menu: ").Append(Time.realtimeSinceStartup.ToString("F1")).AppendLine(" s");
            sb.Append("GC collections so far: gen0=").Append(GC.CollectionCount(0)).AppendLine();
            sb.Append("Boehm: ").AppendLine(BoehmGC.Describe());
            AppendMemory(sb);
            sb.Append("Hyperdrive subsystems: ").AppendLine(_host.Summary());
            if (_auditHarmony)
                AppendHarmonyAudit(sb);
            Log.Info(sb.ToString());
        }

        private static void AppendMemory(StringBuilder sb)
        {
            sb.Append("Memory: mono heap=").Append((Profiler.GetMonoHeapSizeLong() / MB).ToString("F0"))
              .Append(" MB used=").Append((Profiler.GetMonoUsedSizeLong() / MB).ToString("F0"))
              .Append(" MB | unity allocated=").Append((Profiler.GetTotalAllocatedMemoryLong() / MB).ToString("F0"))
              .Append(" MB reserved=").Append((Profiler.GetTotalReservedMemoryLong() / MB).ToString("F0"))
              .Append(" MB | GC.GetTotalMemory=").Append((GC.GetTotalMemory(false) / MB).ToString("F0")).AppendLine(" MB");
        }

        private static string NotableModsSummary()
        {
            var found = new List<string>();
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                if (Array.IndexOf(NotableAssemblies, loaded.name) >= 0)
                    found.Add(loaded.name + " " + loaded.assembly.GetName().Version);
            }
            return found.Count == 0 ? "none" : string.Join(", ", found.ToArray());
        }

        /// <summary>Lists Harmony owners by number of patched methods and the methods patched by several owners.</summary>
        private static void AppendHarmonyAudit(StringBuilder sb)
        {
            var methodsByOwner = new Dictionary<string, int>();
            var shared = new List<string>();
            int total = 0;

            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null)
                    continue;
                total++;

                foreach (string owner in info.Owners)
                {
                    methodsByOwner.TryGetValue(owner, out int count);
                    methodsByOwner[owner] = count + 1;
                }

                if (info.Owners.Count > 1)
                    shared.Add(method.DeclaringType?.FullName + "." + method.Name + " <- " + string.Join(", ", new List<string>(info.Owners).ToArray()));
            }

            sb.Append("Harmony audit: ").Append(total).Append(" patched methods, ").Append(methodsByOwner.Count).AppendLine(" owners");
            foreach (KeyValuePair<string, int> pair in methodsByOwner)
                sb.Append("  ").Append(pair.Key).Append(": ").Append(pair.Value).AppendLine(" methods");

            const int maxShared = 30;
            sb.Append("Methods patched by several owners: ").Append(shared.Count).AppendLine();
            for (int i = 0; i < shared.Count && i < maxShared; i++)
                sb.Append("  ").AppendLine(shared[i]);
            if (shared.Count > maxShared)
                sb.Append("  ... ").Append(shared.Count - maxShared).AppendLine(" more");
        }

        // macOS only: reports whether this x86_64 process runs translated by Rosetta 2 (sysctl.proc_translated).
        [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "sysctlbyname")]
        private static extern int SysctlByName(string name, ref int value, ref IntPtr length, IntPtr newValue, IntPtr newLength);

        private static string RosettaStatus()
        {
            if (Application.platform != RuntimePlatform.OSXPlayer)
                return "n/a";

            try
            {
                int translated = 0;
                IntPtr length = (IntPtr)sizeof(int);
                if (SysctlByName("sysctl.proc_translated", ref translated, ref length, IntPtr.Zero, IntPtr.Zero) != 0)
                    return "no (native)";
                return translated == 1 ? "yes (x86_64 translated on Apple Silicon)" : "no (native)";
            }
            catch (Exception e)
            {
                return "unknown (" + e.GetType().Name + ")";
            }
        }
    }
}
