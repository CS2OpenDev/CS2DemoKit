#region

using System.Runtime.CompilerServices;

#endregion

// The Track-4 parallel-decode internals (EntityFrameDigest, EntityDigestExtractor,
// ParallelDigestProducer) are implementation details of the entity scanner. The
// digest-equivalence gate test reaches into them directly to prove the parallel digest is
// element-wise identical to the sequential one, so it needs the friend grant.
[assembly: InternalsVisibleTo("CS2DemoKit.Analysis.Tests")]
