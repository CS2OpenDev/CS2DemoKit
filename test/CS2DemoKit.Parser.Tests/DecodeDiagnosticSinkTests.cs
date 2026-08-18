#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Pins that the decode-error breadcrumb (and the trace dump that follows it) is
///     redirectable per tracker instead of hard-wired to <see cref="Console" />, so a batch service
///     can collect or silence it per parse.
///     <para>
///         Driven through <c>ReportFirstDecodeErrorForTest</c> rather than a manufactured corrupt
///         packet. That is deliberate and matches <see cref="DecodeTraceGateTests" />' reasoning:
///         <c>BitBuffer</c> zero-pads past end-of-span instead of throwing, so truncating
///         <c>entity_data</c> does not reliably raise a decode error. The seam runs the real report
///         method, so what is asserted here is the shipping code path, not a copy of it.
///     </para>
///     <para>
///         "Console untouched" is read back from TUnit's own per-test capture
///         (<c>TestContext.GetStandardOutput()</c>) rather than by swapping
///         <see cref="Console.Out" /> — the framework forbids that (TUnit0055), and a global writer
///         swap would eat output from whatever test happened to be running alongside.
///     </para>
/// </summary>
[Category("Unit")]
public class DecodeDiagnosticSinkTests
{
    private const string BreadcrumbMarker = "first decode error";

    /// <summary>
    ///     With the sink redirected, the whole first-error report lands in the collector and
    ///     nothing reaches the console.
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task RedirectedSink_CapturesReport_AndLeavesConsoleUntouched()
    {
        List<string> collected = [];
        EntityTracker tracker = new() { DecodeDiagnosticSink = collected.Add };

        tracker.ReportFirstDecodeErrorForTest(new InvalidOperationException("synthetic decode failure"), 4096);

        await Assert.That(collected).IsNotEmpty();
        await Assert.That(collected[0]).Contains(BreadcrumbMarker);
        await Assert.That(collected[0]).Contains("synthetic decode failure");
        await Assert.That(collected.Any(l => l.Contains("entity_data total bits: 4096", StringComparison.Ordinal)))
            .IsTrue();
        await Assert.That(ConsoleOutput()).DoesNotContain(BreadcrumbMarker);
    }

    /// <summary>
    ///     A silencing sink swallows the report entirely — no collector, no console. This is the
    ///     shape a batch parse uses when it only wants the structured
    ///     <see cref="EntityTracker.DecodeErrorRaised" /> stream.
    /// </summary>
    [Test]
    public async Task SilencedSink_EmitsNothingAnywhere()
    {
        int calls = 0;
        EntityTracker tracker = new() { DecodeDiagnosticSink = _ => calls++ };

        tracker.ReportFirstDecodeErrorForTest(new InvalidOperationException("swallowed"), 8);

        await Assert.That(calls).IsGreaterThan(0).Because("the report still ran — it just went nowhere");
        await Assert.That(ConsoleOutput()).DoesNotContain(BreadcrumbMarker);
        await Assert.That(ConsoleOutput()).DoesNotContain("swallowed");
    }

    /// <summary>
    ///     The default sink is still <see cref="Console.WriteLine(string)" /> — today's behaviour is
    ///     preserved exactly for every caller that never touches the property.
    /// </summary>
    [Test]
    public async Task DefaultSink_StillWritesToConsole()
    {
        EntityTracker tracker = new();

        await Assert.That(tracker.DecodeDiagnosticSink).IsEqualTo((Action<string>)Console.WriteLine);

        tracker.ReportFirstDecodeErrorForTest(new InvalidOperationException("default sink"), 16);

        await Assert.That(ConsoleOutput()).Contains(BreadcrumbMarker);
        await Assert.That(ConsoleOutput()).Contains("default sink");
    }

    /// <summary>
    ///     Redirection covers the trace dump too, not just the three breadcrumb lines — the dump
    ///     is emitted from inside the same report, so a sink that missed it would leak the largest
    ///     part of the output to the console.
    /// </summary>
    [Test]
    public async Task RedirectedSink_AlsoCapturesTraceDump()
    {
        List<string> collected = [];
        EntityTracker tracker = new() { DecodeDiagnosticSink = collected.Add };

        tracker.ReportFirstDecodeErrorForTest(new InvalidOperationException("with trace"), 32, true);

        // No decode has run, so the dump takes its "trace buffer empty" branch — still DumpTrace
        // output, and still must not reach the console.
        await Assert.That(collected.Any(l => l.Contains("trace buffer empty", StringComparison.Ordinal))).IsTrue();
        await Assert.That(ConsoleOutput()).DoesNotContain("trace buffer empty");
    }

    /// <summary>
    ///     Two trackers in the same process keep independent sinks — the property is per instance,
    ///     which is what lets one demo in a batch be silenced without silencing the rest.
    /// </summary>
    [Test]
    public async Task SinkIsPerTrackerInstance()
    {
        List<string> a = [];
        List<string> b = [];
        EntityTracker trackerA = new() { DecodeDiagnosticSink = a.Add };
        EntityTracker trackerB = new() { DecodeDiagnosticSink = b.Add };

        trackerA.ReportFirstDecodeErrorForTest(new InvalidOperationException("only-a"), 1);

        await Assert.That(a).IsNotEmpty();
        await Assert.That(b).IsEmpty();
    }

    /// <summary>This test's captured stdout, as TUnit recorded it.</summary>
    private static string ConsoleOutput() => TestContext.Current?.GetStandardOutput() ?? string.Empty;
}
