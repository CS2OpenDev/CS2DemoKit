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

/// <summary>A typed decoder for the three-component vector and angle types; nothing on its path boxes.</summary>
internal delegate System.Numerics.Vector3 Vector3Decoder(ref BitBuffer buffer);

/// <summary>A typed decoder for 64-bit unsigned fields; nothing on its path boxes.</summary>
internal delegate ulong UInt64Decoder(ref BitBuffer buffer);
