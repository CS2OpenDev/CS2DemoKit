#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
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
    ///     A <c>for: each_projectile</c> ruleset. Its instances live on events the evaluator
    ///     synthesizes from entity changes, so loading it widens the dispatch set past the shipped
    ///     rulesets' without naming a stored payload.
    /// </summary>
    private const string GrenadeRows = """
        ruleset: grenade_rows
        for: each_projectile
        stats:
          class:  { capture: projectile.class, keep: first }
          end_x:  { capture: projectile.pos_x, keep: last }
          lands:  { count: projectile_landed }
        show:
          tables:
            grenades:
              per: projectile
              columns:
                - { stat: class, label: class }
                - { stat: end_x, label: end_x }
                - { stat: lands, label: lands }
        """;

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

    private static (ParsedDemo Demo, BuildResult Build) LoadWithRules(params string[] extraYaml)
    {
        string path = DemoTestHelper.FindDemoPath()
                      ?? throw new SkipTestException("no demo available");
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
        List<RulesetDoc> docs = [.. YamlConfigLoader.LoadShippedEmbedded().Rulesets];
        if (extraYaml.Length > 0)
        {
            RuleConfigLoadResult extra = YamlConfigLoader.LoadDocuments(
                extraYaml.Select((y, i) => ($"extra{i}.rules.yaml", y)));
            if (extra.Errors.Count > 0)
            {
                throw new InvalidOperationException("load failed: " + string.Join("; ", extra.Errors));
            }

            docs.AddRange(extra.Rulesets);
        }

        return (demo, DemoAnalysis.Build(demo, docs));
    }

    /// <summary>
    ///     Stored payloads are the messages <c>InnerMessages</c> adds beyond <c>MessageList</c>;
    ///     <c>Dispatchable*</c> counts the messages whose dispatch key the build subscribes to.
    /// </summary>
    private static (int Stored, int DispatchableStored, int DispatchableInFull, int DispatchableInEager)
        CountDispatch(ParsedDemo demo, BuildResult build)
    {
        int stored = 0, dispatchableStored = 0, dispatchableInFull = 0, dispatchableInEager = 0;
        foreach (DemoFrame frame in demo.Frames)
        {
            IReadOnlyList<NetMessage> all = frame.InnerMessages;
            HashSet<NetMessage> eager = new(frame.MessageList, ReferenceEqualityComparer.Instance
                as IEqualityComparer<NetMessage>);
            for (int i = 0; i < all.Count; i++)
            {
                NetMessage m = all[i];
                bool dispatchable = build.RelevantMessageTypes.Contains(DispatchKeyOf(m));
                if (dispatchable)
                {
                    dispatchableInFull++;
                }

                if (eager.Contains(m))
                {
                    continue;
                }

                stored++;
                if (dispatchable)
                {
                    dispatchableStored++;
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

        return (stored, dispatchableStored, dispatchableInFull, dispatchableInEager);
    }

    // The load-bearing invariant. Everything InnerMessages adds beyond MessageList is a stored
    // payload, and none of them can carry a type the graph dispatches on.
    [Test]
    public async Task StoredPayloads_CanNeverMatchADispatchKey()
    {
        (ParsedDemo demo, BuildResult build) = LoadWithRules();
        (int stored, int dispatchable, _, _) = CountDispatch(demo, build);

        if (stored == 0)
        {
            throw new SkipTestException("demo carries no stored payloads to check");
        }

        Console.WriteLine($"stored payloads examined: {stored}; dispatchable: {dispatchable}");
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
        (_, _, int dispatchableInFull, int dispatchableInEager) = CountDispatch(demo, build);

        Console.WriteLine($"dispatchable via InnerMessages: {dispatchableInFull}; via MessageList: {dispatchableInEager}");
        await Assert.That(dispatchableInEager).IsEqualTo(dispatchableInFull);
        await Assert.That(dispatchableInEager).IsGreaterThan(0)
            .Because("zero would make this pass vacuously on a demo the rules never touch");
    }

    // Both halves again with the projectile scope loaded. Its lifecycle is synthesized from entity
    // changes, which widens the dispatch set; the widened set must still be served entirely by
    // MessageList and must still miss every stored payload.
    [Test]
    public async Task ProjectileScope_WidensDispatch_OnlyOverEagerMessages()
    {
        (ParsedDemo demo, BuildResult build) = LoadWithRules(GrenadeRows);
        await Assert.That(build.ExcludedRulesets.Count).IsEqualTo(0)
            .Because(string.Join("; ", build.ExcludedRulesets.SelectMany(e => e.Diagnostics).Select(d => d.ToString())));

        BuildResult shipped = DemoAnalysis.Build(demo, YamlConfigLoader.LoadShippedEmbedded().Rulesets);
        await Assert.That(build.RelevantMessageTypes.Count).IsGreaterThanOrEqualTo(shipped.RelevantMessageTypes.Count)
            .Because("the projectile scope subscribes to at least what the shipped rulesets do");

        (int stored, int dispatchableStored, int dispatchableInFull, int dispatchableInEager) = CountDispatch(demo, build);
        Console.WriteLine($"with each_projectile: stored {stored}; dispatchable stored {dispatchableStored}; "
                          + $"via InnerMessages {dispatchableInFull}; via MessageList {dispatchableInEager}");
        await Assert.That(dispatchableStored).IsEqualTo(0);
        await Assert.That(dispatchableInEager).IsEqualTo(dispatchableInFull);
        await Assert.That(dispatchableInEager).IsGreaterThan(0);
    }
}
