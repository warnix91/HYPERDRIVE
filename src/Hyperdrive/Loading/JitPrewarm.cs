using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using Hyperdrive.Throttle;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// Mono compiles each method the first time it runs. A mod's first explosion, first window or first destruction
    /// therefore pays for compiling all the code involved, which shows up as a one-off hitch (much worse on a Mac,
    /// where Rosetta also has to translate the freshly compiled code). Once the main menu is up, this subsystem
    /// compiles every method of the installed mods ahead of time on low-priority background threads (up to 6)
    /// (RuntimeMethodHandle.GetFunctionPointer forces Mono to JIT the method), so the main thread never waits for it.
    /// Mono may run a class constructor while it compiles (beforefieldinit types, inlined callees): a mod's static
    /// constructor would then run on this thread, and one that touches Unity crashes the game (seen with Astrogator's
    /// ViewTools creating a Texture2D). <see cref="CctorScan"/> reads each method's IL first and skips every method
    /// that could trigger a class constructor outside the .NET base library, so no mod code ever runs here.
    /// Mono itself can still fall over on some mod's metadata (an access violation inside its reflection code, seen in a
    /// 295-mod install): nothing can catch that, the game closes. So PluginData/JitPrewarm.txt names the assemblies being
    /// compiled while the work runs; if the game dies, the next start reads them there and leaves them to run time for good.
    /// An assembly whose types cannot all be loaded is left alone too.
    /// </summary>
    internal sealed class JitPrewarm : Subsystem
    {
        private bool _includeStock;
        private float _delaySeconds = 3f;

        private float _startAt = -1f;
        private bool _started;
        private Thread _worker;

        // Written by the worker, read by the main thread once Done is set.
        private volatile bool _done;
        private int _compiled, _skipped, _guarded, _failed, _assemblies, _workers = 1;
        private long _elapsedMs;
        private volatile bool _stop;

        private const string GuardFileName = "JitPrewarm.txt";
        private string _guardPath;
        private readonly HashSet<string> _skip = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _guardLock = new object();
        private volatile bool _quitting;   // a normal quit: the work in progress is not to blame

        public override string Name => "JitPrewarm";

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _includeStock = settings.GetBool("includeStock", false);
            _delaySeconds = Mathf.Clamp(settings.GetFloat("delaySeconds", _delaySeconds), 0f, 120f);
        }

        protected internal override void OnEnable() { }

        protected internal override void OnDisable()
        {
            _stop = true;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (!_started && scene == GameScenes.MAINMENU)
                _startAt = Time.unscaledTime + _delaySeconds;
        }

        protected internal override void OnUpdate()
        {
            if (!_started && _startAt >= 0f && Time.unscaledTime >= _startAt)
                Start();

            if (_done)
            {
                _done = false;
                Log.Info("JitPrewarm: compiled " + _compiled + " methods of " + _assemblies + " assemblies"
                         + (_includeStock ? " (mods + stock game)" : " (mods)") + " in " + _elapsedMs + " ms on " + _workers + " background thread(s)"
                         + (_skipped > 0 ? ", " + _skipped + " skipped (generic/abstract/extern)" : "")
                         + (_guarded > 0 ? ", " + _guarded + " left to run time (could trigger a class constructor)" : "")
                         + (_failed > 0 ? ", " + _failed + " failed" : ""));
            }
        }

        private void Start()
        {
            _started = true;
            ReadGuard();
            Application.quitting += () =>
            {
                _quitting = true;
                _stop = true;
                WriteGuard(null, null);
            };
            // Assembly list is read here on the main thread (KSP's AssemblyLoader is not thread-safe).
            var assemblies = new List<Assembly>();
            var seen = new HashSet<Assembly>();
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                Assembly assembly = loaded?.assembly;
                if (assembly == null || !seen.Add(assembly))
                    continue;
                if (!_includeStock && ModAssemblies.IsStock(assembly))
                    continue;
                if (_skip.Contains(AssemblyName(assembly)))
                    continue;
                assemblies.Add(assembly);
            }
            if (_includeStock)
            {
                Assembly stock = typeof(Part).Assembly;
                if (seen.Add(stock))
                    assemblies.Add(stock);
            }

            _worker = new Thread(() => Work(assemblies)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "Hyperdrive.JitPrewarm" };
            _worker.Start();
        }

        // Coordinator: the types of every assembly listed once, then shared out to several low-priority workers (each with its
        // own IL scanner) that claim them in turn; Mono compiles different methods on different threads concurrently.
        private void Work(List<Assembly> assemblies)
        {
            var watch = Stopwatch.StartNew();
            var types = new List<Type>();
            var owners = new List<int>();   // index in names of each type's assembly
            var names = new List<string>();
            int failed = 0;
            foreach (Assembly assembly in assemblies)
            {
                if (_stop)
                    break;
                Type[] found;
                try { found = assembly.GetTypes(); }
                catch (Exception) { failed++; continue; }   // types that cannot all load: leave the whole assembly alone
                names.Add(AssemblyName(assembly));
                types.AddRange(found);
                for (int k = 0; k < found.Length; k++)
                    owners.Add(names.Count - 1);
            }
            int workers = Math.Max(1, Math.Min(6, Environment.ProcessorCount - 2));
            var counts = new int[4];   // compiled, skipped, guarded, failed
            int next = -1;
            var threads = new List<Thread>();
            var inFlight = new int[workers];   // assembly each worker is compiling, -1 when none
            for (int w = 0; w < workers; w++)
                inFlight[w] = -1;
            int slots = -1;
            ThreadStart body = () =>
            {
                int slot = Interlocked.Increment(ref slots);
                var scan = new CctorScan();
                int compiled = 0, skipped = 0, guarded = 0, failedHere = 0;
                while (!_stop)
                {
                    int i = Interlocked.Increment(ref next);
                    if (i >= types.Count)
                        break;
                    if (inFlight[slot] != owners[i])
                    {
                        inFlight[slot] = owners[i];
                        WriteGuard(names, inFlight);
                    }
                    CompileType(types[i], scan, ref compiled, ref skipped, ref guarded, ref failedHere);
                }
                inFlight[slot] = -1;
                Interlocked.Add(ref counts[0], compiled);
                Interlocked.Add(ref counts[1], skipped);
                Interlocked.Add(ref counts[2], guarded);
                Interlocked.Add(ref counts[3], failedHere);
            };
            for (int w = 1; w < workers; w++)
            {
                var t = new Thread(body) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "Hyperdrive.JitPrewarm." + w };
                threads.Add(t);
                t.Start();
            }
            body();
            foreach (Thread t in threads)
                t.Join();
            WriteGuard(names, null);

            _compiled = counts[0];
            _skipped = counts[1];
            _guarded = counts[2];
            _failed = counts[3] + failed;
            _assemblies = assemblies.Count;
            _workers = workers;
            _elapsedMs = watch.ElapsedMilliseconds;
            _done = true;
        }

        private static string AssemblyName(Assembly assembly)
        {
            try { return assembly.GetName().Name; }
            catch (Exception) { return assembly.FullName ?? "?"; }
        }

        // Main thread, before the work: the assemblies of the guard file, and those that were being compiled when the
        // previous session died, are skipped from now on.
        private void ReadGuard()
        {
            var blamed = new List<string>();
            try
            {
                _guardPath = Path.Combine(Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"), GuardFileName);
                if (File.Exists(_guardPath))
                {
                    foreach (string raw in File.ReadAllLines(_guardPath))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("skip ", StringComparison.Ordinal))
                            _skip.Add(line.Substring(5).Trim());
                        else if (line.StartsWith("running ", StringComparison.Ordinal) && _skip.Add(line.Substring(8).Trim()))
                            blamed.Add(line.Substring(8).Trim());
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("JitPrewarm: " + GuardFileName + " not read (" + e.Message + ")");
            }
            if (blamed.Count > 0)
                Log.Warn("JitPrewarm: the game stopped while these assemblies were being compiled last time: " + string.Join(", ", blamed.ToArray())
                         + "; they are left to run time from now on (PluginData/" + GuardFileName + ", delete it to try them again)");
            else if (_skip.Count > 0)
                Log.Info("JitPrewarm: " + _skip.Count + " assemblies left to run time after an earlier stop (PluginData/" + GuardFileName + ")");
            WriteGuard(null, null);
        }

        // The skipped assemblies, then those being compiled now (null: none). Any thread; the file is closed after each
        // write, so it survives the game dying an instant later.
        private void WriteGuard(List<string> names, int[] inFlight)
        {
            if (_guardPath == null)
                return;
            lock (_guardLock)
            {
                try
                {
                    var text = new System.Text.StringBuilder();
                    text.Append("// Hyperdrive JitPrewarm: assemblies left to run time because the game stopped while they were compiled.\n");
                    text.Append("// \"running\" lines name the work in progress. Safe to delete.\n");
                    foreach (string name in _skip)
                        text.Append("skip ").Append(name).Append('\n');
                    if (names != null && inFlight != null && !_quitting)
                    {
                        var listed = new HashSet<int>();
                        foreach (int index in inFlight)
                            if (index >= 0 && listed.Add(index))
                                text.Append("running ").Append(names[index]).Append('\n');
                    }
                    File.WriteAllText(_guardPath, text.ToString());
                }
                catch (Exception)
                {
                    // a guard that cannot be written only loses its protection
                }
            }
        }

        private static void CompileType(Type type, CctorScan scan, ref int compiled, ref int skipped, ref int guarded, ref int failed)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            if (type == null || type.ContainsGenericParameters)
            {
                skipped++;
                return;
            }
            MethodBase[] methods;
            try
            {
                MethodInfo[] declared = type.GetMethods(flags);
                ConstructorInfo[] constructors = type.GetConstructors(flags);
                methods = new MethodBase[declared.Length + constructors.Length];
                declared.CopyTo(methods, 0);
                constructors.CopyTo(methods, declared.Length);
            }
            catch (Exception)
            {
                failed++;
                return;
            }
            foreach (MethodBase method in methods)
            {
                if (method.IsAbstract || method.ContainsGenericParameters || method.IsConstructor && method.IsStatic
                    || (method.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0
                    || (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    skipped++;
                    continue;
                }
                try
                {
                    if (scan.MayRunClassConstructor(method))
                    {
                        guarded++;
                        continue;
                    }
                    method.MethodHandle.GetFunctionPointer();
                    compiled++;
                }
                catch (Exception)
                {
                    failed++;
                }
            }
        }

        /// <summary>
        /// Conservative IL check, worker thread only: could compiling this method make Mono run a class constructor?
        /// True if the method's own type, or any static field, method or constructor it references, belongs to a type
        /// with a static constructor outside the .NET base library, or if it calls a method small enough to be inlined
        /// that is itself unsafe. Unreadable IL or unresolvable tokens count as unsafe. Reflection only: never runs code.
        /// </summary>
        internal sealed class CctorScan
        {
            // Mono inlines methods of up to ~20 IL bytes by default; a wider margin costs only some prewarm coverage.
            private const int InlineCandidateMaxILBytes = 64;

            private static readonly OpCode[] OneByte = new OpCode[256];
            private static readonly OpCode[] TwoByte = new OpCode[256];

            private readonly Dictionary<MethodBase, bool> _methods = new Dictionary<MethodBase, bool>();
            private readonly Dictionary<Type, bool> _types = new Dictionary<Type, bool>();

            static CctorScan()
            {
                foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!(field.GetValue(null) is OpCode op))
                        continue;
                    ushort value = (ushort)op.Value;
                    if (value < 0x100)
                        OneByte[value] = op;
                    else if ((value & 0xFF00) == 0xFE00)
                        TwoByte[value & 0xFF] = op;
                }
            }

            public bool MayRunClassConstructor(MethodBase method)
            {
                if (_methods.TryGetValue(method, out bool unsafeMethod))
                    return unsafeMethod;
                _methods[method] = true; // pessimistic while recursion is in progress (cycles)
                unsafeMethod = Scan(method);
                _methods[method] = unsafeMethod;
                return unsafeMethod;
            }

            private bool Scan(MethodBase method)
            {
                if (HasForeignInitializer(method.DeclaringType))
                    return true;

                byte[] il;
                Type[] typeArgs, methodArgs;
                try
                {
                    MethodBody body = method.GetMethodBody();
                    if (body == null)
                        return false;
                    il = body.GetILAsByteArray();
                    typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
                    methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
                }
                catch (Exception)
                {
                    return true;
                }
                if (il == null)
                    return false;

                int i = 0;
                while (i < il.Length)
                {
                    OpCode op;
                    byte code = il[i++];
                    if (code == 0xFE)
                    {
                        if (i >= il.Length)
                            return true;
                        op = TwoByte[il[i++]];
                    }
                    else
                        op = OneByte[code];

                    int size = OperandSize(op.OperandType, il, i);
                    if (size < 0 || i + size > il.Length)
                        return true;

                    if (op.OperandType == OperandType.InlineField && (op == OpCodes.Ldsfld || op == OpCodes.Ldsflda || op == OpCodes.Stsfld))
                    {
                        FieldInfo field;
                        try { field = method.Module.ResolveField(BitConverter.ToInt32(il, i), typeArgs, methodArgs); }
                        catch (Exception) { return true; }
                        if (field == null || HasForeignInitializer(field.DeclaringType))
                            return true;
                    }
                    else if (op.OperandType == OperandType.InlineMethod)
                    {
                        MethodBase callee;
                        try { callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArgs, methodArgs); }
                        catch (Exception) { return true; }
                        if (callee == null || HasForeignInitializer(callee.DeclaringType))
                            return true;
                        if (IsInlineCandidate(callee) && MayRunClassConstructor(callee))
                            return true;
                    }
                    i += size;
                }
                return false;
            }

            private static bool IsInlineCandidate(MethodBase callee)
            {
                try
                {
                    byte[] il = callee.GetMethodBody()?.GetILAsByteArray();
                    return il != null && il.Length <= InlineCandidateMaxILBytes;
                }
                catch (Exception)
                {
                    return true;
                }
            }

            // A static constructor that may touch Unity or game state. The base library's own initializers are safe.
            private bool HasForeignInitializer(Type type)
            {
                if (type == null)
                    return false;
                if (_types.TryGetValue(type, out bool foreign))
                    return foreign;
                try
                {
                    string assembly = type.Assembly.GetName().Name;
                    bool baseLibrary = assembly == "mscorlib" || assembly == "System" || assembly.StartsWith("System.", StringComparison.Ordinal)
                                       || assembly.StartsWith("Mono.", StringComparison.Ordinal);
                    foreign = !baseLibrary && type.TypeInitializer != null;
                }
                catch (Exception)
                {
                    foreign = true;
                }
                _types[type] = foreign;
                return foreign;
            }

            private static int OperandSize(OperandType type, byte[] il, int at)
            {
                switch (type)
                {
                    case OperandType.InlineNone:
                        return 0;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        return 1;
                    case OperandType.InlineVar:
                        return 2;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        return 8;
                    case OperandType.InlineSwitch:
                        return at + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, at) : -1;
                    default:
                        return 4;
                }
            }
        }
    }
}
