namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The S11 parse-diagnostics channel (v0.6.0): warnings accumulate per parse thread and drain
///     into <see cref="ParsedDemo.Warnings" /> at construction (which doubles as the reset). Unit-
///     level (no demo file, synthetic <see cref="ParsedDemo" />) — the App-tier consumer of this
///     channel (the Match Overview damaged-demo banner) is covered separately in
///     <c>DemoViewer.NET.App.Tests</c>.
/// </summary>
[Category("Unit")]
public class ParseDiagnosticsTests
{
    private static ParsedDemo NewDemo() => new(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 6400, 1f / 64, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown);

    /// <summary>Warnings recorded before construction land on the result — and ONLY that result.</summary>
    [Test]
    public async Task Warnings_DrainIntoTheConstructedDemo_AndReset()
    {
        // Self-isolating: Warn's backing store is [ThreadStatic] and only Drain() (inside the
        // ParsedDemo ctor) clears it. A sibling test that calls Warn without ever constructing a
        // ParsedDemo (e.g. StringTableBoundsTests' hostile-table swallow path) can leave residue
        // on a pool thread this test later reuses — drain it first so the count below is ours alone.
        ParseDiagnostics.Drain();

        ParseDiagnostics.Warn(ParseWarningCodes.StringTableCreateFailed, "table 'userinfo' failed");
        ParseDiagnostics.Warn(ParseWarningCodes.PlayerInfoUnreadable, "slot 3 dropped", 1234);

        ParsedDemo first = NewDemo();
        await Assert.That(first.Warnings).HasCount().EqualTo(2);
        await Assert.That(first.Warnings[0].Code).IsEqualTo(ParseWarningCodes.StringTableCreateFailed);
        await Assert.That(first.Warnings[1].Tick).IsEqualTo(1234);

        // Drain-on-construct IS the reset: the next parse on this thread starts clean.
        ParsedDemo second = NewDemo();
        await Assert.That(second.Warnings).HasCount().EqualTo(0);
    }

    /// <summary>A healthy parse carries an empty (never null) warning list.</summary>
    [Test]
    public async Task HealthyParse_HasEmptyWarnings()
    {
        // See the isolating comment in Warnings_DrainIntoTheConstructedDemo_AndReset above — a
        // stale, un-drained warning left on this pool thread by a sibling test would otherwise
        // make "empty" flaky rather than deterministic.
        ParseDiagnostics.Drain();

        ParsedDemo demo = NewDemo();
        await Assert.That(demo.Warnings).IsNotNull();
        await Assert.That(demo.Warnings).HasCount().EqualTo(0);
    }
}
