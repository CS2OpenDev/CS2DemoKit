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
///     subtlety lives in this comparison — health's 0→null, armor/equipment's
///     unseen→lane-default, the weapon handle hop, the freeze-period singleton — and the
///     parallel-decode clone path exercises the new <see cref="IWorkerCloneable{T}" /> hook
///     (spec-constructed providers have no parameterless ctor for the Activator fallback).
/// </summary>
[Category("Unit")]
[NotInParallel]
public class ProviderDigestParityTests
{
    /// <summary>
    ///     Precomputes the full digest stream twice over the same demo — hand-written registry
    ///     vs generic-spec registry — and compares element-wise: per-frame pawn slots, every
    ///     per-player provider value, every singleton value, and the molotov list.
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

        for (int f = 0; f < handWritten.Length; f++)
        {
            EntityFrameDigest a = handWritten[f]!;
            EntityFrameDigest b = generic[f]!;

            await Assert.That(b.PerPawn.Count).IsEqualTo(a.PerPawn.Count)
                .Because($"frame {f}: live-pawn count must match");
            for (int i = 0; i < a.PerPawn.Count; i++)
            {
                (int slotA, object?[] valuesA) = a.PerPawn[i];
                (int slotB, object?[] valuesB) = b.PerPawn[i];
                await Assert.That(slotB).IsEqualTo(slotA).Because($"frame {f} entry {i}: slot");
                await Assert.That(valuesB.Length).IsEqualTo(valuesA.Length);
                for (int p = 0; p < valuesA.Length; p++)
                {
                    if (!Equals(valuesA[p], valuesB[p]))
                    {
                        Assert.Fail(
                            $"frame {f} slot {slotA} provider[{p}]: hand-written="
                            + $"{valuesA[p] ?? "null"} generic={valuesB[p] ?? "null"}");
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

            await Assert.That(b.Molotovs.Count).IsEqualTo(a.Molotovs.Count);
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
