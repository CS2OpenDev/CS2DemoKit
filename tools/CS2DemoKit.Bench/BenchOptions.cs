using System.Diagnostics;

namespace CS2DemoKit.Bench;

/// <summary>Parsed <c>sweep</c> arguments, with the defaults the committed baselines were taken at.</summary>
internal sealed record BenchOptions
{
    public string DemoDirectory { get; init; } = "";
    public int Rounds { get; init; } = 10;
    public int CooldownSeconds { get; init; } = 12;
    public string Output { get; init; } = "baseline.csv";
    public string Label { get; init; } = "local";

    /// <summary>The repo's own <c>demos/</c> tree, which is where the committed baselines were measured.</summary>
    public static string DefaultDemoDirectory() =>
        Path.Combine(RepoRoot() ?? Directory.GetCurrentDirectory(), "demos");

    /// <summary>Parses <c>sweep</c> options. Returns null and writes to stderr on a bad argument.</summary>
    public static BenchOptions? Parse(string[] args)
    {
        BenchOptions o = new()
        {
            DemoDirectory = DefaultDemoDirectory(),
            Label = GitDescribe() ?? "local"
        };

        for (int i = 0; i < args.Length; i++)
        {
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
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
                case "--label" when next is not null:
                    o = o with { Label = next };
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"unrecognised argument: {args[i]}");
                    return null;
            }
        }

        return o;
    }

    /// <summary>The commit the measurement was taken at, so a CSV is traceable to a tree.</summary>
    private static string? GitDescribe()
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (p is null)
            {
                return null;
            }

            string sha = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && sha.Length > 0 ? sha : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "CS2DemoKit.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
