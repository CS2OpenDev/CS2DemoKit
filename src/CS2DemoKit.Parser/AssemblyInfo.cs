#region

using System.Runtime.CompilerServices;

#endregion

// Friend grants for the merged parser assembly (parse pipeline + entity tracking + typed
// entity wrappers). FieldPath / FieldPathReader / HuffmanNode and the RuntimeField internal
// ctor are implementation details of the decoder; the test assemblies reach into them
// directly rather than widening the public surface for everyone.
[assembly: InternalsVisibleTo("CS2DemoKit.Parser.Tests")]
[assembly: InternalsVisibleTo("CS2DemoKit.Analysis.Tests")]
// The codegen tool derives the LensState from the SDK package and stamps its CanonicalHash
// (internal setter) before emitting the generated registry.
[assembly: InternalsVisibleTo("CS2DemoKit.Codegen")]
