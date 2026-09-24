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
///     The documented contract of <c>entity.pawn.active_weapon_clip</c> on the committed sample:
///     never below -1, and -1 exactly when the active weapon has no magazine (a knife, a grenade or
///     the C4). Before #49 the column read the zigzag decoding of clip + 1, so knives read 0 and -1
///     never occurred; this pins the sentinel the provider docs describe.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ActiveWeaponClipSampleTests
{
    private const int MinimumComparisons = 50_000;

    [Test]
    public async Task Provider_ReadsMinusOneExactlyForNoMagazineWeapons()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        GenericPerPlayerFieldProvider clip = new(BuiltinProviderSpecs.PawnActiveWeaponClip);
        GenericPerPlayerFieldProvider cls = new(BuiltinProviderSpecs.PawnActiveWeaponClass);
        EntityStateLayer layer = new(demo.Frames);

        long compared = 0;
        long noMagazine = 0;
        long belowMinusOne = 0;
        string? firstMismatch = null;
        for (int n = 0; n < demo.Frames.Count; n++)
        {
            layer.SeekToTick(demo.Frames[n].ServerTick);
            EntityTracker tracker = layer.Tracker;
            PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
            {
                CSPlayerPawn wrapper = SdkEntityWorlds.Wrap<CSPlayerPawn>(tracker, pawn)!;
                if (clip.ReadForPawn(tracker, wrapper) is not int value
                    || cls.ReadForPawn(tracker, wrapper) is not string className)
                {
                    return;
                }

                compared++;
                if (value < -1)
                {
                    belowMinusOne++;
                }

                bool expectMinusOne = IsNoMagazine(className);
                if (expectMinusOne)
                {
                    noMagazine++;
                }

                if (value == -1 != expectMinusOne)
                {
                    firstMismatch ??= $"frame {n} slot {slot}: {className} read {value}";
                }
            });
        }

        Console.WriteLine($"compared={compared} noMagazine={noMagazine}");
        await Assert.That(compared).IsGreaterThanOrEqualTo(MinimumComparisons);
        await Assert.That(noMagazine).IsGreaterThan(0L);
        await Assert.That(belowMinusOne).IsEqualTo(0L);
        await Assert.That(firstMismatch).IsNull();
    }

    private static bool IsNoMagazine(string className) =>
        className.Contains("Knife", StringComparison.Ordinal)
        || className.EndsWith("Grenade", StringComparison.Ordinal)
        || className is "CFlashbang" or "CC4";
}
