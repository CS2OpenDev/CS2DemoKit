#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     The synthesized <c>timeout_started</c> / <c>timeout_ended</c>: one per observed edge of each of the
///     game rules' three timeout flags, on the post-frame list, carrying the side's count from the same
///     digest.
/// </summary>
[Category("Unit")]
public class TimeoutEventTests
{
    // Tracked order: T flag, CT flag, technical flag, T count, CT count.
    private static readonly string[] _contexts =
    [
        .. EntityChangeScanner.TimeoutFlagContexts, .. EntityChangeScanner.TimeoutsLeftContexts
    ];

    private static EntityChangeScanner Scanner(bool emit = true, IReadOnlyList<string>? contexts = null)
    {
        List<(IEntityValueProvider, StateNode)> tracked = [];
        foreach (string name in contexts ?? _contexts)
        {
            IEntityValueProvider provider = BuiltinProviderSpecs.CreateGameRulesProviders().Single(p => p.ContextName == name);
            tracked.Add((provider, new GenericValueNode<object>(name)));
        }

        return new EntityChangeScanner(new EntityStateLayer([]), tracked, null, false, null, null, null,
            emitTimeouts: emit);
    }

    private static EntityFrameDigest Digest(bool t, bool ct, bool tech, int tLeft, int ctLeft) =>
        new() { Singletons = [t, ct, tech, tLeft, ctLeft] };

    private static List<GameEvent> Run(EntityChangeScanner scanner, params EntityFrameDigest[] digests)
    {
        scanner.InjectDigests(digests);
        List<GameEvent> events = [];
        for (int frame = 0; frame < digests.Length; frame++)
        {
            scanner.AdvanceAndPollAt(frame, 100 + frame);
            events.AddRange(scanner.PostFrameMessages.OfType<GameEventMessage>().Select(m => m.DecodedEvent));
        }

        return events;
    }

    [Test]
    public async Task EachObservedEdge_EmitsOneEvent_WithTheSidesCount()
    {
        List<GameEvent> events = Run(Scanner(),
            Digest(false, false, false, 3, 3),
            Digest(false, true, false, 3, 2),
            Digest(false, true, false, 3, 2),
            Digest(false, false, false, 3, 2),
            Digest(true, false, false, 2, 2),
            Digest(false, false, true, 2, 2),
            Digest(false, false, false, 2, 2));

        await Assert.That(events).IsEquivalentTo(new List<GameEvent>
        {
            new TimeoutStartedEvent(101, 101, 101, 3, 2),
            new TimeoutEndedEvent(103, 103, 103, 3, 2),
            new TimeoutStartedEvent(104, 104, 104, 2, 2),
            new TimeoutEndedEvent(105, 105, 105, 2, 2),
            new TimeoutStartedEvent(105, 105, 105, 0, -1),
            new TimeoutEndedEvent(106, 106, 106, 0, -1)
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ARecordingThatStartsInsideATimeout_ReportsItsEndAlone()
    {
        List<GameEvent> events = Run(Scanner(),
            Digest(true, false, false, 1, 3),
            Digest(false, false, false, 1, 3));

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).IsEqualTo(new TimeoutEndedEvent(101, 101, 101, 2, 1));
    }

    [Test]
    public async Task WithoutTheCountProviders_TheEventsStillFire_AndCarryMinusOne()
    {
        EntityChangeScanner scanner = Scanner(contexts: EntityChangeScanner.TimeoutFlagContexts);
        List<GameEvent> events = Run(scanner,
            new EntityFrameDigest { Singletons = [false, false, false] },
            new EntityFrameDigest { Singletons = [false, true, false] },
            new EntityFrameDigest { Singletons = [false, false, false] });

        await Assert.That(scanner.EmitsTimeouts).IsTrue();
        await Assert.That(events).IsEquivalentTo(new List<GameEvent>
        {
            new TimeoutStartedEvent(101, 101, 101, 3, -1),
            new TimeoutEndedEvent(102, 102, 102, 3, -1)
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task WithoutTheSubscription_NothingIsEmitted()
    {
        List<GameEvent> events = Run(Scanner(emit: false),
            Digest(false, false, false, 3, 3),
            Digest(false, true, false, 3, 2));

        await Assert.That(events.Count).IsEqualTo(0);
    }
}
