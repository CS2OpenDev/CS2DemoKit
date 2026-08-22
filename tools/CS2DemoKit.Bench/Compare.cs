using System.Diagnostics;

namespace CS2DemoKit.Bench;

/// <summary>
///     Interleaved A/B between two published builds of this tool, writing both arms to one CSV.
///     <para>
///         Two builds rather than two modes because the thing under test is the library the bench
///         links, so each arm has to be published from its own checkout:
///         <c>dotnet publish tools/CS2DemoKit.Bench -c Release -o /some/dir</c> on each side.
///     </para>
///     <para>
///         Arm order flips every round. Running one arm to completion and then the other lets a
///         thermal or scheduling drift land entirely on the second, which reads as a result; this way
///         the drift hits both. Compare medians per arm, and check that a metric the change should
///         not have touched held steady before believing one that moved.
///     </para>
/// </summary>
internal static class Compare
{
    public static int Run(CompareOptions options)
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

        foreach ((string label, string exe) in new[] { options.A, options.B })
        {
            if (!File.Exists(exe))
            {
                Console.Error.WriteLine($"arm '{label}': no bench executable at {exe}");
                return 1;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output)) ?? ".");
        using StreamWriter csv = new(options.Output, false);
        csv.AutoFlush = true;
        csv.WriteLine(Measurement.Header);

        int total = options.Rounds * demos.Length * 2;
        int n = 0, failed = 0;
        Stopwatch elapsed = Stopwatch.StartNew();

        Console.Error.WriteLine(
            $"A={options.A.Label} B={options.B.Label} rounds={options.Rounds} demos={demos.Length} "
            + $"cooldown={options.CooldownSeconds}s -> {options.Output}");

        for (int round = 1; round <= options.Rounds; round++)
        {
            (string Label, string Exe)[] order = round % 2 == 1
                ? [options.A, options.B]
                : [options.B, options.A];

            foreach (string demo in demos)
            {
                foreach ((string label, string exe) in order)
                {
                    n++;
                    Thread.Sleep(TimeSpan.FromSeconds(options.CooldownSeconds));
                    double load = LoadAverage.OneMinute();

                    (string? row, string? error) = Measure(exe, demo, label, round);
                    if (row is null)
                    {
                        failed++;
                        Console.Error.WriteLine(
                            $"[{n}/{total}] FAILED {label} {Path.GetFileName(demo)} round {round}");
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            Console.Error.WriteLine(error.Trim());
                        }

                        continue;
                    }

                    csv.WriteLine($"{row},{load:F2}");
                    Console.Error.WriteLine(
                        $"[{n}/{total}] {elapsed.Elapsed.TotalSeconds:F0}s load={load:F2} {label} "
                        + $"{Path.GetFileName(demo)}");
                }
            }
        }

        Console.Error.WriteLine(
            $"DONE {n - failed}/{total} runs in {elapsed.Elapsed.TotalSeconds:F0}s -> {options.Output}"
            + (failed > 0 ? $" ({failed} failed)" : ""));
        return failed > 0 ? 1 : 0;
    }

    private static (string? Row, string? Error) Measure(string exe, string demo, string label, int round)
    {
        ProcessStartInfo psi = new()
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("measure");
        psi.ArgumentList.Add(demo);
        psi.ArgumentList.Add(label);
        psi.ArgumentList.Add(round.ToString());
        psi.Environment["DOTNET_gcServer"] = "0";

        using Process child = Process.Start(psi)!;
        string stdout = child.StandardOutput.ReadToEnd();
        string stderr = child.StandardError.ReadToEnd();
        child.WaitForExit();

        string row = stdout.Trim();
        return child.ExitCode != 0 || row.Length == 0 ? (null, stderr) : (row, null);
    }
}

/// <summary>Parsed <c>compare</c> arguments.</summary>
internal sealed record CompareOptions
{
    public (string Label, string Exe) A { get; init; } = ("a", "");
    public (string Label, string Exe) B { get; init; } = ("b", "");
    public string DemoDirectory { get; init; } = "";
    public int Rounds { get; init; } = 5;
    public int CooldownSeconds { get; init; } = 12;
    public string Output { get; init; } = "compare.csv";

    /// <summary>Parses <c>compare</c> options. Returns null and writes to stderr on a bad argument.</summary>
    public static CompareOptions? Parse(string[] args, string defaultDemoDirectory)
    {
        CompareOptions o = new() { DemoDirectory = defaultDemoDirectory };
        string? aDir = null, bDir = null, aLabel = null, bLabel = null;

        for (int i = 0; i < args.Length; i++)
        {
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--a" when next is not null: aDir = next; i++; break;
                case "--b" when next is not null: bDir = next; i++; break;
                case "--label-a" when next is not null: aLabel = next; i++; break;
                case "--label-b" when next is not null: bLabel = next; i++; break;
                case "--demos" when next is not null:
                    o = o with { DemoDirectory = next };
                    i++;
                    break;
                case "--rounds" when next is not null && int.TryParse(next, out int r) && r > 0:
                    o = o with { Rounds = r };
                    i++;
                    break;
                case "--cooldown" when next is not null && int.TryParse(next, out int c) && c >= 0:
                    o = o with { CooldownSeconds = c };
                    i++;
                    break;
                case "--out" when next is not null:
                    o = o with { Output = next };
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"unrecognised argument: {args[i]}");
                    return null;
            }
        }

        if (aDir is null || bDir is null)
        {
            Console.Error.WriteLine("compare needs --a <published-dir> and --b <published-dir>");
            return null;
        }

        return o with
        {
            A = (aLabel ?? "a", Resolve(aDir)),
            B = (bLabel ?? "b", Resolve(bDir))
        };
    }

    // Accepts either the published directory or the executable inside it.
    private static string Resolve(string path) =>
        Directory.Exists(path) ? Path.Combine(path, "CS2DemoKit.Bench") : path;
}
