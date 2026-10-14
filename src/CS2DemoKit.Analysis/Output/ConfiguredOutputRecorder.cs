#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;

#endregion

namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     The evaluator's view of an observer that watches a run message by message, for a consumer
///     that needs state at particular moments without keeping a snapshot row per message.
///     Internal: the one implementation is <see cref="ConfiguredOutputRecorder" />.
/// </summary>
internal interface IEvaluationObserver
{
    /// <summary>Called before a message is dispatched, before anything it triggers (materialization, the round reset) runs.</summary>
    /// <param name="dispatchKey">The message's dispatch type.</param>
    void BeforeMessage(Type dispatchKey);

    /// <summary>Called once a message has been dispatched and its logic settled.</summary>
    /// <param name="tick">The tick of the message's frame (the frame clock).</param>
    void AfterMessage(int tick);

    /// <summary>Called when a player template has materialized, before any of its edges dispatch.</summary>
    /// <param name="player">The materialized player.</param>
    void OnMaterialized(PerPlayerNodeTemplate.MaterializedPlayer player);

    /// <summary>Called once the last frame has been evaluated.</summary>
    void Finish();
}

/// <summary>
///     Records what a forward run's configured tables need, so they project without per-message
///     snapshots. A snapshot table samples a round at the last snapshot that still held its round
///     number; this samples the same state, the moment before the message that moves the round
///     number off it. Only a handful of event types can move it (the concrete events of
///     <c>$round_freeze_end</c>, <c>$match_start</c> and <c>$match_end</c>, which are the triggers of
///     the <c>round_number</c> and <c>match_live</c> context rules), so the recorder samples just
///     before each of those and keeps the sample only when the round number really did move.
///     <para>
///         A sample holds only the nodes a table reads (every metric node of every output, and the
///         team rosters), captured the way a snapshot row captures them. A player who materializes
///         after a round was sampled reads that round at the node's at-materialization value, which is
///         what a snapshot row that predates the player's column serves. The end of the run closes
///         the current round and supplies the final state the per-match tables read.
///     </para>
/// </summary>
internal sealed class ConfiguredOutputRecorder : IEvaluationObserver
{
    private readonly IReadOnlySet<Type> _boundaryTypes;
    private readonly Dictionary<StateNode, NodeSnapshot> _defaults = new(ReferenceEqualityComparer.Instance);
    private readonly List<EventRow> _events = [];
    private readonly List<OutputDef> _eventOutputs = [];
    private readonly IReadOnlyDictionary<string, StateNode>? _gameNodes;
    private readonly List<EventInstance> _eventInstances = [];
    private readonly HashSet<string> _metricRefs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PerPlayerNodeTemplate.MaterializedPlayer> _players = [];
    private readonly Dictionary<int, Dictionary<StateNode, NodeSnapshot>> _rounds = [];
    private readonly StateNode? _roundNumber;
    private readonly HashSet<StateNode> _staticNodes = new(ReferenceEqualityComparer.Instance);

    // The nodes a sample captures: static metric nodes up front, per-player ones as they materialize.
    private readonly List<StateNode> _sampled = [];
    private readonly HashSet<StateNode> _sampledSet = new(ReferenceEqualityComparer.Instance);

    private Dictionary<StateNode, NodeSnapshot>? _final;
    private (int Round, Dictionary<StateNode, NodeSnapshot> Sample)? _pending;

    /// <summary>Creates a recorder for the state-sampled outputs of <paramref name="build" />.</summary>
    /// <param name="build">The build whose configured outputs the run will project.</param>
    public ConfiguredOutputRecorder(BuildResult build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _boundaryTypes = build.RoundBoundaryTypes;
        _roundNumber = build.GameNodesByRuleId?.GetValueOrDefault(StatValues.RoundNumberRuleId);
        _gameNodes = build.GameNodesByRuleId;

        foreach (StateNode node in build.Nodes)
        {
            _staticNodes.Add(node);
        }

        foreach (OutputDef output in build.Outputs ?? [])
        {
            if (output.Scope == OutputScope.PerEvent)
            {
                continue;
            }

            if (IsEventScope(output.Scope))
            {
                _eventOutputs.Add(output);
                TrackStaticEvents(output, build);
                continue;
            }

            foreach (MetricRef metric in output.Metrics)
            {
                _metricRefs.Add(metric.RuleRef);
                if (build.GameNodesByRuleId?.GetValueOrDefault(metric.RuleRef) is { } gameNode)
                {
                    Track(gameNode);
                }

                foreach (IReadOnlyDictionary<string, StateNode> side in build.TeamNodesByRuleId?.Values ?? [])
                {
                    if (side.GetValueOrDefault(metric.RuleRef) is { } teamNode)
                    {
                        Track(teamNode);
                    }
                }
            }
        }

        foreach (StateNode roster in build.TeamRosterNodes?.Values ?? [])
        {
            Track(roster);
        }
    }

    /// <summary>Whether <paramref name="scope" /> is one of the event-table scopes.</summary>
    internal static bool IsEventScope(OutputScope scope) =>
        scope is OutputScope.PerPlayerPerEvent or OutputScope.PerTeamPerEvent or OutputScope.PerMatchPerEvent;

    /// <summary>Whether a build has any output this recorder can project.</summary>
    public static bool Serves(BuildResult build) =>
        build.Outputs?.Any(o => o.Enabled && o.Scope != OutputScope.PerEvent) == true;

    /// <inheritdoc />
    public void BeforeMessage(Type dispatchKey)
    {
        if (_boundaryTypes.Contains(dispatchKey) && CurrentRound() is { } round and >= 1)
        {
            _pending = (round, Sample());
        }
    }

    /// <inheritdoc />
    public void AfterMessage(int tick)
    {
        RecordEvents(tick);
        if (_pending is not { } pending)
        {
            return;
        }

        _pending = null;
        if (CurrentRound() != pending.Round)
        {
            // Last-wins, like the snapshot rule's "last index holding round r".
            _rounds[pending.Round] = pending.Sample;
        }
    }

    /// <inheritdoc />
    public void OnMaterialized(PerPlayerNodeTemplate.MaterializedPlayer player)
    {
        _players.Add(player);
        foreach (StateNode node in player.Nodes)
        {
            // A snapshot run tracks every materialized node it does not exclude, and serves a row that
            // predates the column its at-materialization value.
            if (node is not ISnapshotExcludedNode)
            {
                _defaults.TryAdd(node, Capture(node));
            }
        }

        if (player.NodesByRuleId is { } byRuleId)
        {
            foreach (string metricRef in _metricRefs)
            {
                if (byRuleId.GetValueOrDefault(metricRef) is { } node)
                {
                    Track(node);
                }
            }

            foreach (OutputDef output in _eventOutputs)
            {
                if (output.Scope == OutputScope.PerPlayerPerEvent
                    && byRuleId.GetValueOrDefault(output.SequenceRef!) is { } sequence)
                {
                    AddEventInstance(sequence, output, byRuleId);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Finish()
    {
        _pending = null;
        _final = Sample();
        if (CurrentRound() is { } round and >= 1)
        {
            _rounds[round] = _final;
        }
    }

    /// <summary>The recorded source, once the run has finished.</summary>
    public ProjectionSource ToSource() =>
        new(
            _rounds.OrderBy(r => r.Key).Select(r => (r.Key, Reader(r.Value))).ToList(),
            _final is null ? null : Reader(_final),
            _players)
        {
            Events = _events
        };

    private void TrackStaticEvents(OutputDef output, BuildResult build)
    {
        if (output.SequenceRef is not { } sequenceRef)
        {
            return;
        }

        if (output.Scope == OutputScope.PerMatchPerEvent
            && _gameNodes?.GetValueOrDefault(sequenceRef) is { } matchSequence)
        {
            AddEventInstance(matchSequence, output, null);
        }
        else if (output.Scope == OutputScope.PerTeamPerEvent && build.TeamNodesByRuleId is { } teams)
        {
            foreach (int side in (int[])[2, 3])
            {
                if (teams.GetValueOrDefault(side) is { } sideNodes
                    && sideNodes.GetValueOrDefault(sequenceRef) is { } sideSequence)
                {
                    AddEventInstance(sideSequence, output, sideNodes);
                }
            }
        }
    }

    private void AddEventInstance(StateNode sequence, OutputDef output,
        IReadOnlyDictionary<string, StateNode>? instanceNodes)
    {
        List<StateNode> columns = [];
        foreach (MetricRef metric in output.Metrics)
        {
            StateNode? node = instanceNodes?.GetValueOrDefault(metric.RuleRef) ?? _gameNodes?.GetValueOrDefault(metric.RuleRef);
            // A node a snapshot run does not track reads null there, so it is never captured here.
            if (node is not null && (_staticNodes.Contains(node) || node is not ISnapshotExcludedNode)
                && !columns.Contains(node))
            {
                columns.Add(node);
            }
        }

        _eventInstances.Add(new EventInstance(sequence, [.. columns]));
    }

    // One row per increase, so a match restart's reset to zero emits nothing.
    private void RecordEvents(int tick)
    {
        for (int i = 0; i < _eventInstances.Count; i++)
        {
            EventInstance instance = _eventInstances[i];
            double value = instance.Sequence.GetNumericValue() ?? 0;
            if (value <= instance.Last)
            {
                instance.Last = value;
                continue;
            }

            instance.Last = value;
            StateNode[] columns = instance.Columns;
            NodeSnapshot[] cells = new NodeSnapshot[columns.Length];
            for (int c = 0; c < columns.Length; c++)
            {
                cells[c] = Capture(columns[c]);
            }

            _events.Add(new EventRow(instance.Sequence, tick, CurrentRound() ?? 0, node =>
            {
                int c = Array.IndexOf(columns, node);
                return c >= 0 ? StatValues.ReadSnapshotValue(cells[c], node) : null;
            }));
        }
    }

    private Func<StateNode, object?> Reader(Dictionary<StateNode, NodeSnapshot> sample) =>
        node =>
        {
            if (sample.TryGetValue(node, out NodeSnapshot snap)
                || _defaults.TryGetValue(node, out snap))
            {
                return StatValues.ReadSnapshotValue(snap, node);
            }

            return null;
        };

    private void Track(StateNode node)
    {
        // A node a snapshot run would not track reads null there, so it is never sampled here.
        bool tracked = _staticNodes.Contains(node) || node is not ISnapshotExcludedNode;
        if (tracked && _sampledSet.Add(node))
        {
            _sampled.Add(node);
        }
    }

    private Dictionary<StateNode, NodeSnapshot> Sample()
    {
        Dictionary<StateNode, NodeSnapshot> sample = new(_sampled.Count, ReferenceEqualityComparer.Instance);
        foreach (StateNode node in _sampled)
        {
            sample[node] = Capture(node);
        }

        return sample;
    }

    private static NodeSnapshot Capture(StateNode node) =>
        new(node.IsActive, node.GetDisplayValue(), node.GetNumericValue());

    // The live round number the snapshot rule keys on: null when the node is absent or inactive.
    private int? CurrentRound()
    {
        if (_roundNumber is not { IsActive: true } node)
        {
            return null;
        }

        return node.GetNumericValue() is { } numeric ? (int)numeric : null;
    }

    private sealed class EventInstance(StateNode sequence, StateNode[] columns)
    {
        public StateNode Sequence { get; } = sequence;

        public StateNode[] Columns { get; } = columns;

        public double Last { get; set; }
    }
}
