#region

using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Gate for the modular-UI playback framework (docs/ui/modular-ui-design.md): "play to frame N" must
///     yield the same <see cref="EntitySet" /> as "seek to frame N." The play loop steps the
///     authoritative tracker one frame at a time via the additive
///     <see cref="EntityTracker.AdvanceOneFrame" />; a discrete seek replays from zero via
///     <see cref="EntityTracker.AdvanceToIndex" />. This proves the two paths are byte-identical, so
///     the play loop and discrete seeks can never desync the entity state.
///     <para>
///         The equivalence is exact (not the analysis-relevant parity of the FullPacket checkpoint
///         spike): both paths invoke the SAME private per-frame primitive over the SAME frame
///         sequence from a fresh tracker, so every entity — in-PVS and dormant — and every field must
///         match. Any divergence means <c>AdvanceOneFrame</c> is not the trivial wrapper it claims.
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class AdvanceOneFrameEquivalenceTests
{
    [Test]
    public async Task AdvanceOneFrame_StepwisePlay_MatchesSeekToFrame()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo demo = DemoParser.Parse(bytes.AsMemory());
        IReadOnlyList<DemoFrame> frames = demo.Frames;

        await Assert.That(frames.Count).IsGreaterThan(0);

        // Early / mid / late targets — dormant accumulation and baseline history grow over a match,
        // so the equivalence is exercised at different entity-set sizes.
        int last = frames.Count - 1;
        int[] picks =
        {
            Math.Min(50, last), last / 2, last
        };

        foreach (int n in picks)
        {
            // "Seek to N": discrete checkpoint-style replay from zero.
            EntityTracker seek = new();
            seek.AdvanceToIndex(n, frames);

            // "Play to N": step one frame at a time, exactly as the play loop does.
            EntityTracker play = new();
            for (int i = 0; i <= n; i++)
            {
                play.AdvanceOneFrame(frames[i]);
            }

            await Assert.That(play.CurrentTick).IsEqualTo(seek.CurrentTick);
            await Assert.That(play.CurrentFrameIndex).IsEqualTo(seek.CurrentFrameIndex);

            Dictionary<int, EntityState> seekAll =
                seek.CurrentEntities.AllIndexed().ToDictionary(t => t.Index, t => t.Entity);
            Dictionary<int, EntityState> playAll =
                play.CurrentEntities.AllIndexed().ToDictionary(t => t.Index, t => t.Entity);

            Console.WriteLine(
                $"frame {n}: seek entities={seekAll.Count}  play entities={playAll.Count}  tick={seek.CurrentTick}");

            await Assert.That(playAll.Count).IsEqualTo(seekAll.Count);

            foreach ((int idx, EntityState se) in seekAll)
            {
                await Assert.That(playAll.ContainsKey(idx)).IsTrue();
                EntityState pe = playAll[idx];

                string? mismatch = CompareFields(se, pe);
                if (mismatch is not null)
                {
                    Console.WriteLine($"  ent#{idx} {se.ClassName}: {mismatch}");
                }

                await Assert.That(mismatch).IsNull();
                await Assert.That(pe.IsInPvs).IsEqualTo(se.IsInPvs);
            }
        }
    }

    /// <summary>
    ///     Exact whole-set field comparison between a seek-built and a play-built entity. Returns a
    ///     description of the first divergence, or null when identical.
    /// </summary>
    private static string? CompareFields(EntityState a, EntityState b)
    {
        if (a.ClassName != b.ClassName)
        {
            return $"class {a.ClassName} vs {b.ClassName}";
        }

        if (a.Serial != b.Serial)
        {
            return $"serial {a.Serial} vs {b.Serial}";
        }

        IReadOnlyDictionary<string, object?> fa = a.Fields;
        IReadOnlyDictionary<string, object?> fb = b.Fields;
        if (fa.Count != fb.Count)
        {
            string? onlyInA = fa.Keys.FirstOrDefault(k => !fb.ContainsKey(k));
            string? onlyInB = fb.Keys.FirstOrDefault(k => !fa.ContainsKey(k));
            return $"field-count {fa.Count} vs {fb.Count} (onlyA={onlyInA ?? "-"}, onlyB={onlyInB ?? "-"})";
        }

        foreach ((string key, object? va) in fa)
        {
            if (!fb.TryGetValue(key, out object? vb))
            {
                return $"key {key} absent in play tracker";
            }

            if (!Equals(va, vb))
            {
                return $"{key} = {va} vs {vb}";
            }
        }

        return null;
    }
}
