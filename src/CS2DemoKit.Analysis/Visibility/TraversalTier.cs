namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     The traversal implementations of <see cref="TriangleBvh" />. They differ only in how a
///     node's eight lane boxes go through the slab test: one lane at a time, four at a time or all
///     eight at once. The scalar tier is the reference: every other tier is held to it verdict for
///     verdict, hint for hint and distance for distance over a large real-bake corpus
///     (<c>TraversalTierTests</c>), which is what lets the wide tree be offered on every machine
///     rather than only where a particular vector width exists. Production runs the widest tier the
///     machine accelerates (<see cref="TriangleBvh.DefaultTier" />); the named-tier entry points
///     exist for that test and for the bench's rays verb.
/// </summary>
internal enum TraversalTier
{
    /// <summary>One lane at a time, explicit compare-select, no intrinsics. The reference.</summary>
    Scalar,

    /// <summary>Four lanes per register, a node as two halves. What SSE and NEON machines run.</summary>
    Vector128,

    /// <summary>
    ///     Eight lanes per register, a node as one. What AVX2 machines run: the runtime reports
    ///     256-bit registers as accelerated only with AVX2, so a machine with AVX alone runs
    ///     <see cref="Vector128" /> even though this tier uses only AVX-level float operations.
    /// </summary>
    Vector256
}
