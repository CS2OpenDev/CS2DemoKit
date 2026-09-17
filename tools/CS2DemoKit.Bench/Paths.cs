#region

using System.Diagnostics;
using System.Reflection;

#endregion

namespace CS2DemoKit.Bench;

/// <summary>
///     Runs the paths benchmark: one child process per (round, demo, arm), a cooldown between
///     runs, a CSV row per measurement. A fresh process per arm is what makes the memory
///     high-water mark mean anything: nothing from a previous arm is committed when this one
///     starts. Rows for a demo are held until every arm of that round has run, then written
///     together only if each digest pair agrees; a disagreement is printed with both digests
///     and neither row lands in the CSV.
/// </summary>
internal static class Paths
{
    public static int Start(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(
                """
                usage: CS2DemoKit.Bench paths [options]
                  --demos <dir>     directory of .dem files (default: <repo>/demos)
                  --rounds <n>      passes over the demo set (default: 3)
                  --cooldown <sec>  pause before each run (default: 5)
                  --out <file>      CSV output path (default: paths.csv)
                  --label <string>  variant label per row (default: git short SHA)
                  --arms <a,b,..>   arms to run (default: all)
                """);
            Console.WriteLine("arms: " + string.Join(", ", PathMeasurement.AllArms));
            return 0;
        }

        PathOptions? options = PathOptions.Parse(args);
        return options is null ? 2 : Run(options);
    }

    public static int Measure(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("usage: CS2DemoKit.Bench path-measure <demo.dem> <arm> <label> <round>");
            return 2;
        }

        try
        {
            Console.WriteLine(PathMeasurement.Run(args[1], args[2], args[3], args[4]));
            return 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Run(PathOptions options)
    {
        string[] demos = Directory.Exists(options.DemoDirectory)
            ? Directory.GetFiles(options.DemoDirectory, "*.dem", SearchOption.TopDirectoryOnly)
                .OrderBy(p => new FileInfo(p).Length).ToArray()
            : [];

        if (demos.Length == 0)
        {
            Console.Error.WriteLine($"no .dem files under {options.DemoDirectory}");
            return 1;
        }

        (string exe, string? dllArgument) = ResolveSelf();
        if (exe.Length == 0)
        {
            Console.Error.WriteLine("cannot resolve this executable's path to spawn measurements");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output)) ?? ".");
        using StreamWriter csv = new(options.Output, false);
        csv.AutoFlush = true;
        csv.WriteLine(PathMeasurement.Header);

        int total = options.Rounds * demos.Length * options.Arms.Count;
        int n = 0, failed = 0, mismatched = 0;
        Stopwatch elapsed = Stopwatch.StartNew();

        Console.Error.WriteLine(
            $"label={options.Label} rounds={options.Rounds} demos={demos.Length} arms={options.Arms.Count} "
            + $"cooldown={options.CooldownSeconds}s -> {options.Output}");

        for (int round = 1; round <= options.Rounds; round++)
        {
            foreach (string demo in demos)
            {
                Dictionary<string, (string Row, string Digest)> rows = new(StringComparer.Ordinal);
                foreach (string arm in options.Arms)
                {
                    n++;
                    Thread.Sleep(TimeSpan.FromSeconds(options.CooldownSeconds));
                    double load = LoadAverage.OneMinute();

                    (string? row, string? error) = MeasureOne(exe, dllArgument, demo, arm, options.Label, round);
                    if (row is null)
                    {
                        failed++;
                        Console.Error.WriteLine($"[{n}/{total}] FAILED {arm} {Path.GetFileName(demo)} round {round}");
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            Console.Error.WriteLine(error.Trim());
                        }

                        continue;
                    }

                    rows[arm] = ($"{row},{load:F2}", DigestOf(row));
                    Console.Error.WriteLine(
                        $"[{n}/{total}] {elapsed.Elapsed.TotalSeconds:F0}s load={load:F2} "
                        + $"{arm} {Path.GetFileName(demo)} -> {Summarize(row)}");
                }

                foreach ((string a, string b) in PathMeasurement.DigestPairs)
                {
                    if (rows.TryGetValue(a, out (string Row, string Digest) ra)
                        && rows.TryGetValue(b, out (string Row, string Digest) rb)
                        && ra.Digest != rb.Digest)
                    {
                        mismatched++;
                        Console.Error.WriteLine(
                            $"DIGEST MISMATCH {Path.GetFileName(demo)} round {round}: {a}={ra.Digest} {b}={rb.Digest}; both rows dropped");
                        rows.Remove(a);
                        rows.Remove(b);
                    }
                }

                foreach (string arm in options.Arms)
                {
                    if (rows.TryGetValue(arm, out (string Row, string Digest) kept))
                    {
                        csv.WriteLine(kept.Row);
                    }
                }
            }
        }

        Console.Error.WriteLine(
            $"DONE {n - failed}/{total} runs in {elapsed.Elapsed.TotalSeconds:F0}s -> {options.Output}"
            + (failed > 0 ? $" ({failed} failed)" : "")
            + (mismatched > 0 ? $" ({mismatched} digest pairs disagreed)" : ""));
        return failed > 0 || mismatched > 0 ? 1 : 0;
    }

    private static (string? Row, string? Error) MeasureOne(
        string exe, string? dllArgument, string demo, string arm, string label, int round)
    {
        ProcessStartInfo psi = new()
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (dllArgument is not null)
        {
            psi.ArgumentList.Add(dllArgument);
        }

        psi.ArgumentList.Add("path-measure");
        psi.ArgumentList.Add(demo);
        psi.ArgumentList.Add(arm);
        psi.ArgumentList.Add(label);
        psi.ArgumentList.Add(round.ToString());
        psi.Environment["DOTNET_gcServer"] = "0";

        using Process child = Process.Start(psi)!;
        string stdout = child.StandardOutput.ReadToEnd();
        string stderr = child.StandardError.ReadToEnd();
        child.WaitForExit();

        string row = stdout.Trim();
        if (child.ExitCode != 0 || row.Length == 0)
        {
            return (null, stderr);
        }

        int expected = PathMeasurement.Header.Split(',').Length - 1;
        int fields = row.Split(',').Length;
        return fields == expected
            ? (row, null)
            : (null, $"row has {fields} fields where the header names {expected} before load1");
    }

    private static string DigestOf(string row)
    {
        string[] f = row.Split(',');
        return f[^1];
    }

    private static string Summarize(string row)
    {
        string[] f = row.Split(',');
        // wall_ms, peak_heap_mb, peak_working_set_mb
        return f.Length > 16 ? $"wall={f[5]}ms peakHeap={f[12]}MB peakWs={f[16]}MB" : row;
    }

    private static (string Exe, string? DllArgument) ResolveSelf()
    {
        string exe = Environment.ProcessPath ?? "";
        string dll = Assembly.GetEntryAssembly()?.Location ?? "";
        bool viaMuxer = Path.GetFileNameWithoutExtension(exe)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        return viaMuxer && dll.Length > 0 ? (exe, dll) : (exe, null);
    }
}
