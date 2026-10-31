using System;
using System.IO;
using System.Runtime.InteropServices;
using Hyperdrive.Core;
using UnityEngine;
using UnityEngine.Profiling;

namespace Hyperdrive.Memory
{
    /// <summary>
    /// Binds to the Boehm GC functions exported by the Mono runtime that KSP already loaded
    /// (libmonobdwgc-2.0.dylib / mono-2.0-bdwgc.dll / libmonobdwgc-2.0.so). It never loads a second copy:
    /// it only looks up a module that is already in the process, and checks that the values it reads
    /// match what Unity reports before declaring itself available. Main thread only.
    /// </summary>
    internal static class BoehmGC
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate UIntPtr GetWordFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetWordFn(UIntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ExpandFn(UIntPtr bytes);

        private static GetWordFn s_getFreeSpaceDivisor;
        private static SetWordFn s_setFreeSpaceDivisor;
        private static GetWordFn s_getHeapSize;
        private static GetWordFn s_getFreeBytes;
        private static GetWordFn s_getBytesSinceGC;
        private static GetWordFn s_getGCNo;
        private static ExpandFn s_expandHeap;

        private static bool s_initialized;

        public static bool Available { get; private set; }

        /// <summary>Why the binding is unavailable, or the module it bound to.</summary>
        public static string Status { get; private set; } = "not initialized";

        /// <summary>Free space divisor found at startup, before anyone changed it.</summary>
        public static int OriginalFreeSpaceDivisor { get; private set; }

        public static bool TryInitialize()
        {
            if (s_initialized)
                return Available;
            s_initialized = true;

            try
            {
                IntPtr module = FindLoadedRuntime(out string moduleName);
                if (module == IntPtr.Zero)
                {
                    Status = "Mono GC module not found in process";
                    return false;
                }

                s_getFreeSpaceDivisor = Bind<GetWordFn>(module, "GC_get_free_space_divisor");
                s_setFreeSpaceDivisor = Bind<SetWordFn>(module, "GC_set_free_space_divisor");
                s_getHeapSize = Bind<GetWordFn>(module, "GC_get_heap_size");
                s_getFreeBytes = Bind<GetWordFn>(module, "GC_get_free_bytes");
                s_getBytesSinceGC = Bind<GetWordFn>(module, "GC_get_bytes_since_gc");
                s_getGCNo = Bind<GetWordFn>(module, "GC_get_gc_no");
                s_expandHeap = Bind<ExpandFn>(module, "GC_expand_hp");

                if (s_getFreeSpaceDivisor == null || s_setFreeSpaceDivisor == null || s_getHeapSize == null
                    || s_getFreeBytes == null || s_getBytesSinceGC == null || s_getGCNo == null || s_expandHeap == null)
                {
                    Status = "Boehm GC functions not exported by " + moduleName;
                    return false;
                }

                // Sanity check: the heap size we read must match what Unity reports for the same heap.
                long nativeHeap = (long)s_getHeapSize().ToUInt64();
                long unityHeap = Profiler.GetMonoHeapSizeLong();
                int divisor = (int)s_getFreeSpaceDivisor().ToUInt64();
                if (unityHeap <= 0 || nativeHeap < unityHeap / 2 || nativeHeap > unityHeap * 2 || divisor < 1 || divisor > 100)
                {
                    Status = "sanity check failed (native heap " + nativeHeap + ", Unity heap " + unityHeap + ", divisor " + divisor + ")";
                    return false;
                }

                OriginalFreeSpaceDivisor = divisor;
                Available = true;
                Status = "bound to " + moduleName;
                return true;
            }
            catch (Exception e)
            {
                Status = e.GetType().Name + ": " + e.Message;
                Available = false;
                return false;
            }
        }

        public static int FreeSpaceDivisor
        {
            get => Available ? (int)s_getFreeSpaceDivisor().ToUInt64() : 0;
            set
            {
                if (Available && value >= 1)
                    s_setFreeSpaceDivisor(new UIntPtr((uint)value));
            }
        }

        public static long HeapSize => Available ? (long)s_getHeapSize().ToUInt64() : Profiler.GetMonoHeapSizeLong();

        public static long FreeBytes => Available ? (long)s_getFreeBytes().ToUInt64() : 0;

        /// <summary>Bytes allocated since the last completed collection, or -1 if unavailable.</summary>
        public static long BytesSinceGC => Available ? (long)s_getBytesSinceGC().ToUInt64() : -1;

        public static long CollectionNumber => Available ? (long)s_getGCNo().ToUInt64() : GC.CollectionCount(0);

        /// <summary>Grow the heap now by <paramref name="bytes"/> (Boehm GC_expand_hp). Returns false on failure.</summary>
        public static bool ExpandHeap(long bytes) => Available && bytes > 0 && s_expandHeap(new UIntPtr((ulong)bytes)) != 0;

        private static T Bind<T>(IntPtr module, string symbol) where T : class
        {
            IntPtr address = Lookup(module, symbol);
            return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        // ---- platform module lookup (already-loaded modules only) ----

        private const int RTLD_LAZY = 0x1;
        private const int RTLD_NOLOAD_MAC = 0x10;
        private const int RTLD_NOLOAD_LINUX = 0x4;

        [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dlopen")] private static extern IntPtr MacDlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dlsym")] private static extern IntPtr MacDlsym(IntPtr handle, string symbol);
        [DllImport("libdl.so.2", EntryPoint = "dlopen")] private static extern IntPtr LinuxDlopen(string path, int mode);
        [DllImport("libdl.so.2", EntryPoint = "dlsym")] private static extern IntPtr LinuxDlsym(IntPtr handle, string symbol);
        // glibc 2.34 and later have dlopen/dlsym in libc itself; libdl.so.2 is then a stub that may be missing.
        [DllImport("libc.so.6", EntryPoint = "dlopen")] private static extern IntPtr LibcDlopen(string path, int mode);
        [DllImport("libc.so.6", EntryPoint = "dlsym")] private static extern IntPtr LibcDlsym(IntPtr handle, string symbol);
        private static bool s_useLibc;

        private static IntPtr LinuxOpen(string path, int mode)
        {
            if (!s_useLibc)
            {
                try { return LinuxDlopen(path, mode); }
                catch (DllNotFoundException) { s_useLibc = true; }
                catch (EntryPointNotFoundException) { s_useLibc = true; }
            }
            return LibcDlopen(path, mode);
        }

        private static IntPtr LinuxSymbol(IntPtr handle, string symbol) => s_useLibc ? LibcDlsym(handle, symbol) : LinuxDlsym(handle, symbol);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string moduleName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)] private static extern IntPtr GetProcAddress(IntPtr module, string procName);

        private static IntPtr FindLoadedRuntime(out string moduleName)
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXPlayer:
                {
                    string[] candidates =
                    {
                        Path.Combine(Path.Combine(Application.dataPath, "Frameworks"), "libmonobdwgc-2.0.dylib"),
                        "@executable_path/../Frameworks/libmonobdwgc-2.0.dylib",
                        "libmonobdwgc-2.0.dylib"
                    };
                    foreach (string candidate in candidates)
                    {
                        IntPtr handle = MacDlopen(candidate, RTLD_LAZY | RTLD_NOLOAD_MAC);
                        if (handle != IntPtr.Zero)
                        {
                            moduleName = candidate;
                            return handle;
                        }
                    }
                    break;
                }
                case RuntimePlatform.WindowsPlayer:
                {
                    IntPtr handle = GetModuleHandleW("mono-2.0-bdwgc.dll");
                    if (handle != IntPtr.Zero)
                    {
                        moduleName = "mono-2.0-bdwgc.dll";
                        return handle;
                    }
                    break;
                }
                case RuntimePlatform.LinuxPlayer:
                {
                    // Unity's Linux player keeps Mono in <Game>_Data/MonoBleedingEdge/x86_64.
                    string[] candidates =
                    {
                        Path.Combine(Path.Combine(Path.Combine(Application.dataPath, "MonoBleedingEdge"), "x86_64"), "libmonobdwgc-2.0.so"),
                        "libmonobdwgc-2.0.so"
                    };
                    foreach (string candidate in candidates)
                    {
                        IntPtr handle = LinuxOpen(candidate, RTLD_LAZY | RTLD_NOLOAD_LINUX);
                        if (handle != IntPtr.Zero)
                        {
                            moduleName = candidate;
                            return handle;
                        }
                    }
                    break;
                }
            }
            moduleName = null;
            return IntPtr.Zero;
        }

        private static IntPtr Lookup(IntPtr module, string symbol)
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXPlayer: return MacDlsym(module, symbol);
                case RuntimePlatform.WindowsPlayer: return GetProcAddress(module, symbol);
                case RuntimePlatform.LinuxPlayer: return LinuxSymbol(module, symbol);
                default: return IntPtr.Zero;
            }
        }

        public static string Describe()
        {
            if (!Available)
                return "native Boehm API unavailable (" + Status + ")";
            const float mb = 1024f * 1024f;
            return "native Boehm API " + Status + " | divisor=" + FreeSpaceDivisor + " (original " + OriginalFreeSpaceDivisor + ")"
                   + " heap=" + (HeapSize / mb).ToString("F0") + " MB free=" + (FreeBytes / mb).ToString("F0")
                   + " MB sinceGC=" + (BytesSinceGC / mb).ToString("F0") + " MB gcNo=" + CollectionNumber;
        }
    }
}
