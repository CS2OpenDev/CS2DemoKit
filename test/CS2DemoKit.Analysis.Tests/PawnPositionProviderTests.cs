#region

using System.Globalization;
using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The correctness gate for <see cref="PawnPositionProvider" />.
///     <para>
///         Position is reconstructed as <c>(cell - 32) * 512 + offset</c>. The multiplier is the
///         part that goes wrong: <see cref="PositionUtil" />'s doc records 1024 as the classic
///         mis-derivation, and a wrong constant moves every coordinate the engine reports without
///         failing anything else. So the gate below recomputes the formula from the raw leaves and
///         judges the provider against that, rather than against <see cref="PositionUtil" />,
///         which would let the two agree with each other while both drifted.
///     </para>
/// </summary>
[Category("Unit")]
[NotInParallel]
public class PawnPositionProviderTests
{
    private static readonly string[] AxisNames =
        ["entity.pawn.pos_x", "entity.pawn.pos_y", "entity.pawn.pos_z"];

    /// <summary>Metadata: three axes, distinct names, distinct offset leaves, float throughout.</summary>
    [Test]
    public async Task PositionProviders_ExposeExpectedMetadata()
    {
        PawnPositionProvider x = new(PawnPositionAxis.X);
        PawnPositionProvider y = new(PawnPositionAxis.Y);
        PawnPositionProvider z = new(PawnPositionAxis.Z);

        await Assert.That(x.Name).IsEqualTo("entity.pawn.pos_x");
        await Assert.That(y.Name).IsEqualTo("entity.pawn.pos_y");
        await Assert.That(z.Name).IsEqualTo("entity.pawn.pos_z");

        await Assert.That(x.FieldName).IsEqualTo("CBodyComponent.m_vecX");
        await Assert.That(y.FieldName).IsEqualTo("CBodyComponent.m_vecY");
        await Assert.That(z.FieldName).IsEqualTo("CBodyComponent.m_vecZ");

        foreach (PawnPositionProvider p in new[] { x, y, z })
        {
            await Assert.That(p.EntityClass).IsEqualTo("CCSPlayerPawn");
            await Assert.That(p.ValueType).IsEqualTo(typeof(float));
            // Constructor state means the scanner's Activator fallback cannot clone it.
            await Assert.That(p.CloneForWorker()).IsTypeOf<PawnPositionProvider>();
            await Assert.That(((PawnPositionProvider)p.CloneForWorker()).Axis).IsEqualTo(p.Axis);
        }
    }

    /// <summary>
    ///     Pins the whole read chain against the literal oracle formula, recomputed here from the
    ///     raw <c>CBodyComponent</c> leaves: <c>world = (cell - 32) * 512 + offset</c>. Independent
    ///     of <see cref="PositionUtil" />'s own implementation, so a drift in either the util or the
    ///     provider's axis selection fails rather than agreeing with itself.
    ///     <para>
    ///         Note what is NOT used here. <c>CSPlayerPawn.Origin</c> is the SDK's own
    ///         reconstruction and would be the obvious read, but it resolves through the Lens lane
    ///         only and returns null on this decode path for pawns whose leaves the state indexer
    ///         reads fine. That divergence is the entire reason
    ///         <see cref="IPawnStateReader" /> exists, and it is asserted below so the provider
    ///         cannot be quietly "simplified" back onto the wrapper.
    ///     </para>
    /// </summary>
    [Test]
    public async Task PawnPositionMatchesTheOracleFormula()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        EntityStateLayer layer = new(frames);
        PawnPositionProvider px = new(PawnPositionAxis.X);
        PawnPositionProvider py = new(PawnPositionAxis.Y);
        PawnPositionProvider pz = new(PawnPositionAxis.Z);

        int compared = 0, sdkNull = 0, sdkResolved = 0;
        string? firstMismatch = null;

        // Every 250th frame: the walk is O(entities) per frame, and pinning a constant needs a
        // spread of distinct positions rather than every tick.
        for (int n = 0; n < frames.Count && firstMismatch is null; n += 250)
        {
            layer.SeekToTick(frames[n].ServerTick);
            EntityTracker tracker = layer.Tracker;
            int frameIndex = n;

            PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
            {
                if (firstMismatch is not null)
                {
                    return;
                }

                if (SdkEntityWorlds.Wrap<CSPlayerPawn>(tracker, pawn)!.Origin is null)
                {
                    sdkNull++;
                }
                else
                {
                    sdkResolved++;
                }

                if (pawn["CBodyComponent.m_cellX"] is not { } rawCellX ||
                    pawn["CBodyComponent.m_vecX"] is not { } rawVecX ||
                    pawn["CBodyComponent.m_cellY"] is not { } rawCellY ||
                    pawn["CBodyComponent.m_vecY"] is not { } rawVecY ||
                    pawn["CBodyComponent.m_cellZ"] is not { } rawCellZ ||
                    pawn["CBodyComponent.m_vecZ"] is not { } rawVecZ)
                {
                    // Not all six networked yet. The provider must agree it has nothing.
                    if (px.ReadForPawnState(tracker, pawn) is not null)
                    {
                        firstMismatch = $"frame {frameIndex} slot {slot}: provider emitted with leaves missing";
                    }

                    return;
                }

                compared++;
                float ex = Oracle(rawCellX, rawVecX);
                float ey = Oracle(rawCellY, rawVecY);
                float ez = Oracle(rawCellZ, rawVecZ);

                object? gx = px.ReadForPawnState(tracker, pawn);
                object? gy = py.ReadForPawnState(tracker, pawn);
                object? gz = pz.ReadForPawnState(tracker, pawn);

                if (!Equals(gx, ex) || !Equals(gy, ey) || !Equals(gz, ez))
                {
                    firstMismatch =
                        $"frame {frameIndex} slot {slot}: provider ({gx},{gy},{gz}) != oracle ({ex},{ey},{ez})";
                    return;
                }

                // The util and the provider must not disagree either, since the util is what the
                // rest of the engine (visibility) reads.
                if (PositionUtil.CellToWorld(pawn) is not { } util ||
                    util != new Vector3(ex, ey, ez))
                {
                    firstMismatch = $"frame {frameIndex} slot {slot}: PositionUtil != oracle ({ex},{ey},{ez})";
                }
            });
        }

        Console.WriteLine($"compared {compared:N0} pawn positions against the oracle formula");
        Console.WriteLine($"CSPlayerPawn.Origin resolved={sdkResolved:N0} null={sdkNull:N0}");
        if (firstMismatch is not null)
        {
            Console.WriteLine("first mismatch: " + firstMismatch);
        }

        await Assert.That(firstMismatch).IsNull();
        // Guards against a vacuous pass: an all-null run would compare nothing and still be green.
        await Assert.That(compared).IsGreaterThan(0)
            .Because("the pinning is worthless if no pawn resolved a position");
        // The reason IPawnStateReader exists. If this ever fails because the SDK started resolving,
        // the provider can move onto the wrapper and this assertion should be deleted, deliberately.
        await Assert.That(sdkNull).IsGreaterThan(0)
            .Because("CSPlayerPawn.Origin reads the Lens lane only, which is why the provider "
                     + "reads EntityState instead");
    }

    /// <summary>The oracle reconstruction, spelled out rather than delegated.</summary>
    private static float Oracle(object cell, object offset) =>
        (Convert.ToInt32(cell, CultureInfo.InvariantCulture) - 32) * 512f
        + Convert.ToSingle(offset, CultureInfo.InvariantCulture);

    /// <summary>
    ///     The registry and the parity-gate twin list must stay index-aligned, which is what lets
    ///     <c>ProviderDigestParityTests</c> compare them positionally.
    /// </summary>
    [Test]
    public async Task RegistryAndGenericTwinList_AgreeOnOrderAndNames()
    {
        string[] registry = PerPlayerEntityValueProviderRegistry.CreateDefault()
            .All.Select(p => p.Name).ToArray();
        string[] twins = BuiltinProviderSpecs.CreateGenericPerPlayerProviders()
            .Select(p => p.Name).ToArray();

        // Sequence equality, not set equality: ProviderDigestParityTests walks the two lists by
        // index, so a reorder that preserved the names would break it without failing here.
        await Assert.That(twins.SequenceEqual(registry, StringComparer.Ordinal)).IsTrue()
            .Because($"registry=[{string.Join(", ", registry)}] twins=[{string.Join(", ", twins)}]");
        await Assert.That(registry.TakeLast(3).SequenceEqual(AxisNames, StringComparer.Ordinal)).IsTrue()
            .Because("the three position providers must be the last three, in x/y/z order");
    }
}
