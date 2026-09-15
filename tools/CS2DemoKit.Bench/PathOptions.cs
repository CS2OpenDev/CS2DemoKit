namespace CS2DemoKit.Bench;

/// <summary>Parsed <c>paths</c> arguments.</summary>
internal sealed record PathOptions
{
    public string DemoDirectory { get; init; } = "";
    public int Rounds { get; init; } = 3;
    public int CooldownSeconds { get; init; } = 5;
    public string Output { get; init; } = "paths.csv";
    public string Label { get; init; } = "local";
    public IReadOnlyList<string> Arms { get; init; } = PathMeasurement.AllArms;

    public static PathOptions? Parse(string[] args)
    {
        PathOptions o = new()
        {
            DemoDirectory = BenchOptions.DefaultDemoDirectory(),
            Label = BenchOptions.GitDescribe() ?? "local"
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
                case "--arms" when next is not null:
                    string[] arms = next.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    string? unknown = arms.FirstOrDefault(a => !PathMeasurement.AllArms.Contains(a));
                    if (unknown is not null)
                    {
                        Console.Error.WriteLine($"unknown arm: {unknown} (arms: {string.Join(", ", PathMeasurement.AllArms)})");
                        return null;
                    }

                    o = o with { Arms = arms };
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"unrecognised argument: {args[i]}");
                    return null;
            }
        }

        return o;
    }
}
