#region

using System.Runtime.CompilerServices;

#endregion

// The Track-4 parallel-decode internals (EntityFrameDigest, EntityDigestExtractor,
// ParallelDigestProducer) are implementation details of the entity scanner. The
// digest-equivalence gate test reaches into them directly to prove the parallel digest is
// element-wise identical to the sequential one, so it needs the friend grant.
[assembly: InternalsVisibleTo("CS2DemoKit.Analysis.Tests")]

// The bench's rays verb reads the BVH traversal's work counters (nodes popped, triangles tested)
// so a throughput change can be explained by the work behind it. Measurement only; the counters
// are threaded through the traversal as a struct policy the production path never instantiates.
[assembly: InternalsVisibleTo("CS2DemoKit.Bench")]
