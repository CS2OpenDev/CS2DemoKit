#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The data-driven provider migration gate: the shipped hand-written entity providers
///     re-expressed as <see cref="ProviderSpec" /> data must produce IDENTICAL entity digests on a
///     real demo. Both sides run through the same delta-encoded producer with the same chunking, so
///     a row-for-row comparison still holds: any divergence is the providers disagreeing, not the
///     encoding. The parallel-decode clone path also exercises the
///     <see cref="IWorkerCloneable{T}" /> hook (spec-constructed providers have no parameterless
///     ctor for the Activator fallback).
///     <para>
///         <b>What this comparison actually judges, column by column.</b> Only the columns in
///         <see cref="_independentlyImplemented" /> plus the freeze-period singleton have two
///         independent implementations — a hand-written class on one side, a
///         <see cref="GenericPerPlayerFieldProvider" /> built from a <see cref="ProviderSpec" /> on
///         the other — and those are the columns where equality is evidence. Every provider added
///         since has no hand-written twin: either the same spec-constructed provider is registered
///         on both sides (the Tier C and aim-rating reads), or the same hand-written class is
///         (the angle and position columns, which have no spec form). Those hold by CONSTRUCTION,
///         not by comparison, and the assertions below are sized so that admitting it does not
///         quietly turn the whole test into a tautology.
///     </para>
///     <para>
///         <b>The vacuity floors are per column, not global.</b> The digest is delta-encoded, so a
///         column that reads the same value as last frame has no present bit and its two sides are
///         never compared at all — which means a global "some cells were compared" count is
///         satisfied entirely by the columns that hold by construction. A schema rename that
///         darkened <c>m_iHealth</c> on BOTH sides would leave the two present masks agreeing on
///         "absent" for every row of the one column where equality was evidence, and the run would
///         still report hundreds of thousands of tautological cells. Each column therefore has to
///         have been PRESENT on some row of its own, and <see cref="_independentlyImplemented" />
///         is asserted to still be in the layout so the gate cannot be hollowed out by retiring the
///         hand-written side instead.
///     </para>
///     <para>
///         Rows are typed columns now, so "identical" is: same slot per row, same present mask,
///         and the same value in every present cell, compared boxed in the column's declared type.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ProviderDigestParityTests
{
    /// <summary>
    ///     Per column, the minimum number of pawn-rows on which the two present masks were
    ///     compared. A row exists only for a pawn something changed on, so this counts emitted
    ///     rows, not live pawn-frames: the four-round sample emits about 69,700 of them and a
    ///     fuller match more. The floor sits well under that, so it fails on a walk that collapsed
    ///     rather than on a demo that is merely shorter.
    /// </summary>
    private const int MinimumRowsPerColumn = 10_000;

    /// <summary>
    ///     The columns whose two sides are genuinely separate code: a hand-written provider class
    ///     against a <see cref="GenericPerPlayerFieldProvider" /> over the equivalent
    ///     <see cref="ProviderSpec" />. Every emit-gate subtlety the migration had to preserve
    ///     lives in these four — health's 0-to-null, armor's and equipment's unseen-to-lane-default,
    ///     and the weapon handle hop — so if one of them stops being compared, the gate has stopped
    ///     gating whatever the other columns still tally.
    /// </summary>
    private static readonly string[] _independentlyImplemented =
    [
        "entity.pawn.active_weapon_class",
        "entity.pawn.armor",
        "entity.pawn.equipment_value",
        "entity.pawn.health"
    ];

    /// <summary>
    ///     Precomputes the full digest stream twice over the same demo, hand-written registry
    ///     vs generic-spec registry, and compares element-wise: per-frame pawn slots, every
    ///     per-player provider cell, every singleton value, and the molotov list.
    /// </summary>
    [Test]
    public async Task GenericProviders_ProduceByteIdenticalDigests()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);

        List<IPerPlayerEntityValueProvider> handWrittenProviders =
            PerPlayerEntityValueProviderRegistry.CreateDefault().All.ToList();
        EntityFrameDigest?[] handWritten = Precompute(
            parsed,
            handWrittenProviders,
            new FreezePeriodProvider());

        EntityFrameDigest?[] generic = Precompute(
            parsed,
            BuiltinProviderSpecs.CreateGenericPerPlayerProviders(),
            BuiltinProviderSpecs.CreateGenericFreezePeriodProvider());

        await Assert.That(generic.Length).IsEqualTo(handWritten.Length);

        DigestColumnLayout expected = DigestColumnLayout.For(handWrittenProviders);
        foreach (string name in _independentlyImplemented)
        {
            await Assert.That(expected.Names.Contains(name, StringComparer.Ordinal)).IsTrue()
                .Because($"'{name}' is one of the columns with two independent implementations; "
                         + "without it in the layout every remaining column holds by construction");
        }

        // Per column, not global: the rows on which the two present masks were compared, and the
        // subset of those where the column was present and its two values were compared.
        long[] rowsPerColumn = new long[expected.Count];
        long[] changedCells = new long[expected.Count];

        for (int f = 0; f < handWritten.Length; f++)
        {
            EntityFrameDigest a = handWritten[f]!;
            EntityFrameDigest b = generic[f]!;
            PerPawnColumns rowsA = a.PerPawn;
            PerPawnColumns rowsB = b.PerPawn;

            await Assert.That(rowsB.Count).IsEqualTo(rowsA.Count)
                .Because($"frame {f}: live-pawn count must match");
            if (rowsA.Count > 0)
            {
                await Assert.That(rowsA.Layout.IsCompatibleWith(expected)).IsTrue()
                    .Because($"frame {f}: the hand-written stream must lay its columns out as declared");
                await Assert.That(rowsB.Layout.IsCompatibleWith(rowsA.Layout)).IsTrue()
                    .Because($"frame {f}: the two registries must lay their columns out identically");
            }

            for (int i = 0; i < rowsA.Count; i++)
            {
                int slotA = rowsA.SlotAt(i);
                await Assert.That(rowsB.SlotAt(i)).IsEqualTo(slotA).Because($"frame {f} entry {i}: slot");
                for (int p = 0; p < rowsA.Layout.Count; p++)
                {
                    rowsPerColumn[p]++;
                    bool presentA = rowsA.IsPresent(i, p);
                    if (presentA != rowsB.IsPresent(i, p))
                    {
                        Assert.Fail(
                            $"frame {f} slot {slotA} provider[{p}] ('{rowsA.Layout.Names[p]}'): hand-written "
                            + $"{(presentA ? "present" : "absent")} generic {(presentA ? "absent" : "present")}");
                    }

                    if (!presentA)
                    {
                        continue;
                    }

                    changedCells[p]++;
                    object? valueA = rowsA.GetBoxed(i, p);
                    object? valueB = rowsB.GetBoxed(i, p);
                    if (!Equals(valueA, valueB))
                    {
                        Assert.Fail(
                            $"frame {f} slot {slotA} provider[{p}]: hand-written="
                            + $"{valueA ?? "null"} generic={valueB ?? "null"}");
                    }
                }
            }

            await Assert.That(b.Singletons.Length).IsEqualTo(a.Singletons.Length);
            for (int sIdx = 0; sIdx < a.Singletons.Length; sIdx++)
            {
                if (!Equals(a.Singletons[sIdx], b.Singletons[sIdx]))
                {
                    Assert.Fail(
                        $"frame {f} singleton[{sIdx}]: hand-written={a.Singletons[sIdx] ?? "null"} "
                        + $"generic={b.Singletons[sIdx] ?? "null"}");
                }
            }

            await Assert.That(b.Molotovs.Length).IsEqualTo(a.Molotovs.Length);
        }

        for (int p = 0; p < expected.Count; p++)
        {
            Console.WriteLine(
                $"{expected.Names[p]}: {rowsPerColumn[p]:N0} rows compared, {changedCells[p]:N0} present");
        }

        for (int p = 0; p < expected.Count; p++)
        {
            await Assert.That(rowsPerColumn[p]).IsGreaterThanOrEqualTo(MinimumRowsPerColumn)
                .Because($"'{expected.Names[p]}' must have had its present mask compared on real pawn-rows");
            await Assert.That(changedCells[p]).IsGreaterThan(0)
                .Because($"'{expected.Names[p]}' was absent on every row of both streams, so the two agreed "
                         + "about nothing: a column dark on both sides passes a comparison and proves none");
        }
    }

    private static EntityFrameDigest?[] Precompute(
        ParsedDemo parsed,
        List<IPerPlayerEntityValueProvider> perPlayer,
        IEntityValueProvider singleton)
    {
        EntityChangeScanner scanner = new(
            new EntityStateLayer(parsed.Frames),
            [(singleton, new GenericBoolNode(singleton.ContextName))],
            perPlayer,
            true);
        scanner.PrecomputeParallelDigests(parsed.Frames);
        return scanner.PrecomputedDigests
               ?? throw new InvalidOperationException("digest precompute returned null");
    }
}
