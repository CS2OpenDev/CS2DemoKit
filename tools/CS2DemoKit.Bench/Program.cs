using CS2DemoKit.Bench;

// Load-pipeline benchmark.
//
//   sweep     run the whole benchmark: one child process per (round, demo), CSV out
//   compare   interleaved A/B between two published builds, both arms into one CSV
//   measure   one measured load, one CSV row on stdout (what the other two spawn)
//
// measure is separate because a fresh process per measurement is the property the numbers rest on.

return args switch
{
    ["measure", ..] => Measure(args),
    ["compare", .. var compareArgs] => StartCompare(compareArgs),
    ["sweep", .. var sweepArgs] => StartSweep(sweepArgs),
    _ when args.Contains("--help") || args.Contains("-h") => Help(),
    _ => StartSweep(args)
};

static int Measure(string[] args)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("usage: CS2DemoKit.Bench measure <demo.dem> <label> <round>");
        return 2;
    }

    Console.WriteLine(Measurement.Run(args[1], args[2], args[3]));
    return 0;
}

static int StartSweep(string[] args)
{
    if (args.Contains("--help") || args.Contains("-h"))
    {
        return Help();
    }

    BenchOptions? options = BenchOptions.Parse(args);
    return options is null ? 2 : Sweep.Run(options);
}

static int StartCompare(string[] args)
{
    if (args.Contains("--help") || args.Contains("-h"))
    {
        return Help();
    }

    CompareOptions? options = CompareOptions.Parse(args, BenchOptions.DefaultDemoDirectory());
    return options is null ? 2 : Compare.Run(options);
}

static int Help()
{
    Console.WriteLine(
        """
        usage: CS2DemoKit.Bench <command> [options]

        sweep (the default)
          --demos <dir>     directory of .dem files (default: <repo>/demos)
          --rounds <n>      passes over the demo set (default: 10)
          --cooldown <sec>  pause before each run, for thermal recovery (default: 12)
          --out <file>      CSV output path (default: baseline.csv)
          --label <string>  variant label per row (default: git short SHA)

        compare
          --a <dir>         published bench build for arm A (dir or executable)
          --b <dir>         published bench build for arm B
          --label-a <s>     row label for arm A (default: a)
          --label-b <s>     row label for arm B (default: b)
          --demos, --rounds, --cooldown, --out as above (rounds default: 5)

          Publish each arm from its own checkout first:
            dotnet publish tools/CS2DemoKit.Bench -c Release -o /tmp/arm-a
          Arm order flips per round so drift hits both arms rather than the second one.

        measure <demo.dem> <label> <round>
          one measured load, one CSV row on stdout

        Rows are written as they complete, so a long run can be read while it runs.
        Progress goes to stderr.
        """);
    return 0;
}
