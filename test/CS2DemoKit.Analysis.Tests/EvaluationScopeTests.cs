#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The evaluator walks <c>DemoFrame.MessageList</c> rather than <c>InnerMessages</c>, skipping
///     the stored <c>svc_UserCmds</c> payloads. These pin the reason that is safe.
///     <para>
///         The reason is not "no rule happens to want them today". It is that their dispatch key can
///         never match: <c>StateGraphEvaluator.GetDispatchKey</c> falls through to
///         <c>message.Payload.GetType()</c>, and a stored payload's type is the storage wrapper, not
///         <c>CSVCMsg_UserCommands</c>. The same was true of the earlier <c>DeferredMessage</c>, so
///         no ruleset has ever been able to dispatch on subtick input.
///     </para>
///     <para>
///         If that ever changes, these fail rather than the engine silently dropping messages a rule
///         asked for.
///     </para>
/// </summary>
[Category("Unit")]
public class EvaluationScopeTests
{
    /// <summary>
    ///     Mirrors <c>StateGraphEvaluator.GetDispatchKey</c>, which is private. Using
    ///     <c>Payload.GetType()</c> instead would miss every decoded game event, and the test would
    ///     pass while checking almost nothing.
    /// </summary>
    private static Type DispatchKeyOf(NetMessage message) => message switch
    {
        GameEventMessage gem => gem.DecodedEvent.Payload?.GetType() ?? gem.DecodedEvent.GetType(),
        EntityChangeMessage e => e.ChangeEvent.GetType(),
        _ => message.Payload.GetType()
    };

    private static (ParsedDemo Demo, BuildResult Build) LoadWithRules()
    {
        string path = DemoTestHelper.FindDemoPath()
                      ?? throw new SkipTestException("no demo available");
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        var rules = YamlConfigLoader.LoadShippedEmbedded();
        return (demo, DemoAnalysis.Build(demo, rules.Rulesets));
    }

    // The load-bearing invariant. Everything InnerMessages adds beyond MessageList is a stored
    // payload, and none of them can carry a type the graph dispatches on.
    [Test]
    public async Task StoredPayloads_CanNeverMatchADispatchKey()
    {
        (ParsedDemo demo, BuildResult build) = LoadWithRules();

        int extras = 0, dispatchable = 0;
        foreach (DemoFrame frame in demo.Frames)
        {
            IReadOnlyList<NetMessage> all = frame.InnerMessages;
            if (all.Count == frame.MessageList.Count)
            {
                continue;
            }

            HashSet<NetMessage> eager = new(frame.MessageList, ReferenceEqualityComparer.Instance
                as IEqualityComparer<NetMessage>);
            for (int i = 0; i < all.Count; i++)
            {
                NetMessage m = all[i];
                if (eager.Contains(m))
                {
                    continue;
                }

                extras++;
                if (build.RelevantMessageTypes.Contains(DispatchKeyOf(m)))
                {
                    dispatchable++;
                }
            }
        }

        if (extras == 0)
        {
            throw new SkipTestException("demo carries no stored payloads to check");
        }

        Console.WriteLine($"stored payloads examined: {extras}; dispatchable: {dispatchable}");
        await Assert.That(dispatchable).IsEqualTo(0)
            .Because("a stored payload the graph could dispatch on would be silently dropped by the "
                     + "evaluator, which walks MessageList");
    }

    // Guards the other half: MessageList must still hold everything the graph does dispatch on, so
    // narrowing the walk cannot lose a message a rule wanted.
    [Test]
    public async Task EagerList_HoldsEveryDispatchableMessage()
    {
        (ParsedDemo demo, BuildResult build) = LoadWithRules();

        int dispatchableInFull = 0, dispatchableInEager = 0;
        foreach (DemoFrame frame in demo.Frames)
        {
            IReadOnlyList<NetMessage> all = frame.InnerMessages;
            for (int i = 0; i < all.Count; i++)
            {
                if (build.RelevantMessageTypes.Contains(DispatchKeyOf(all[i])))
                {
                    dispatchableInFull++;
                }
            }

            foreach (NetMessage m in frame.MessageList)
            {
                if (build.RelevantMessageTypes.Contains(DispatchKeyOf(m)))
                {
                    dispatchableInEager++;
                }
            }
        }

        Console.WriteLine($"dispatchable via InnerMessages: {dispatchableInFull}; via MessageList: {dispatchableInEager}");
        await Assert.That(dispatchableInEager).IsEqualTo(dispatchableInFull);
        await Assert.That(dispatchableInEager).IsGreaterThan(0)
            .Because("zero would make this pass vacuously on a demo the rules never touch");
    }
}
