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

/// <summary>
///     Records the order in which nodes were visited and triangles tested, for tests that pin traversal order.
///     <para>
///         It is a struct only because the traversal takes its policy as a struct type parameter, and
///         the lists are therefore held by a value that <c>default</c> can produce without them. Such
///         a value records nothing, so every member on it says that instead of dereferencing null:
///         <see cref="Create" /> is the only way to get a usable trace.
///     </para>
/// </summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
    Justification = "A recording policy passed by reference; equality is meaningless.")]
internal struct TraversalTrace : ITraversalStats
{
    private List<int>? _slots;
    private List<int>? _nodes;
    private List<int>? _lanes;

    /// <summary>Leaf slots in the order they were tested; <see cref="TriangleBvh.TriangleOfSlot" /> names the triangles.</summary>
    public readonly List<int> Slots => Recording(_slots);

    /// <summary>Node indices in the order they were popped.</summary>
    public readonly List<int> Nodes => Recording(_nodes);

    /// <summary>Lane indices (<c>node * 8 + lane</c>) in the order their references were pushed.</summary>
    public readonly List<int> Lanes => Recording(_lanes);

    /// <summary>A trace with empty lists.</summary>
    public static TraversalTrace Create() => new() { _slots = [], _nodes = [], _lanes = [] };

    /// <inheritdoc />
    public readonly void VisitNode(int node) => Recording(_nodes).Add(node);

    /// <inheritdoc />
    public readonly void EnterLane(int lane) => Recording(_lanes).Add(lane);

    /// <inheritdoc />
    public readonly void TestSlot(int slot) => Recording(_slots).Add(slot);

    private static List<int> Recording(List<int>? list) =>
        list ?? throw new InvalidOperationException(
            $"a default {nameof(TraversalTrace)} has no lists to record into; take one from "
            + $"{nameof(TraversalTrace)}.{nameof(Create)}()");
}
