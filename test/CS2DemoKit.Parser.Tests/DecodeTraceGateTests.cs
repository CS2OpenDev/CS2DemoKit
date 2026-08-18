#region

using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Pins the runtime opt-in gate on the entity-decode trace. The trace's
///     per-op <c>DecodeTraceEntry</c> construction + buffer append are now armed only when
///     <see cref="Tracing.Enabled" /> (env <c>DEMOVIEWER_TRACE_DECODE</c>) is set; a default run
///     pays one predicted branch per op and builds nothing.
///     <para>
///         <b>Why a gate-COUNT test, not a forced-error test.</b> The gate's defining property is
///         "trace entries are built iff the flag is on" — that is deterministic on a healthy demo
///         and needs no decode error: the buffer is cleared at each packet's start (when armed), so
///         after a full <c>Replay</c> it holds exactly the last <c>PacketEntities</c> packet's
///         entries. Flag on ⇒ non-empty; flag off ⇒ empty. A forced-error path (truncated
///         <c>EntityData</c>) was rejected: <c>BitBuffer</c> zero-pads past end-of-span rather than
///         throwing (BitBuffer.cs ReadUBits overrun), so truncation does not reliably raise the
///         decode error the breadcrumb/dump would need. The always-on breadcrumb is a cold-path,
///         read-only-of-existing-state addition; the golden byte-identical suite proves the
///         healthy (flag-off) path is unchanged.
///     </para>
///     <para>
///         <c>[NotInParallel]</c> because <see cref="Tracing.Enabled" /> is a process-global flag;
///         each test restores it to <c>false</c> in a <c>finally</c> so it cannot leak ON into the
///         rest of the suite (a leaked flag would re-introduce the per-op trace cost into every
///         later decode test).
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class DecodeTraceGateTests
{
    /// <summary>
    ///     Flag OFF (default): replaying a healthy demo builds NO trace entries — the gate elides
    ///     the per-op construction + append entirely.
    /// </summary>
    [Test]
    public async Task DecodeTrace_Disabled_BuildsNoTraceEntries()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] demoBytes = await File.ReadAllBytesAsync(path);
        ParsedDemo parsed = DemoParser.Parse(demoBytes.AsMemory());

        bool prior = Tracing.Enabled;
        try
        {
            Tracing.Enabled = false;
            EntityTracker tracker = new();
            tracker.Replay(parsed.Frames);

            // Decode stayed healthy (no error path) AND the trace buffer is empty: every gated
            // construction site was skipped on the default path.
            await Assert.That(tracker.LastEntityError).IsNull();
            await Assert.That(tracker.TraceEntryCountForTest).IsEqualTo(0);
        }
        finally
        {
            Tracing.Enabled = prior;
        }
    }

    /// <summary>
    ///     Flag ON: the same healthy replay builds a faithful trace — after the full replay the
    ///     buffer holds the last PacketEntities packet's entries (cleared-per-packet semantics).
    /// </summary>
    [Test]
    public async Task DecodeTrace_Enabled_BuildsFaithfulTrace()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] demoBytes = await File.ReadAllBytesAsync(path);
        ParsedDemo parsed = DemoParser.Parse(demoBytes.AsMemory());

        bool prior = Tracing.Enabled;
        try
        {
            Tracing.Enabled = true;
            EntityTracker tracker = new();
            tracker.Replay(parsed.Frames);

            // Enabling the flag runs the current, faithful in-situ decode with tracing on — the
            // buffer is non-empty (it captured the last packet's path ops + field reads).
            await Assert.That(tracker.LastEntityError).IsNull();
            await Assert.That(tracker.TraceEntryCountForTest).IsGreaterThan(0);
        }
        finally
        {
            Tracing.Enabled = prior;
        }
    }

    /// <summary>
    ///     Both directions in one assertion: enabling the flag is what makes the trace populate.
    ///     Same parsed demo, two fresh trackers — the only difference is the flag.
    /// </summary>
    [Test]
    public async Task DecodeTrace_FlagControlsTracePopulation()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] demoBytes = await File.ReadAllBytesAsync(path);
        ParsedDemo parsed = DemoParser.Parse(demoBytes.AsMemory());

        bool prior = Tracing.Enabled;
        try
        {
            Tracing.Enabled = false;
            EntityTracker offTracker = new();
            offTracker.Replay(parsed.Frames);
            int offCount = offTracker.TraceEntryCountForTest;

            Tracing.Enabled = true;
            EntityTracker onTracker = new();
            onTracker.Replay(parsed.Frames);
            int onCount = onTracker.TraceEntryCountForTest;

            Console.WriteLine($"trace entries — off: {offCount}  on: {onCount}");
            await Assert.That(offCount).IsEqualTo(0);
            await Assert.That(onCount).IsGreaterThan(0);
            await Assert.That(offTracker.LastEntityError).IsNull();
            await Assert.That(onTracker.LastEntityError).IsNull();
        }
        finally
        {
            Tracing.Enabled = prior;
        }
    }
}
