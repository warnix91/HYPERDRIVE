using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// PartLoader.ApplyPartValue (every value of every PART node: 27800 calls on a heavy modpack) looks up the field by
    /// name with Type.GetField, the parser with Type.GetMethod("Parse", [string]) and sets the field with
    /// Type.InvokeMember(name, SetField, ...), three reflection lookups by name per value. Here those three calls go
    /// through caches keyed by type and name: the same FieldInfo/MethodInfo stock would get (the lookups only depend on
    /// the type and the name; failures are not cached, so stock's exceptions and messages are unchanged), and the field
    /// is set with the same default binder InvokeMember uses. Anything unusual (other binding flags, a binder, several
    /// arguments) goes to the stock call. Stands down with KSPCommunityFixes PartParsingPerf (which replaces the method).
    /// </summary>
    internal sealed class PartValueCache : Subsystem
    {
        private static readonly MethodInfo GetFieldByName = AccessTools.Method(typeof(Type), nameof(Type.GetField), new[] { typeof(string) });
        private static readonly MethodInfo GetMethodByTypes = AccessTools.Method(typeof(Type), nameof(Type.GetMethod), new[] { typeof(string), typeof(Type[]) });
        private static readonly MethodInfo InvokeMember = AccessTools.Method(typeof(Type), nameof(Type.InvokeMember),
            new[] { typeof(string), typeof(BindingFlags), typeof(Binder), typeof(object), typeof(object[]) });

        private static readonly Dictionary<Type, Dictionary<string, FieldInfo>> s_fields = new Dictionary<Type, Dictionary<string, FieldInfo>>();
        private static readonly Dictionary<Type, MethodInfo> s_parsers = new Dictionary<Type, MethodInfo>();
        private static int s_fieldSites, s_methodSites, s_invokeSites;
        private static long s_sets;
        private static bool s_fingerprint, s_bypass, s_dump;

        public override string Name => "PartValueCache";
        public override string[] KspcfEquivalents => new[] { "PartParsingPerf" };
        public override bool YieldOnSharedTargets => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            s_fingerprint = settings.GetBool("fingerprint", false);
            s_bypass = settings.GetBool("bypass", false);
            s_dump = settings.GetBool("fingerprintDump", false);   // developer: every value in PluginData, to diff two runs   // developer: no patch, fingerprint only (stock reference)
        }

        protected internal override void OnEnable()
        {
            if (GetFieldByName == null || GetMethodByTypes == null || InvokeMember == null)
                throw new SubsystemUnavailableException("reflection methods not found");
            MethodInfo apply = AccessTools.Method(typeof(PartLoader), "ApplyPartValue");
            if (apply == null)
                throw new SubsystemUnavailableException("PartLoader.ApplyPartValue not found (game version mismatch?)");
            if (s_bypass)
            {
                Log.Info("PartValueCache: bypassed (stock ApplyPartValue), fingerprint only");
                return;
            }
            s_fieldSites = s_methodSites = s_invokeSites = 0;
            Patch(apply, transpiler: nameof(Transpiler));
            // KSP 1.12.5: 1 GetField, 2 GetMethod, 11 InvokeMember.
            if (s_fieldSites != 1 || s_methodSites != 2 || s_invokeSites != 11)
                throw new InvalidOperationException("ApplyPartValue: " + s_fieldSites + "/" + s_methodSites + "/" + s_invokeSites
                                                    + " reflection calls found, 1/2/11 expected (game version mismatch?)");
            Log.Info("PartValueCache: part config values set through cached field and parser lookups");
        }

        protected internal override void OnDisable()
        {
            s_fields.Clear();
            s_parsers.Clear();
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (scene == GameScenes.MAINMENU && s_sets > 0)
                Log.Info("PartValueCache: " + s_sets + " part values set through the cache");
            if (scene == GameScenes.MAINMENU && s_fingerprint)
                Log.Info("PartValueCache: part prefab fingerprint " + Fingerprint());
        }

        // Developer check (fingerprint = true): a hash of every public field of every part prefab, to compare a run with
        // this subsystem against one without it.
        internal static string Fingerprint()
        {
            ulong hash = 14695981039346656037UL;
            int parts = 0, values = 0;
            System.Text.StringBuilder dump = s_dump ? new System.Text.StringBuilder(1 << 22) : null;
            foreach (AvailablePart ap in PartLoader.LoadedPartsList)
            {
                Part p = ap?.partPrefab;
                if (p == null)
                    continue;
                parts++;
                foreach (FieldInfo f in p.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    Type t = f.FieldType;
                    if (!t.IsPrimitive && !t.IsEnum && t != typeof(string) && t != typeof(UnityEngine.Vector2) && t != typeof(UnityEngine.Vector3)
                        && t != typeof(UnityEngine.Vector4) && t != typeof(UnityEngine.Quaternion) && t != typeof(UnityEngine.Color))
                        continue;
                    // craftID is a runtime id from object creation order (PC: the only value that differed between two stock
                    // runs, on every part), not a configured value: left out.
                    if (f.Name == "craftID")
                        continue;
                    object v = f.GetValue(p);
                    string text = f.Name + "=" + (v is float fl ? fl.ToString("R", CultureInfo.InvariantCulture)
                                                : v is double d ? d.ToString("R", CultureInfo.InvariantCulture)
                                                : Convert.ToString(v, CultureInfo.InvariantCulture));
                    foreach (char c in text)
                        hash = (hash ^ c) * 1099511628211UL;
                    values++;
                    dump?.Append(ap.name).Append('\t').Append(text).Append('\n');
                }
            }
            if (dump != null)
            {
                try
                {
                    string file = System.IO.Path.Combine(System.IO.Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"),
                                                         "part_values_" + (s_bypass ? "stock" : "cache") + ".txt");
                    System.IO.File.WriteAllText(file, dump.ToString());
                }
                catch (Exception e) { Log.Warn("PartValueCache: dump failed (" + e.Message + ")"); }
            }
            return hash.ToString("x16") + " (" + parts + " parts, " + values + " values)";
        }

        // Each callvirt on Type becomes a call to a static helper taking the same arguments (the Type first).
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Callvirt && instruction.operand is MethodInfo m)
                {
                    if (m == GetFieldByName)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(PartValueCache), nameof(Field));
                        s_fieldSites++;
                    }
                    else if (m == GetMethodByTypes)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(PartValueCache), nameof(Method));
                        s_methodSites++;
                    }
                    else if (m == InvokeMember)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(PartValueCache), nameof(Invoke));
                        s_invokeSites++;
                    }
                }
                yield return instruction;
            }
        }

        private static FieldInfo Field(Type type, string name)
        {
            if (type == null || name == null)
                return type.GetField(name);   // stock's exception
            if (!s_fields.TryGetValue(type, out Dictionary<string, FieldInfo> byName))
                s_fields[type] = byName = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            if (byName.TryGetValue(name, out FieldInfo field))
                return field;
            field = type.GetField(name);   // throws as stock on an ambiguous name: nothing cached
            byName[name] = field;          // null cached too: the same type never gains a field
            return field;
        }

        private static MethodInfo Method(Type type, string name, Type[] types)
        {
            if (type == null || name != "Parse" || types == null || types.Length != 1 || types[0] != typeof(string))
                return type.GetMethod(name, types);
            if (s_parsers.TryGetValue(type, out MethodInfo parse))
                return parse;
            parse = type.GetMethod(name, types);
            s_parsers[type] = parse;
            return parse;
        }

        // InvokeMember(name, SetField, null, target, [value]): Mono looks the field up with Public | Instance | Static (no
        // lookup flags given) and sets it with the default binder; FieldInfo.SetValue does the same with the field
        // Type.GetField(name) returns (same default flags).
        private static object Invoke(Type type, string name, BindingFlags flags, Binder binder, object target, object[] args)
        {
            if (type == null || name == null || flags != BindingFlags.SetField || binder != null || target == null || args == null || args.Length != 1)
                return type.InvokeMember(name, flags, binder, target, args);
            FieldInfo field = Field(type, name);
            if (field == null || field.IsLiteral || field.IsInitOnly)
                return type.InvokeMember(name, flags, binder, target, args);
            field.SetValue(target, args[0], BindingFlags.SetField, null, CultureInfo.CurrentCulture);
            s_sets++;
            return null;
        }
    }
}
