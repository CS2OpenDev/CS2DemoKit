#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     What a traversal reports about its own work, threaded through <see cref="TriangleBvh" /> as a
///     struct type parameter so the production path pays nothing for it: the JIT specialises the
///     traversal per implementation, and <see cref="NoStats" /> compiles to no code at all. The
///     bench's rays verb and the pop-order test use the counting and tracing implementations to
///     explain a speedup rather than assert one.
/// </summary>
internal interface ITraversalStats
{
    /// <summary>An inner node was popped and its eight lane boxes were tested.</summary>
    /// <param name="node">The node's index; the root is 0.</param>
    void VisitNode(int node);

    /// <summary>
    ///     A lane passed the slab test and its reference was pushed. Every lane entered is a real
    ///     one; the empty-lane test records these to prove it without relying on a Debug assertion.
    /// </summary>
    /// <param name="lane">The lane index, <c>node * 8 + lane</c>.</param>
    void EnterLane(int lane);

    /// <summary>
    ///     One Moller-Trumbore evaluation against the triangle in this leaf slot. The slot, not the
    ///     caller-facing index, so the production policy costs the hot loop nothing at all; a
    ///     recording policy maps it through <see cref="TriangleBvh.TriangleOfSlot" /> afterwards.
    /// </summary>
    /// <param name="slot">The leaf slot tested.</param>
    void TestSlot(int slot);
}

/// <summary>The production policy: every call is empty and inlines away.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Justification = "A stateless policy type; equality is meaningless.")]
internal struct NoStats : ITraversalStats
{
    /// <inheritdoc />
    public void VisitNode(int node)
    {
    }

    /// <inheritdoc />
    public void EnterLane(int lane)
    {
    }

    /// <inheritdoc />
    public void TestSlot(int slot)
    {
    }
}

/// <summary>Tallies nodes visited and triangles tested across every ray it is threaded through.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Justification = "A mutable tally passed by reference; equality is meaningless.")]
internal struct TraversalCounters : ITraversalStats
{
    /// <summary>Inner nodes popped, each costing eight lane box tests.</summary>
    public long Nodes;

    /// <summary>Moller-Trumbore evaluations.</summary>
    public long Triangles;

    /// <inheritdoc />
    public void VisitNode(int node) => Nodes++;

    /// <inheritdoc />
    public void EnterLane(int lane)
    {
    }

    /// <inheritdoc />
    public void TestSlot(int slot) => Triangles++;
}

/// <summary>Records the order in which nodes were visited and triangles tested, for tests that pin traversal order.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Justification = "A recording policy passed by reference; equality is meaningless.")]
internal struct TraversalTrace : ITraversalStats
{
    /// <summary>Leaf slots in the order they were tested; <see cref="TriangleBvh.TriangleOfSlot" /> names the triangles.</summary>
    public List<int> Slots;

    /// <summary>Node indices in the order they were popped.</summary>
    public List<int> Nodes;

    /// <summary>Lane indices (<c>node * 8 + lane</c>) in the order their references were pushed.</summary>
    public List<int> Lanes;

    /// <summary>A trace with empty lists.</summary>
    public static TraversalTrace Create() => new() { Slots = [], Nodes = [], Lanes = [] };

    /// <inheritdoc />
    public void VisitNode(int node) => Nodes.Add(node);

    /// <inheritdoc />
    public void EnterLane(int lane) => Lanes.Add(lane);

    /// <inheritdoc />
    public void TestSlot(int slot) => Slots.Add(slot);
}
