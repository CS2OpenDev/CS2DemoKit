#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The data-driven provider migration gate: the five shipped
///     hand-written entity providers re-expressed as <see cref="ProviderSpec" /> data must
///     produce IDENTICAL entity digests on a real demo. Both sides run through the same
///     delta-encoded producer with the same chunking, so a row-for-row comparison still holds:
///     any divergence is the providers disagreeing, not the encoding. Every provider's emit-gate
///     subtlety lives in this comparison, health's 0 to null, armor/equipment's
///     unseen to lane default, the weapon handle hop, the freeze-period singleton, and the
///     parallel-decode clone path exercises the new <see cref="IWorkerCloneable{T}" /> hook
///     (spec-constructed providers have no parameterless ctor for the Activator fallback).
///     <para>
///         Rows are typed columns now, so "identical" is: same slot per row, same present mask,
///         and the same value in every present cell, compared boxed in the column's declared type.
///     </para>
/// </summary>
[Category("Unit")]
[NotInParallel]
public class ProviderDigestParityTests
{
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

        EntityFrameDigest?[] handWritten = Precompute(
            parsed,
            PerPlayerEntityValueProviderRegistry.CreateDefault().All.ToList(),
            new FreezePeriodProvider());

        EntityFrameDigest?[] generic = Precompute(
            parsed,
            BuiltinProviderSpecs.CreateGenericPerPlayerProviders(),
            BuiltinProviderSpecs.CreateGenericFreezePeriodProvider());

        await Assert.That(generic.Length).IsEqualTo(handWritten.Length);

        long cellsCompared = 0;
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
                await Assert.That(rowsB.Layout.IsCompatibleWith(rowsA.Layout)).IsTrue()
                    .Because($"frame {f}: the two registries must lay their columns out identically");
            }

            for (int i = 0; i < rowsA.Count; i++)
            {
                int slotA = rowsA.SlotAt(i);
                await Assert.That(rowsB.SlotAt(i)).IsEqualTo(slotA).Because($"frame {f} entry {i}: slot");
                for (int p = 0; p < rowsA.Layout.Count; p++)
                {
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

                    cellsCompared++;
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

        Console.WriteLine($"compared {cellsCompared:N0} present cells across {handWritten.Length:N0} frames");
        await Assert.That(cellsCompared).IsGreaterThan(0)
            .Because("a stream with no present cells would make this comparison pass while checking nothing");
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
