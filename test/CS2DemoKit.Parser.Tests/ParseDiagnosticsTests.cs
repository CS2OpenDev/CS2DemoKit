namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The warning channel is one instance per parse. Its result owns whatever it drained, a
///     snapshot reads the same shape without clearing, and the cap turns into one summary entry.
/// </summary>
[Category("Unit")]
public class ParseDiagnosticsTests
{
    private static ParsedDemo NewDemo(IReadOnlyList<ParseWarning>? warnings = null) => new(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 6400, 1f / 64, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown, warnings: warnings);

    [Test]
    public async Task Drain_HandsTheWarningsToTheResult_AndResets()
    {
        ParseDiagnostics diagnostics = new();
        diagnostics.Warn(ParseWarningCodes.StringTableCreateFailed, "table 'userinfo' failed");
        diagnostics.Warn(ParseWarningCodes.PlayerInfoUnreadable, "slot 3 dropped", 1234);

        ParsedDemo first = NewDemo(diagnostics.Drain());
        await Assert.That(first.Warnings).HasCount().EqualTo(2);
        await Assert.That(first.Warnings[0].Code).IsEqualTo(ParseWarningCodes.StringTableCreateFailed);
        await Assert.That(first.Warnings[1].Tick).IsEqualTo(1234);
        await Assert.That(first.Health).IsEqualTo(ParseHealth.Damaged);

        await Assert.That(diagnostics.Count).IsEqualTo(0);
        ParsedDemo second = NewDemo(diagnostics.Drain());
        await Assert.That(second.Warnings).HasCount().EqualTo(0);
    }

    [Test]
    public async Task Snapshot_ReadsWithoutClearing()
    {
        ParseDiagnostics diagnostics = new();
        diagnostics.Warn(ParseWarningCodes.DemoTruncated, "cut short");

        IReadOnlyList<ParseWarning> live = diagnostics.Snapshot();
        await Assert.That(live).HasCount().EqualTo(1);
        await Assert.That(diagnostics.Count).IsEqualTo(1);

        diagnostics.Warn(ParseWarningCodes.NetMessageDropped, "svc_X dropped", count: 3);
        await Assert.That(live).HasCount().EqualTo(1).Because("a snapshot is a copy");
        await Assert.That(diagnostics.Snapshot()).HasCount().EqualTo(2);
    }

    [Test]
    public async Task PastTheCap_OneSummaryEntryCarriesTheRest()
    {
        ParseDiagnostics diagnostics = new();
        for (int i = 0; i < ParseDiagnostics.MaxWarnings + 7; i++)
        {
            diagnostics.Warn(ParseWarningCodes.StringTableUpdateFailed, $"update {i}");
        }

        IReadOnlyList<ParseWarning> snapshot = diagnostics.Snapshot();
        await Assert.That(snapshot).HasCount().EqualTo(ParseDiagnostics.MaxWarnings + 1);
        await Assert.That(snapshot[^1].Code).IsEqualTo(ParseWarningCodes.WarningsTruncated);
        await Assert.That(snapshot[^1].Message).Contains("7 further");

        IReadOnlyList<ParseWarning> drained = diagnostics.Drain();
        await Assert.That(drained).HasCount().EqualTo(ParseDiagnostics.MaxWarnings + 1);
        await Assert.That(diagnostics.Drain()).HasCount().EqualTo(0);
    }

    [Test]
    public async Task HealthyParse_HasEmptyWarnings()
    {
        ParsedDemo demo = NewDemo();
        await Assert.That(demo.Warnings).IsNotNull();
        await Assert.That(demo.Warnings).HasCount().EqualTo(0);
        await Assert.That(demo.Health).IsEqualTo(ParseHealth.Clean);
    }
}
