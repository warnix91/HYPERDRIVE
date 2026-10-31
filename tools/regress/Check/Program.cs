using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Hyperdrive regression check: reads the Player.log of each AutoBench run of tools/regress.sh / regress.ps1 and compares
// them with the machine's baseline (tools/regress/baseline_<machine>.txt):
//   FAIL  a run did not finish (no "AutoBench DONE": crash, freeze, timeout)
//   FAIL  a subsystem failed or threw (state Failed, "failed to start", "threw in"), or the runtime did not start
//   FAIL  an exception with Hyperdrive code in its stack (log relays excluded)
//   FAIL  an exception not in the baseline (new message + first stack frame)
//   FAIL  the heavy craft's temperatures at 10 s moved by more than 0.03 K from the baseline (or the run's own tolerance,
//         4th field of its thermal line: the PC's cycles run spreads 0.15 K with identical code)
// usage: Check <baseline file> [--update] <name>=<run dir> ...
internal static class Program
{
    private static readonly string[] Relays =
    {
        "UnityEngine.DebugLogHandler", "UnityEngine.Logger", "UnityEngine.Debug:", "UnityEngine.Debug.",
        "Hyperdrive.Diagnostics.GuardedLogHandler", "Hyperdrive.Loading.WorkerLogFilter"
    };

    private static readonly Regex ExceptionLine = new Regex(@"^([A-Za-z_][\w.`+]*Exception)(: .*)?$", RegexOptions.Compiled);
    private static readonly Regex Thermal = new Regex(@"AutoBench at 10 s: thermal: .*?max core ([\d.]+) K, max skin ([\d.]+) K, mean core ([\d.]+) K, mean skin ([\d.]+) K", RegexOptions.Compiled);
    private const double ThermalTolerance = 0.03;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: Check <baseline file> [--update] <name>=<run dir> ...");
            return 2;
        }
        string baselinePath = args[0];
        bool update = args.Contains("--update");
        var runs = args.Skip(1).Where(a => a.Contains('=')).Select(a => (Name: a.Substring(0, a.IndexOf('=')), Dir: a.Substring(a.IndexOf('=') + 1))).ToList();

        var baseSigs = new HashSet<string>(StringComparer.Ordinal);
        var baseLines = new List<string>();   // "run<TAB>signature", kept by --update
        var baseThermal = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var baseTolerance = new Dictionary<string, double>(StringComparer.Ordinal);   // optional 4th field: this run's own tolerance
        if (File.Exists(baselinePath))
        {
            foreach (string line in File.ReadAllLines(baselinePath))
            {
                string[] parts = line.Split('\t');
                if (parts.Length >= 3 && parts[0] == "exception")
                {
                    baseSigs.Add(parts[2]);   // known in any run: some stock errors only show up now and then (at quit...)
                    baseLines.Add(parts[1] + "\t" + parts[2]);
                }
                else if (parts.Length >= 3 && parts[0] == "thermal")
                {
                    baseThermal[parts[1]] = parts[2].Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    if (parts.Length >= 4)
                        baseTolerance[parts[1]] = double.Parse(parts[3], CultureInfo.InvariantCulture);
                }
            }
        }
        else if (!update)
            Console.WriteLine("No baseline yet (" + baselinePath + "): exceptions and temperatures are only listed. Run with --update once on a good build.");

        var failures = new List<string>();
        var newSigs = new SortedSet<string>(StringComparer.Ordinal);
        var newThermal = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            string log = Path.Combine(run.Dir, "Player.log");
            if (!File.Exists(log))
            {
                failures.Add(run.Name + ": no Player.log in " + run.Dir);
                continue;
            }
            string[] lines = File.ReadAllLines(log);
            Console.WriteLine("== " + run.Name + ": " + Summary(lines));

            if (!lines.Any(l => l.Contains("AutoBench DONE")))
                failures.Add(run.Name + ": the run did not finish (no 'AutoBench DONE': crash, freeze or timeout)");
            foreach (string l in lines)
            {
                if (l.Contains("[Hyperdrive] Runtime failed to start"))
                    failures.Add(run.Name + ": " + Trim(l));
                else if (l.Contains("[Hyperdrive]") && (l.Contains(" failed to start and was rolled back") || l.Contains(" and was disabled:") || l.Contains(" threw while disabling")))
                    failures.Add(run.Name + ": " + Trim(l));
                else if (l.StartsWith("[Hyperdrive]", StringComparison.Ordinal) && !l.Contains("LoadProblems") && !l.Contains("LogSpamGuard") && Regex.IsMatch(l, @": (System\.)?[\w.]+Exception: "))
                    failures.Add(run.Name + ": Hyperdrive logged an exception: " + Trim(l));
                else if (l.StartsWith("[Hyperdrive] Subsystems:", StringComparison.Ordinal))
                    foreach (Match m in Regex.Matches(l, @"(\w+)=Failed"))
                        failures.Add(run.Name + ": subsystem " + m.Groups[1].Value + " Failed");
            }

            // After "AutoBench DONE" the game quits: stock errors of its shutdown order (objects destroyed in any order) come
            // and go from run to run; they are not compared with the baseline (Hyperdrive code in them still fails).
            int done = Array.FindIndex(lines, l => l.Contains("AutoBench DONE"));
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = ExceptionLine.Match(lines[i].Trim());
                if (!m.Success)
                    continue;
                string frame = null;
                bool ours = false;
                for (int j = i + 1; j < lines.Length && j < i + 40; j++)
                {
                    string s = lines[j].Trim();
                    if (s.Length == 0 || s.StartsWith("(Filename:", StringComparison.Ordinal))
                        break;
                    if (Relays.Any(r => s.Contains(r)))
                        continue;
                    // The test driver started what threw (a scene change): it and its callers are not the mod's code.
                    if (s.Contains("Hyperdrive.Diagnostics.AutoBench"))
                        break;
                    if (s.Contains("Hyperdrive."))
                        ours = true;
                    if (frame == null)
                        frame = s;
                }
                string sig = Normalize(m.Value) + " @ " + Normalize(frame ?? "(no stack)");
                newSigs.Add(run.Name + "\t" + sig);
                if (ours)
                    failures.Add(run.Name + ": exception in Hyperdrive code: " + sig);
                else if (!update && baseSigs.Count > 0 && !baseSigs.Contains(sig) && (done < 0 || i < done))
                    failures.Add(run.Name + ": new exception (not in the baseline): " + sig);
            }

            Match t = lines.Select(l => Thermal.Match(l)).FirstOrDefault(x => x.Success);
            if (t != null)
            {
                double[] values = Enumerable.Range(1, 4).Select(k => double.Parse(t.Groups[k].Value, CultureInfo.InvariantCulture)).ToArray();
                newThermal[run.Name] = values;
                double tolerance = baseTolerance.TryGetValue(run.Name, out double own) ? own : ThermalTolerance;
                if (!update && baseThermal.TryGetValue(run.Name, out double[] b))
                    for (int k = 0; k < 4; k++)
                        if (Math.Abs(values[k] - b[k]) > tolerance)
                            failures.Add(run.Name + ": temperature " + k + " is " + values[k].ToString("F3", CultureInfo.InvariantCulture) + " K, baseline "
                                         + b[k].ToString("F3", CultureInfo.InvariantCulture) + " K (more than " + tolerance.ToString(CultureInfo.InvariantCulture) + " K apart)");
            }
        }

        if (update)
        {
            var sb = new StringBuilder();
            sb.Append("# Hyperdrive regression baseline (tools/regress): known exceptions per run, heavy craft temperatures at 10 s.\n");
            sb.Append("# Written by --update on a build known to be good. Lines: exception<TAB>run<TAB>signature | thermal<TAB>run<TAB>values[<TAB>tolerance K]\n");
            // Known exceptions are added to, never dropped: a modpack error that shows up one start in five would otherwise
            // leave the baseline with the run that happened to miss it, and fail the next battery that sees it.
            foreach (string s in baseLines)
                newSigs.Add(s);
            foreach (string s in newSigs)
                sb.Append("exception\t").Append(s).Append('\n');
            foreach (var kv in newThermal)
            {
                sb.Append("thermal\t").Append(kv.Key).Append('\t').Append(string.Join(" ", kv.Value.Select(v => v.ToString("F3", CultureInfo.InvariantCulture))));
                if (baseTolerance.TryGetValue(kv.Key, out double keep))
                    sb.Append('\t').Append(keep.ToString(CultureInfo.InvariantCulture));   // a run's own tolerance survives --update
                sb.Append('\n');
            }
            File.WriteAllText(baselinePath, sb.ToString());
            Console.WriteLine("Baseline written: " + baselinePath + " (" + newSigs.Count + " known exceptions, " + newThermal.Count + " temperature sets)");
        }

        foreach (string f in failures.Distinct())
            Console.WriteLine("FAIL  " + f);
        Console.WriteLine(failures.Count == 0 ? "RESULT: PASS" : "RESULT: FAIL (" + failures.Distinct().Count() + ")");
        return failures.Count == 0 ? 0 : 1;
    }

    private static string Summary(string[] lines)
    {
        string load = lines.Select(l => Regex.Match(l, @"LoadReport: .*\(of ([\d.]+) s total")).Where(m => m.Success).Select(m => m.Groups[1].Value + " s to menu").FirstOrDefault();
        string fps = lines.Select(l => Regex.Match(l, @"AutoBench REPORT: .*?\(([\d.]+) fps\)")).Where(m => m.Success).Select(m => m.Groups[1].Value + " fps").FirstOrDefault();
        string editor = lines.Any(l => l.Contains("AutoBench EDITOR")) ? "editor searches done" : null;
        string crash = lines.Select(l => Regex.Match(l, @"AutoBench CRASH.*")).Where(m => m.Success).Select(m => m.Value.Length > 90 ? m.Value.Substring(0, 90) : m.Value).FirstOrDefault();
        return string.Join(" | ", new[] { load, fps, editor, crash }.Where(s => s != null));
    }

    // Numbers, addresses and object names vary from run to run: the signature keeps the shape of the message.
    private static string Normalize(string s)
    {
        s = Regex.Replace(s, @"\[0x[0-9a-fA-F]+\]", "");
        s = Regex.Replace(s, @"<[0-9a-f]{32}>", "");
        s = Regex.Replace(s, @"'[^']*'", "'*'");
        s = Regex.Replace(s, @"\d+(\.\d+)?", "#");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 220 ? s.Substring(0, 220) : s;
    }

    private static string Trim(string s) => s.Length > 240 ? s.Substring(0, 240) : s;
}
