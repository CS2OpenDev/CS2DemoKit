#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Every shipped per-player provider's typed cell reader against its own boxed read, on every
///     live pawn of every frame of the sample demo, over both registries. The typed path is the
///     boxed contract restated without the box, and this is the proof: a reader that returned
///     "no value" where the boxed read has one, or a different value, fails here per provider,
///     with the first divergence named.
///     <para>
///         Asserted per column with a floor of 50,000 comparisons, so a reader that silently
///         returns nothing cannot pass by comparing nothing; the sample's four rounds hold about
///         175,000 live pawn-frames, so every column clears it. The walk also counts the
///         pawn-frames on which the boxed read produced a value, because two nulls agreeing
///         proves only that the reader agrees on "no value": every shipped column must have
///         carried a value on the sample, and the count is printed per column so a dead column
///         is visible rather than tallied as 175,000 passing comparisons. NaN and negative zero
///         do not occur in the sample and are pinned in <c>PerPawnDeltaStateTests</c> instead.
///     </para>
///     <para>
///         The third test walks the same pawn-frames over specs no shipped provider declares:
///         <c>PositiveOnly</c> on each of the four value types, <c>UnseenAsDefault</c> on the
///         three the registries leave ungated, and both gates behind a handle hop. Those are the
///         arms of the boxed <c>Gate</c> the typed readers transcribe and the registries never
///         reach; each column states the population it expects (values only, nulls only, or
///         both) so an arm that never took its interesting branch on this demo cannot pass
///         quietly.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class PawnCellReaderParityTests
{
    private const int MinimumComparisonsPerColumn = 50_000;
    private const int SampleFrameCount = 19238;

    [Test]
    public async Task TypedReaders_MatchBoxedReads_DefaultRegistry()
    {
        await RunParity(PerPlayerEntityValueProviderRegistry.CreateDefault().All.ToList(), Population.Values);
    }

    [Test]
    public async Task TypedReaders_MatchBoxedReads_GenericRegistry()
    {
        await RunParity(BuiltinProviderSpecs.CreateGenericPerPlayerProviders(), Population.Values);
    }

    [Test]
    public async Task TypedReaders_MatchBoxedReads_GateArmsNoShippedSpecUses()
    {
        (IPerPlayerEntityValueProvider Provider, Population Expect)[] arms = GateArms();
        await RunParity(arms.Select(a => a.Provider).ToList(), null, arms.Select(a => a.Expect).ToArray());
    }

    /// <summary>What a column's boxed reads must have produced over the walk for the arm to count as exercised.</summary>
    private enum Population
    {
        /// <summary>At least one pawn-frame with a value.</summary>
        Values,

        /// <summary>At least one pawn-frame with a value AND at least one with "no value".</summary>
        Mixed,

        /// <summary>No pawn-frame with a value: the gate rejects this value type outright.</summary>
        AllNull,

        /// <summary>A value on every pawn-frame: the gate defaults every unseen read.</summary>
        AllValues
    }

    // The gate arms, built by re-flagging shipped specs so the paths are real leaves on this
    // demo. The hop arms ride the active-weapon handle, which points at nothing for a pawn
    // between dropping its weapons and the next spawn, so the hop fails on some pawn-frames and
    // succeeds on the rest; a gate that treats "hop failed" as "unseen" diverges there.
    private static (IPerPlayerEntityValueProvider Provider, Population Expect)[] GateArms() =>
    [
        (Arm(BuiltinProviderSpecs.PawnShotsFired with { Name = "gate.int.positive_only", PositiveOnly = true }), Population.Mixed),
        (Arm(BuiltinProviderSpecs.PawnDuckAmount with { Name = "gate.float.positive_only", PositiveOnly = true }), Population.AllNull),
        (Arm(BuiltinProviderSpecs.PawnIsScoped with { Name = "gate.bool.positive_only", PositiveOnly = true }), Population.AllNull),
        (Arm(BuiltinProviderSpecs.PawnPlace with { Name = "gate.string.positive_only", PositiveOnly = true }), Population.AllNull),
        (Arm(BuiltinProviderSpecs.PawnFlashDuration with { Name = "gate.float.unseen_default", UnseenAsDefault = true }), Population.AllValues),
        (Arm(BuiltinProviderSpecs.PawnIsScoped with { Name = "gate.bool.unseen_default", UnseenAsDefault = true }), Population.AllValues),
        (Arm(BuiltinProviderSpecs.PawnPlace with { Name = "gate.string.unseen_default", UnseenAsDefault = true }), Population.Values),
        (Arm(BuiltinProviderSpecs.PawnActiveWeaponClip with { Name = "gate.hop.int.unseen_default", UnseenAsDefault = true }), Population.Mixed),
        (Arm(BuiltinProviderSpecs.WeaponRecoilIndex with { Name = "gate.hop.float.unseen_default", UnseenAsDefault = true }), Population.Mixed),
        (Arm(BuiltinProviderSpecs.PawnActiveWeaponClip with { Name = "gate.hop.int.positive_only", PositiveOnly = true }), Population.Mixed),
        (Arm(BuiltinProviderSpecs.WeaponRecoilIndex with { Name = "gate.hop.float.positive_only", PositiveOnly = true }), Population.AllNull)
    ];

    private static GenericPerPlayerFieldProvider Arm(ProviderSpec spec) => new(spec);

    private static async Task RunParity(
        List<IPerPlayerEntityValueProvider> providers, Population? expectAll, Population[]? expectPerColumn = null)
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        await Assert.That(demo.Frames.Count).IsEqualTo(SampleFrameCount)
            .Because("the comparison floor is sized for this trim of the sample demo");

        DigestColumnLayout layout = DigestColumnLayout.For(providers);
        for (int p = 0; p < layout.Count; p++)
        {
            bool typed = layout.Kinds[p] switch
            {
                PawnCellKind.Int or PawnCellKind.Bool => layout.IntReaders[p] is not null,
                PawnCellKind.Float => layout.FloatReaders[p] is not null,
                _ => layout.StringReaders[p] is not null
            };
            await Assert.That(typed).IsTrue()
                .Because($"provider '{layout.Names[p]}' must carry a typed cell reader");
        }

        PawnReadContext context = new();
        long[] compared = new long[layout.Count];
        long[] withValue = new long[layout.Count];
        string?[] firstMismatch = new string?[layout.Count];
        EntityStateLayer entityLayer = new(demo.Frames);
        Walk walk = new(entityLayer, layout, context, compared, withValue, firstMismatch);

        for (int n = 0; n < demo.Frames.Count; n++)
        {
            entityLayer.SeekToTick(demo.Frames[n].ServerTick);
            walk.Frame = n;
            PawnLookup.ForEachLivePawn(entityLayer.Tracker, walk, static (w, slot, pawn) => w.Compare(slot, pawn));
        }

        for (int p = 0; p < layout.Count; p++)
        {
            Console.WriteLine(
                $"{layout.Names[p]}: {compared[p]:N0} comparisons, {withValue[p]:N0} with a boxed value, "
                + $"first mismatch: {firstMismatch[p] ?? "none"}");
        }

        for (int p = 0; p < layout.Count; p++)
        {
            await Assert.That(firstMismatch[p]).IsNull()
                .Because($"'{layout.Names[p]}' typed read must agree with its boxed read");
            await Assert.That(compared[p]).IsGreaterThanOrEqualTo(MinimumComparisonsPerColumn)
                .Because($"'{layout.Names[p]}' must actually be compared on live pawns");

            Population expect = expectPerColumn is not null ? expectPerColumn[p] : expectAll!.Value;
            long nulls = compared[p] - withValue[p];
            switch (expect)
            {
                case Population.Values:
                    await Assert.That(withValue[p]).IsGreaterThan(0)
                        .Because($"'{layout.Names[p]}' must have carried a value on the sample, or the comparison only agreed on nulls");
                    break;
                case Population.Mixed:
                    await Assert.That(withValue[p]).IsGreaterThan(0)
                        .Because($"'{layout.Names[p]}' must have carried a value on the sample");
                    await Assert.That(nulls).IsGreaterThan(0)
                        .Because($"'{layout.Names[p]}' must have read as no value on some pawn-frame, or its gate's rejecting branch never ran");
                    break;
                case Population.AllNull:
                    await Assert.That(withValue[p]).IsEqualTo(0)
                        .Because($"'{layout.Names[p]}' is gated to no value on every read of this value type");
                    break;
                default:
                    await Assert.That(nulls).IsEqualTo(0)
                        .Because($"'{layout.Names[p]}' defaults every unseen read, so no pawn-frame reads as no value");
                    break;
            }
        }
    }

    private sealed class Walk(
        EntityStateLayer layer,
        DigestColumnLayout layout,
        PawnReadContext context,
        long[] compared,
        long[] withValue,
        string?[] firstMismatch)
    {
        public int Frame;

        public void Compare(int slot, EntityState pawn)
        {
            EntityTracker tracker = layer.Tracker;
            context.Bind(tracker, pawn);
            CSPlayerPawn wrapper = SdkEntityWorlds.Wrap<CSPlayerPawn>(tracker, pawn)!;
            for (int p = 0; p < layout.Count; p++)
            {
                IPerPlayerEntityValueProvider provider = layout.Providers[p];
                object? boxed = provider is IPawnStateReader stateReader
                    ? stateReader.ReadForPawnState(tracker, pawn)
                    : provider.ReadForPawn(tracker, wrapper);

                object? typed;
                switch (layout.Kinds[p])
                {
                    case PawnCellKind.Int:
                        typed = layout.IntReaders[p]!.TryReadInt(context, out int i) ? i : null;
                        break;
                    case PawnCellKind.Bool:
                        typed = layout.IntReaders[p]!.TryReadInt(context, out int b) ? b != 0 : null;
                        break;
                    case PawnCellKind.Float:
                        typed = layout.FloatReaders[p]!.TryReadFloat(context, out float f) ? f : null;
                        break;
                    default:
                        typed = layout.StringReaders[p]!.TryReadString(context, out string? s) ? s : null;
                        break;
                }

                compared[p]++;
                if (boxed is not null)
                {
                    withValue[p]++;
                }

                if (!Equals(boxed, typed))
                {
                    firstMismatch[p] ??= $"frame {Frame} slot {slot}: boxed={boxed ?? "null"} typed={typed ?? "null"}";
                }
            }
        }
    }
}
