#region

using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     Issue #58's sample contract on two full matchmaking demos, the build-10231 de_nuke the issue
///     measured and a build-10924 de_dust2. Both live in the local corpus only (point
///     <c>CS2DEMOKIT_CORPUS_DIR</c> at the Steam replays folder), and the tests skip cleanly
///     without them.
/// </summary>
[Category("Corpus")]
[NotInParallel]
public class PositionSamplerCorpusTests
{
    public static IEnumerable<string> Demos()
    {
        yield return "match730_003731893271710924851_1024675027_129.dem";
        yield return "match730_003844470418295488702_1553410689_408.dem";
    }

    /// <summary>
    ///     Over the whole match at stride 1: no pawn reads alive after its <c>player_death</c>, no
    ///     pawn reads dead while the events say it is alive (true on these two demos; a disconnect
    ///     breaks it elsewhere, see <see cref="PawnLookup.IsAlive" />), dead pawns are a real share
    ///     of the one-second rows, <c>Place</c> is never null, and every team is T or CT.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Demos))]
    public async Task Walk_AliveTeamAndPlaceHoldOverAWholeMatch(string file)
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(file));
        List<PositionSample> samples = PositionSampler.Walk(demo).ToList();

        await Assert.That(PositionSamplerTests.AliveAfterDeath(demo, samples)).IsEqualTo(0)
            .Because("no pawn reads alive on or after the frame that carries its player_death");
        await Assert.That(DeadWhileEventsSayAlive(demo, samples)).IsEqualTo(0)
            .Because("on these two demos every dead pawn has a player_death behind it");
        await Assert.That(samples.All(s => s.Place is not null)).IsTrue()
            .Because("m_szLastPlaceName is always on the wire; outside a named area it is empty");
        await Assert.That(samples.All(s => s.Team is 2 or 3)).IsTrue();

        // One row per second: the first frame of each tick on the 64-tick grid.
        int rows = 0, rowsWithDead = 0;
        foreach (IGrouping<int, PositionSample> row in samples
                     .Where(s => s.Tick > 0 && s.Tick % 64 == 0)
                     .GroupBy(s => s.Tick))
        {
            int first = row.Min(s => s.FrameIndex);
            rows++;
            if (row.Any(s => s.FrameIndex == first && !s.IsAlive))
            {
                rowsWithDead++;
            }
        }

        Console.WriteLine($"{file}: {samples.Count} samples, {rowsWithDead} of {rows} one-second rows "
                          + $"carry a dead pawn, place empty on {samples.Count(s => s.Place == "")}");
        await Assert.That(rowsWithDead * 10).IsGreaterThan(rows)
            .Because("more than a tenth of the one-second rows carry a dead pawn (measured 18.6% and 20.4%)");
    }

    /// <summary>
    ///     Samples that read dead while the <c>player_death</c>/<c>player_spawn</c> replay says the
    ///     slot is alive. Only slots that have spawned at least once count, so a pawn seen before its
    ///     first spawn event is not an event-alive one.
    /// </summary>
    private static int DeadWhileEventsSayAlive(ParsedDemo demo, IEnumerable<PositionSample> samples)
    {
        List<GameEvent> lifecycle = demo.AllGameEvents
            .Where(e => e.Payload is PlayerDeathEvent or PlayerSpawnEvent)
            .OrderBy(e => e.FrameNumber)
            .ToList();
        bool[] aliveByEvent = new bool[64];
        int next = 0;
        int count = 0;
        foreach (PositionSample s in samples)
        {
            while (next < lifecycle.Count && lifecycle[next].FrameNumber <= s.FrameIndex)
            {
                GameEvent e = lifecycle[next++];
                (int slot, bool spawned) = e.Payload switch
                {
                    PlayerDeathEvent d => (d.UserId, false),
                    PlayerSpawnEvent p => (p.UserId, true),
                    _ => (-1, false)
                };
                if (slot is >= 0 and < 64)
                {
                    aliveByEvent[slot] = spawned;
                }
            }

            if (!s.IsAlive && aliveByEvent[s.PlayerSlot])
            {
                count++;
            }
        }

        return count;
    }
}
