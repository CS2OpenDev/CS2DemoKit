using System.Diagnostics;
using System.Reflection;

namespace CS2DemoKit.Bench;

/// <summary>
///     Runs the benchmark: one child process per (round, demo), with a cooldown between runs,
///     appending a CSV row per measurement.
///     <para>
///         The child is this same executable in <c>measure</c> mode. Looping in-process would be
///         simpler and wrong: a fresh heap and no allocator history per measurement is the property
///         the whole harness rests on.
///     </para>
///     <para>
///         Runs under workstation GC, which is what a consumer gets unless their app opts into server
///         GC. Server GC hides most of the collector cost, and the collector is where this pipeline's
///         time is.
///     </para>
/// </summary>
internal static class Sweep
{
    public static int Run(BenchOptions options)
    {
        string[] demos = Directory.Exists(options.DemoDirectory)
            ? Directory.GetFiles(options.DemoDirectory, "*.dem", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal).ToArray()
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
        csv.WriteLine(Measurement.Header);

        int total = options.Rounds * demos.Length;
        int n = 0, failed = 0;
        Stopwatch elapsed = Stopwatch.StartNew();

        Console.Error.WriteLine(
            $"label={options.Label} rounds={options.Rounds} demos={demos.Length} "
            + $"cooldown={options.CooldownSeconds}s -> {options.Output}");

        for (int round = 1; round <= options.Rounds; round++)
        {
            foreach (string demo in demos)
            {
                n++;
                Thread.Sleep(TimeSpan.FromSeconds(options.CooldownSeconds));

                // Stamped per row: a machine doing something else inflates every number here by a
                // wide margin, and without this column a contended sample is invisible afterwards.
                double load = LoadAverage.OneMinute();

                (string? row, string? error) = Measure(exe, dllArgument, demo, options.Label, round);
                if (row is null)
                {
                    failed++;
                    Console.Error.WriteLine($"[{n}/{total}] FAILED {Path.GetFileName(demo)} round {round}");
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        Console.Error.WriteLine(error.Trim());
                    }

                    continue;
                }

                csv.WriteLine($"{row},{load:F2}");
                Console.Error.WriteLine(
                    $"[{n}/{total}] {elapsed.Elapsed.TotalSeconds:F0}s load={load:F2} "
                    + $"{Path.GetFileName(demo)} -> {Summarize(row)}");
            }
        }

        Console.Error.WriteLine(
            $"DONE {n - failed}/{total} runs in {elapsed.Elapsed.TotalSeconds:F0}s -> {options.Output}"
            + (failed > 0 ? $" ({failed} failed)" : ""));
        return failed > 0 ? 1 : 0;
    }

    private static (string? Row, string? Error) Measure(
        string exe, string? dllArgument, string demo, string label, int round)
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

        psi.ArgumentList.Add("measure");
        psi.ArgumentList.Add(demo);
        psi.ArgumentList.Add(label);
        psi.ArgumentList.Add(round.ToString());

        // Belt and braces over the csproj setting: an inherited DOTNET_gcServer=1 would otherwise
        // silently change what is being measured.
        psi.Environment["DOTNET_gcServer"] = "0";

        using Process child = Process.Start(psi)!;
        string stdout = child.StandardOutput.ReadToEnd();
        string stderr = child.StandardError.ReadToEnd();
        child.WaitForExit();

        string row = stdout.Trim();
        return child.ExitCode != 0 || row.Length == 0 ? (null, stderr) : (row, null);
    }

    /// <summary>
    ///     How to re-invoke this program. Published as an apphost it is the executable itself; run
    ///     through <c>dotnet run</c> the process is the muxer, so the entry assembly has to be passed
    ///     back as the first argument or the child would re-run the SDK rather than the bench.
    /// </summary>
    private static (string Exe, string? DllArgument) ResolveSelf()
    {
        string exe = Environment.ProcessPath ?? "";
        string dll = Assembly.GetEntryAssembly()?.Location ?? "";
        bool viaMuxer = Path.GetFileNameWithoutExtension(exe)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        return viaMuxer && dll.Length > 0 ? (exe, dll) : (exe, null);
    }

    private static string Summarize(string row)
    {
        string[] f = row.Split(',');
        // parse_ms, parse_pause_ms, eval_ms, eval_pause_ms
        return f.Length > 15 ? $"parse={f[3]} eval={f[14]} evalPause={f[15]}" : row;
    }
}
