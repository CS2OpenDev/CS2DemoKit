namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     A function that reads a single field value from the entity bit stream.
///     Returns a boxed value; used for complex types (Vectors, strings, enums).
/// </summary>
internal delegate object? FieldDecoder(ref BitBuffer buffer);

/// <summary>Typed decoder for dominant integer scalar fields — avoids boxing on the hot entity-decode path.</summary>
internal delegate int IntDecoder(ref BitBuffer buffer);

/// <summary>Typed decoder for dominant float scalar fields — avoids boxing on the hot entity-decode path.</summary>
internal delegate float FloatDecoder(ref BitBuffer buffer);
