#region

using System.Diagnostics.Tracing;

#endregion

namespace CS2DemoKit.Analysis.Diagnostics;

[EventSource(Name = "CS2DemoKit.Analysis.Evaluator")]
internal sealed class EvaluatorEventSource : EventSource
{
    /// <summary>Singleton instance used by the evaluator for ETW / .NET EventListener tracing.</summary>
    public static readonly EvaluatorEventSource Log = new();

    /// <summary>Traces dispatch-slot ordering: emitted when the evaluator finishes its topological sort for an event type.</summary>
    [Event(11, Level = EventLevel.Informational)]
    public void DispatchSlotSorted(string eventType, int edgeCount, int dependencyCount) =>
        WriteEvent(11, eventType, edgeCount, dependencyCount);

    /// <summary>Verbose trace: one record per edge evaluation with source-active and applied flags.</summary>
    [Event(5, Level = EventLevel.Verbose)]
    public void EdgeEvaluated(int edgeId, bool sourceActive, bool applied) =>
        WriteEvent(5, edgeId, sourceActive, applied);

    /// <summary>Emitted once per edge at evaluator construction.</summary>
    [Event(2, Level = EventLevel.Informational, Message = "Edge registered: {1} → {2}")]
    public void EdgeRegistered(int edgeId, string sourceName, string destName, string eventType) =>
        WriteEvent(2, edgeId, sourceName, destName, eventType);

    /// <summary>Emitted at the end of the evaluator's full run with aggregate counters.</summary>
    [Event(9, Level = EventLevel.Informational)]
    public void EvaluationCompleted(int messagesProcessed, int totalEdgesFired, long elapsedTicks) =>
        WriteEvent(9, messagesProcessed, totalEdgesFired, elapsedTicks);

    /// <summary>Emitted at the start of a run with the graph dimensions.</summary>
    [Event(8, Level = EventLevel.Informational)]
    public void EvaluationStarted(int frameCount, int edgeCount, int nodeCount) =>
        WriteEvent(8, frameCount, edgeCount, nodeCount);

    /// <summary>Emitted once per processed demo frame.</summary>
    [Event(3, Level = EventLevel.Informational)]
    public void FrameProcessed(int frameIndex, int messageCount, long elapsedTicks) =>
        WriteEvent(3, frameIndex, messageCount, elapsedTicks);

    /// <summary>Verbose trace: emitted when a conjunction/disjunction recomputes.</summary>
    [Event(6, Level = EventLevel.Verbose)]
    public void LogicNodeRecomputed(int nodeId, bool satisfied, bool risingEdge) =>
        WriteEvent(6, nodeId, satisfied, risingEdge);

    /// <summary>Emitted once per dispatched message with per-message counters.</summary>
    [Event(4, Level = EventLevel.Informational)]
    public void MessageProcessed(int frameIndex, string messageType,
        int edgesEvaluated, int edgesFired, int logicNodesRecomputed, long elapsedTicks) =>
        WriteEvent(4, frameIndex, messageType, edgesEvaluated, edgesFired, logicNodesRecomputed, elapsedTicks);

    /// <summary>Emitted once per node at evaluator construction.</summary>
    [Event(1, Level = EventLevel.Informational, Message = "Node registered: {1}")]
    public void NodeRegistered(int nodeId, string name, string nodeType) =>
        WriteEvent(1, nodeId, name, nodeType);

    /// <summary>Emitted when a per-player template is materialized for a newly-discovered slot.</summary>
    [Event(7, Level = EventLevel.Informational)]
    public void PlayerMaterialized(int playerSlot, string playerName, int templateIndex) =>
        WriteEvent(7, playerSlot, playerName, templateIndex);

    /// <summary>Emitted at the start of each round when round-scoped nodes are reset.</summary>
    [Event(10, Level = EventLevel.Informational)]
    public void RoundReset(int roundScopedNodeCount) =>
        WriteEvent(10, roundScopedNodeCount);

    /// <summary>Warning trace: emitted when an edge writes a node without declaring its effect type.</summary>
    [Event(12, Level = EventLevel.Warning)]
    public void UndeclaredEdgeEffect(string sourceName, string writtenNodeName) =>
        WriteEvent(12, sourceName, writtenNodeName);
}
