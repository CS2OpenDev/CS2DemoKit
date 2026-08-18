#region

using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.Entities.Generated;
using CS2DemoKit.Parser.Entities.SchemaLens;
using CS2DemoKit.Parser.Entities.SdkAbstractions;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.SdkAbstractions.Tests;

/// <summary>
///     End-to-end smoke over one real demo: a Lens-bound tracker replay, bindings built by
///     <see cref="LensBindingBuilder" /> from the shipped Lens state, and a
///     <see cref="LensBoundReader" /> whose ordinal reads must agree — presence and value —
///     with the <see cref="EntityState.Fields" /> projection for every canonical pawn path.
///     One parse, per the parser-test memory rules; skips gracefully without a demo.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class SdkAdapterDemoSmokeTests
{
    [Test]
    public async Task OrdinalReads_MatchTheFieldsProjection_OnARealDemo()
    {
        string? demoPath = DemoTestHelper.FindDemoPath();
        if (demoPath is null)
        {
            throw new SkipTestException("No demo found — skipping SDK-adapter smoke test.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(demoPath);
        ParsedDemo parsed = DemoParser.Parse(bytes.AsMemory());

        LensState lens = GeneratedLensRegistry.Load();
        EntityTracker tracker = new();
        tracker.BindLensResolver(LensResolverBridge.Build(lens));
        tracker.Replay(parsed.Frames);

        EntityState? pawn = tracker.CurrentEntities.OfClass("CCSPlayerPawn").FirstOrDefault();
        if (pawn is null)
        {
            throw new SkipTestException("No CCSPlayerPawn in demo — skipping.");
        }

        EntityClassBinding binding = LensBindingBuilder.Build(lens, "CCSPlayerPawn");
        TrackerEntityWorld world = new(tracker);
        LensBoundReader reader = world.CreateReader(binding, pawn);

        await Assert.That(reader.EngineClassName).IsEqualTo("CCSPlayerPawn");

        IReadOnlyDictionary<string, object?> fields = pawn.Fields;

        // The Fields projection keys by the WIRE spelling; since the lens became derived from
        // the SDK package, the binding ordinals carry the SDK canonicals (cells included). Join
        // through the lens AliasMap — every spelling the runtime can bind for a canonical —
        // mirroring the stage-3 battery's alias-bridge join.
        Dictionary<string, List<string>> spellingsByCanonical = new(StringComparer.Ordinal);
        foreach ((string spelling, string canonical) in lens.AliasMap["CCSPlayerPawn"])
        {
            if (!spellingsByCanonical.TryGetValue(canonical, out List<string>? list))
            {
                spellingsByCanonical[canonical] = list = [];
            }

            list.Add(spelling);
        }

        List<string> divergences = new();

        for (int ordinal = 0; ordinal < binding.CanonicalPaths.Count; ordinal++)
        {
            string path = binding.CanonicalPaths[ordinal];
            bool projected = fields.TryGetValue(path, out object? projectedValue);
            if (!projected && spellingsByCanonical.TryGetValue(path, out List<string>? candidates))
            {
                foreach (string spelling in candidates)
                {
                    if (fields.TryGetValue(spelling, out projectedValue))
                    {
                        projected = true;
                        break;
                    }
                }
            }

            bool read = reader.TryReadObject(ordinal, out object? readValue);

            if (projected != read)
            {
                divergences.Add($"  PRESENCE-DIVERGES: [{ordinal}] {path}  Fields={projected}, reader={read}");
                continue;
            }

            if (projected && !Equals(projectedValue, readValue))
            {
                divergences.Add(
                    $"  VALUE-DIVERGES: [{ordinal}] {path}  Fields={projectedValue}, reader={readValue}");
            }
        }

        await Assert.That(string.Join("\n", divergences)).IsEqualTo("");

        // Typed spot-checks on the paths every analysis consumer leans on.
        int healthOrdinal = OrdinalOf(binding, "m_iHealth");
        if (fields.TryGetValue("m_iHealth", out object? health))
        {
            await Assert.That(reader.TryReadInt32(healthOrdinal, out int typedHealth)).IsTrue();
            await Assert.That(typedHealth).IsEqualTo((int)health!);
        }

        int controllerOrdinal = OrdinalOf(binding, "m_hController");
        if (fields.TryGetValue("m_hController", out object? rawController))
        {
            await Assert.That(reader.TryReadEntityHandle(controllerOrdinal, out uint handle)).IsTrue();
            // The projection carries the decoder's boxed width (ulong); the seam folds it to
            // the raw packed uint without touching the bits.
            await Assert.That((ulong)handle)
                .IsEqualTo(Convert.ToUInt64(rawController, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static int OrdinalOf(EntityClassBinding binding, string canonicalPath)
    {
        for (int i = 0; i < binding.CanonicalPaths.Count; i++)
        {
            if (string.Equals(binding.CanonicalPaths[i], canonicalPath, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"'{canonicalPath}' is not in the binding's ordinal space.");
    }
}
