#region

using System.Text;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     <c>m_iClip1</c> decodes to the rounds in the magazine, with -1 for a weapon that has none
///     (#49). The field is networked with the <c>minusone</c> serializer; read as zigzag it
///     alternated in sign and came out one high. These walk the committed sample, so they always
///     run: every weapon entity on every frame is tallied by class.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class WeaponClipDecodeTests
{
    /// <summary>Measured full magazines on the sample, one per firearm class it carries.</summary>
    private static readonly Dictionary<string, int> SampleMagazines = new(StringComparer.Ordinal)
    {
        ["CWeaponGalilAR"] = 35,
        ["CAK47"] = 30,
        ["CWeaponMP9"] = 30,
        ["CWeaponMAC10"] = 30,
        ["CWeaponAug"] = 30,
        ["CWeaponM4A1"] = 30,
        ["CWeaponGlock"] = 20,
        ["CWeaponFiveSeven"] = 20,
        ["CWeaponTec9"] = 18,
        ["CWeaponHKP2000"] = 13,
        ["CDEagle"] = 7
    };

    [Test]
    public async Task Sample_ClipReadsAreMagazineCounts()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        ClipTally tally = ClipTally.Walk(demo.Frames);
        Console.WriteLine(tally.Describe());

        await Assert.That(tally.Error).IsNull();
        await Assert.That(tally.BelowMinusOne).IsEqualTo(0L);
        await Assert.That(tally.ByClass.TryGetValue("CKnife", out ClipClassTally? knife)).IsTrue();
        await Assert.That(knife!.Reads).IsGreaterThanOrEqualTo(90_000L);

        foreach ((string cls, ClipClassTally t) in tally.ByClass)
        {
            if (ClipTally.IsNoMagazine(cls))
            {
                await Assert.That(t.MinusOnes).IsEqualTo(t.Reads).Because($"{cls} has no magazine");
            }
            else
            {
                await Assert.That(t.MinusOnes).IsEqualTo(0L).Because($"{cls} is a firearm");
            }
        }

        foreach ((string cls, int magazine) in SampleMagazines)
        {
            await Assert.That(tally.ByClass.ContainsKey(cls)).IsTrue().Because($"{cls} is on the sample");
            await Assert.That(tally.ByClass[cls].Max).IsEqualTo(magazine).Because($"{cls} magazine");
        }

        await Assert.That(tally.FirearmChanges).IsGreaterThan(0L);
        await Assert.That(tally.FirearmDecrements * 10).IsGreaterThanOrEqualTo(tally.FirearmChanges * 9);
    }

    /// <summary>
    ///     The forward reader decodes through the same descriptors as the retained parse, so the
    ///     clip tallies from a reader-fed tracker are the retained tallies exactly.
    /// </summary>
    [Test]
    public async Task Sample_ForwardAndRetainedPathsAgree()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        ClipTally retained = ClipTally.Walk(demo.Frames);

        byte[] bytes = await File.ReadAllBytesAsync(path);
        ClipTally forward;
        using (DemoReader reader = DemoReader.Open(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.EntityReplay }))
        {
            forward = ClipTally.Walk(ReadAll(reader));
        }

        await Assert.That(forward.Error).IsNull();
        await Assert.That(forward.Describe()).IsEqualTo(retained.Describe());
    }

    /// <summary>
    ///     <c>m_iClip2</c> uses the same serializer. No CS2 weapon has a secondary clip, so every
    ///     read on every weapon class is -1 (raw 0). As zigzag the same bytes read 0.
    /// </summary>
    [Test]
    public async Task Sample_Clip2ReadsMinusOneOnEveryWeapon()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        EntityTracker tracker = EntityTrackerFactory.CreateCurated();
        long reads = 0;
        HashSet<string> classes = new(StringComparer.Ordinal);
        Dictionary<int, long> values = [];
        foreach (DemoFrame frame in demo.Frames)
        {
            tracker.AdvanceOneFrame(frame);
            foreach ((int _, EntityState entity) in tracker.CurrentEntities.AllIndexed())
            {
                if (entity.TryGet<int>("m_iClip2") is not { } clip2)
                {
                    continue;
                }

                reads++;
                classes.Add(entity.ClassName);
                values[clip2] = values.GetValueOrDefault(clip2) + 1;
            }
        }

        Console.WriteLine($"m_iClip2 reads={reads} classes={classes.Count} values="
                          + string.Join(",", values.Select(kv => $"{kv.Key}:{kv.Value}")));

        await Assert.That(tracker.LastEntityError).IsNull();
        await Assert.That(reads).IsGreaterThanOrEqualTo(90_000L);
        await Assert.That(classes).Contains("CKnife");
        await Assert.That(classes).Contains("CAK47");
        await Assert.That(values.Keys.ToArray()).IsEquivalentTo(new[] { -1 });
    }

    private static IEnumerable<DemoFrame> ReadAll(DemoReader reader)
    {
        while (reader.TryReadNext(out DemoFrame? frame))
        {
            yield return frame;
        }
    }
}

/// <summary>Per-class <c>m_iClip1</c> reads over a walk.</summary>
internal sealed class ClipClassTally
{
    public long Reads { get; set; }
    public long MinusOnes { get; set; }
    public int Min { get; set; } = int.MaxValue;
    public int Max { get; set; } = int.MinValue;
}

/// <summary>
///     Replays frames through a curated tracker and tallies every <c>m_iClip1</c> read by class,
///     plus how each firearm's clip moves from one frame to the next.
/// </summary>
internal sealed class ClipTally
{
    private const string Clip1 = "m_iClip1";

    public SortedDictionary<string, ClipClassTally> ByClass { get; } = new(StringComparer.Ordinal);
    public long BelowMinusOne { get; private set; }
    public long FirearmChanges { get; private set; }
    public long FirearmDecrements { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Weapons that carry no magazine: knives, grenades and the bomb.</summary>
    public static bool IsNoMagazine(string className) =>
        className.Contains("Knife", StringComparison.Ordinal)
        || className.EndsWith("Grenade", StringComparison.Ordinal)
        || className is "CFlashbang" or "CC4";

    public static ClipTally Walk(IEnumerable<DemoFrame> frames)
    {
        ClipTally tally = new();
        EntityTracker tracker = EntityTrackerFactory.CreateCurated();
        Dictionary<(int Index, int Serial), int> last = [];
        foreach (DemoFrame frame in frames)
        {
            tracker.AdvanceOneFrame(frame);
            foreach ((int index, EntityState entity) in tracker.CurrentEntities.AllIndexed())
            {
                if (entity.TryGet<int>(Clip1) is not { } clip)
                {
                    continue;
                }

                tally.Record(index, entity, clip, last);
            }
        }

        tally.Error = tracker.LastEntityError?.ToString();
        return tally;
    }

    private void Record(int index, EntityState entity, int clip, Dictionary<(int, int), int> last)
    {
        if (!ByClass.TryGetValue(entity.ClassName, out ClipClassTally? t))
        {
            t = new ClipClassTally();
            ByClass[entity.ClassName] = t;
        }

        t.Reads++;
        t.Min = Math.Min(t.Min, clip);
        t.Max = Math.Max(t.Max, clip);
        if (clip == -1)
        {
            t.MinusOnes++;
        }
        else if (clip < -1)
        {
            BelowMinusOne++;
        }

        (int, int) key = (index, entity.Serial);
        if (last.TryGetValue(key, out int previous) && previous != clip && !IsNoMagazine(entity.ClassName))
        {
            FirearmChanges++;
            if (clip == previous - 1)
            {
                FirearmDecrements++;
            }
        }

        last[key] = clip;
    }

    /// <summary>A stable rendering of the whole tally, one line per class.</summary>
    public string Describe()
    {
        StringBuilder sb = new();
        sb.Append("belowMinusOne=").Append(BelowMinusOne)
            .Append(" firearmChanges=").Append(FirearmChanges)
            .Append(" decrements=").Append(FirearmDecrements).AppendLine();
        foreach ((string cls, ClipClassTally t) in ByClass)
        {
            sb.Append(cls).Append(" reads=").Append(t.Reads).Append(" min=").Append(t.Min)
                .Append(" max=").Append(t.Max).Append(" minusOnes=").Append(t.MinusOnes).AppendLine();
        }

        return sb.ToString();
    }
}
