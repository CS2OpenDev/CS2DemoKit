#region

using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     <c>entity.pawn.on_ground</c>, <c>entity.pawn.ducking</c> and <c>entity.weapon.throw_strength</c>
///     through a <see cref="FrameView" /> on the sample demo, at both parallelism caps: each flag bit
///     equals its mask over <c>entity.pawn.flags</c> on every slot-frame and has a value exactly when
///     the flags do, and throw strength reads a value in 0..1 whenever the active weapon is a
///     grenade. After the grenade goes away the column keeps its last value, as every column does
///     once its read stops resolving, so a non-grenade weapon is counted, not failed.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class PawnFlagsObserverTests
{
    private const string Flags = "entity.pawn.flags";
    private const string OnGround = "entity.pawn.on_ground";
    private const string Ducking = "entity.pawn.ducking";
    private const string ThrowStrength = "entity.weapon.throw_strength";
    private const string WeaponClass = "entity.pawn.active_weapon_class";

    private static readonly HashSet<string> GrenadeWeapons =
        ["CHEGrenade", "CFlashbang", "CSmokeGrenade", "CMolotovGrenade", "CIncendiaryGrenade", "CDecoyGrenade"];

    private sealed class Recorder : IFrameObserver
    {
        public string? First;
        public int Flagged, OnGroundTrue, OnGroundFalse, DuckingTrue, GrenadeOut, Stale;

        public void OnFrame(FrameView frame)
        {
            ObservedProvider flags = frame.Provider(Flags);
            ObservedProvider onGround = frame.Provider(OnGround);
            ObservedProvider ducking = frame.Provider(Ducking);
            ObservedProvider strength = frame.Provider(ThrowStrength);
            ObservedProvider weapon = frame.Provider(WeaponClass);
            for (int slot = 0; slot < 64 && First is null; slot++)
            {
                bool hasFlags = frame.TryReadInt(flags, slot, out int f);
                bool hasGround = frame.TryReadInt(onGround, slot, out int g);
                bool hasDuck = frame.TryReadInt(ducking, slot, out int d);
                if (hasGround != hasFlags || hasDuck != hasFlags)
                {
                    First = $"frame {frame.FrameIndex} slot {slot}: flags {hasFlags}, on_ground {hasGround}, ducking {hasDuck}";
                    return;
                }

                if (hasFlags)
                {
                    Flagged++;
                    if (g != ((f & 1) != 0 ? 1 : 0) || d != ((f & 2) != 0 ? 1 : 0))
                    {
                        First = $"frame {frame.FrameIndex} slot {slot}: flags {f:X}, on_ground {g}, ducking {d}";
                        return;
                    }

                    _ = g == 1 ? OnGroundTrue++ : OnGroundFalse++;
                    DuckingTrue += d;
                }

                bool hasStrength = frame.TryReadFloat(strength, slot, out float s);
                if (frame.TryReadString(weapon, slot, out string? cls) && GrenadeWeapons.Contains(cls!))
                {
                    GrenadeOut++;
                    if (!hasStrength || s is < 0f or > 1f)
                    {
                        First = $"frame {frame.FrameIndex} slot {slot}: {cls} out, throw strength {(hasStrength ? s : "none")}";
                        return;
                    }
                }
                else if (hasStrength)
                {
                    Stale++;
                }
            }
        }

        public void Finish()
        {
        }
    }

    [Test]
    public async Task FlagBitsAndThrowStrength_AtTheSequentialCap() => await AssertConsistent(1);

    [Test]
    public async Task FlagBitsAndThrowStrength_AtTheDefaultCap() => await AssertConsistent(null);

    private static async Task AssertConsistent(int? maxDegreeOfParallelism)
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));
        Recorder observer = new();
        DemoAnalysis.Run(demo, [], new AnalysisOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CaptureSnapshots = false,
            ObservedProviders = [Flags, OnGround, Ducking, ThrowStrength, WeaponClass],
            FrameObserver = observer
        });

        Console.WriteLine($"flagged slot-frames {observer.Flagged}, on ground {observer.OnGroundTrue}, airborne "
                          + $"{observer.OnGroundFalse}, ducking {observer.DuckingTrue}, grenade out {observer.GrenadeOut}, "
                          + $"stale strength {observer.Stale}");
        await Assert.That(observer.First).IsNull().Because(observer.First ?? "");
        await Assert.That(observer.OnGroundTrue).IsGreaterThan(0);
        await Assert.That(observer.OnGroundFalse).IsGreaterThan(0);
        await Assert.That(observer.DuckingTrue).IsGreaterThan(0);
        await Assert.That(observer.GrenadeOut).IsGreaterThan(0);
    }
}
