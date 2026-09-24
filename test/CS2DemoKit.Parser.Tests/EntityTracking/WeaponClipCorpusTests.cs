#region

using CS2DemoKit.Parser.Entities;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The <c>m_iClip1</c> invariants that hold on any build, over every demo this machine has:
///     the sample, <c>&lt;repo-root&gt;/demos/**</c>, and <c>DEMO_PATH</c> when it is set. Explicit
///     because it replays each demo in full. A build that stopped serializing the field with
///     <c>minusone</c> would fail here first: knives would stop reading -1 and firearm clips would
///     stop counting down one at a time.
/// </summary>
[Explicit]
[NotInParallel]
[Category("Integration")]
public class WeaponClipCorpusTests
{
    /// <summary>The largest magazine in the game (the Negev's 150).</summary>
    private const int LargestMagazine = 150;

    public static IEnumerable<string> Demos()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in DemoReaderCorpusTests.Demos())
        {
            if (seen.Add(Path.GetFullPath(path)))
            {
                yield return path;
            }
        }

        string? fromEnv = Environment.GetEnvironmentVariable("DEMO_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv) && seen.Add(Path.GetFullPath(fromEnv)))
        {
            yield return fromEnv;
        }
    }

    [Test]
    [MethodDataSource(nameof(Demos))]
    public async Task Corpus_ClipInvariants(string path)
    {
        if (!File.Exists(path))
        {
            throw new SkipTestException($"Demo '{path}' is not present on this machine.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(path);
        ClipTally tally;
        using (DemoReader reader = DemoReader.Open(bytes.AsMemory(), new ParseOptions { Plan = DecodePlan.EntityReplay }))
        {
            tally = ClipTally.Walk(ReadAll(reader));
        }

        Console.WriteLine(Path.GetFileName(path));
        Console.WriteLine(tally.Describe());

        await Assert.That(tally.Error).IsNull();
        await Assert.That(tally.BelowMinusOne).IsEqualTo(0L);
        foreach ((string cls, ClipClassTally t) in tally.ByClass)
        {
            if (ClipTally.IsNoMagazine(cls))
            {
                await Assert.That(t.MinusOnes).IsEqualTo(t.Reads).Because($"{cls} has no magazine");
            }
            else
            {
                await Assert.That(t.MinusOnes).IsEqualTo(0L).Because($"{cls} is a firearm");
                await Assert.That(t.Max).IsLessThanOrEqualTo(LargestMagazine).Because($"{cls} magazine");
            }
        }

        if (tally.FirearmChanges > 0)
        {
            await Assert.That(tally.FirearmDecrements * 100).IsGreaterThanOrEqualTo(tally.FirearmChanges * 85);
        }
    }

    private static IEnumerable<DemoFrame> ReadAll(DemoReader reader)
    {
        while (reader.TryReadNext(out DemoFrame? frame))
        {
            yield return frame;
        }
    }
}
