namespace CS2DemoKit.Parser.Entities;

// Adapted from demofile-net (MIT): https://github.com/saul/demofile-net
// Changes: namespace; FieldShape and the field-shape classification are ours; the field
//          metadata model and its encoder/serializer wiring follow demofile-net.

/// <summary>
///     Wire-level shape of a serialized field. Determines how a length-1 path on the wire
///     consumes bits when it lands on this field's slot.
/// </summary>
#pragma warning disable CA1720 // "Ptr" matches demofile-net's terminology and the schema's SchemaTypeCategory
public enum FieldShape
{
    /// <summary>Leaf value (int, float, string, vector, etc.). Length-1 path = atomic decode.</summary>
    Atomic,

    /// <summary>
    ///     Pointer-to-class (e.g. <c>CCSPlayerPawn*</c>). Length-1 path on the wire consumes
    ///     <b>1 bit</b> (isSet — null vs allocated). Length-2+ paths recurse into the inner.
    /// </summary>
    Ptr,

    /// <summary>
    ///     Polymorphic pointer-to-class (<c>MNetworkPolymorphic</c> attribute). Length-1 path
    ///     consumes <b>1 bit (isSet) + UBitVar (child class id)</b>. Length-2+ paths recurse.
    /// </summary>
    PolymorphicPtr,

    /// <summary>
    ///     Variable-length collection (<c>CNetworkUtlVectorBase&lt;T&gt;</c>, <c>CUtlVector&lt;T&gt;</c>,
    ///     <c>T[]</c>). Length-1 path consumes a <b>UVarInt32 resize</b>. Length-2+ paths index
    ///     into an element.
    /// </summary>
    Vector,

    /// <summary>
    ///     Fixed-size array (<c>T[N]</c>). Length-1 paths are unreachable on the wire — element
    ///     access always uses <c>path.Length == 2</c> with <c>path[1]</c> as the index.
    /// </summary>
    FixedArray,

    /// <summary>
    ///     Plain non-Ptr nested struct. demofile-net flattens these via SendNode at codegen time,
    ///     so length-1 paths landing on a PlainStruct slot are not expected on the wire. Logged
    ///     defensively.
    /// </summary>
    PlainStruct
}
#pragma warning restore CA1720

/// <summary>
///     One field within a <see cref="RuntimeSerializer" />. Carries all encoding metadata needed
///     to build a decoder and to reconstruct the field path during entity delta decoding.
/// </summary>
public sealed class RuntimeField
{
    internal RuntimeField(
        string name, string typeName, string? encoder,
        int bitCount, float? lowValue, float? highValue,
        int encodeFlags,
        string? childSerializerName, int childSerializerVersion,
        string[] sendNode,
        IReadOnlyList<(string Name, int Version)> polymorphicTypes,
        string? varSerializerName)
    {
        Name = name;
        TypeName = typeName;
        Encoder = encoder;
        BitCount = bitCount;
        LowValue = lowValue;
        HighValue = highValue;
        EncodeFlags = encodeFlags;
        ChildSerializerName = childSerializerName;
        ChildSerializerVersion = childSerializerVersion;
        SendNode = sendNode;
        PolymorphicTypes = polymorphicTypes;
        VarSerializerName = varSerializerName;
        Shape = ComputeShape(typeName, childSerializerName, polymorphicTypes.Count);
    }

    /// <summary>Bit-count hint from the schema; the field decoder uses this when the wire format is fixed-width.</summary>
    public int BitCount { get; }

    /// <summary>Non-null when this field embeds another serialized entity type.</summary>
    public RuntimeSerializer? ChildSerializer { get; private set; }

    // Held only until ResolveChildSerializer is called
    internal string? ChildSerializerName { get; }
    internal int ChildSerializerVersion { get; }

    /// <summary>Bitmask of <c>FieldEncodeFlags</c> values from the schema (e.g. <c>UnsignedInteger</c>, <c>Coord</c>).</summary>
    public int EncodeFlags { get; }

    /// <summary>Optional encoder name from the schema (e.g. <c>simtime</c>, <c>fixed64</c>); selects a custom decoder.</summary>
    public string? Encoder { get; }

    /// <summary>For ranged encoders: upper bound of the value range, or <c>null</c> when unset.</summary>
    public float? HighValue { get; }

    // Derived helper

    /// <summary>True when the declared type is a variable-length collection (array, <c>CUtlVector&lt;T&gt;</c>, etc.).</summary>
    public bool IsArray => TypeName.EndsWith("[]", StringComparison.Ordinal)
                           || TypeName.StartsWith("CNetworkUtlVectorBase<", StringComparison.Ordinal)
                           || TypeName.StartsWith("CUtlVector<", StringComparison.Ordinal)
                           || TypeName.StartsWith("CUtlVectorEmbeddedNetworkVar<", StringComparison.Ordinal);

    /// <summary>True when the declared type is a fixed-size array <c>T[N]</c> (not a variable vector).</summary>
    public bool IsFixedArray => TypeName.Contains('[') && !IsArray;

    /// <summary>For ranged encoders: lower bound of the value range, or <c>null</c> when unset.</summary>
    public float? LowValue { get; }

    /// <summary>The field's name as declared on the source schema entity.</summary>
    public string Name { get; }

    /// <summary>
    ///     Non-empty when this field is polymorphic (<c>MNetworkPolymorphic</c>). The list
    ///     enumerates the candidate child serializers; on the wire the child class id (UBitVar)
    ///     indexes into this list.
    /// </summary>
    public IReadOnlyList<(string Name, int Version)> PolymorphicTypes { get; }

    /// <summary>
    ///     Dot-separated descent prefix from the proto's <c>send_node_sym</c>. Empty array when
    ///     unset or "(root)". demofile-net uses this to flatten plain non-Ptr nested structs at
    ///     codegen time; we keep it for diagnostics and possible future use.
    /// </summary>
    public string[] SendNode { get; }

    /// <summary>
    ///     Wire-level shape: classifies how a length-1 path on the wire consumes bits when it
    ///     lands on this field's slot. Computed once at construction from the field metadata.
    /// </summary>
    public FieldShape Shape { get; }

    /// <summary>
    ///     The field's declared CLR-style type name from the schema (e.g. <c>int32</c>, <c>CHandle&lt;CBaseEntity&gt;</c>
    ///     ).
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    ///     Optional secondary serializer name (proto <c>var_serializer_sym</c>). Rarely set;
    ///     used when the field's serializer differs from the implicit one derived from type.
    /// </summary>
    public string? VarSerializerName { get; }

    /// <inheritdoc />
    public override string ToString() => $"{TypeName} {Name}";

    internal void ResolveChildSerializer(IReadOnlyDictionary<(string, int), RuntimeSerializer> all)
    {
        if (ChildSerializerName is null)
        {
            return;
        }

        all.TryGetValue((ChildSerializerName, ChildSerializerVersion), out RuntimeSerializer? ser);
        ChildSerializer = ser;
    }

    private static FieldShape ComputeShape(string typeName, string? childSerializerName, int polymorphicCount)
    {
        // Polymorphic before Ptr: a polymorphic pointer also matches `EndsWith("*")` on outer
        // type, but its wire encoding has the extra UBitVar child-class-id and must take priority.
        if (polymorphicCount > 0)
        {
            return FieldShape.PolymorphicPtr;
        }

        // Outer pointer suffix: a generic parameter's `*` is inside `<>` so it can't trip this.
        if (typeName.EndsWith('*'))
        {
            return FieldShape.Ptr;
        }

        bool isArray = typeName.EndsWith("[]", StringComparison.Ordinal)
                       || typeName.StartsWith("CNetworkUtlVectorBase<", StringComparison.Ordinal)
                       || typeName.StartsWith("CUtlVector<", StringComparison.Ordinal)
                       || typeName.StartsWith("CUtlVectorEmbeddedNetworkVar<", StringComparison.Ordinal);
        if (isArray)
        {
            return FieldShape.Vector;
        }

        bool isFixedArray = typeName.Contains('[') && !isArray;
        if (isFixedArray)
        {
            return FieldShape.FixedArray;
        }

        // Wire-level proto only emits `field_serializer_name_sym` for nested types that the
        // server has NOT inlined via SendNode. A non-array, non-`*` declared-class field with
        // a child serializer is, on the wire, encoded as a Ptr (length-1 path = 1 isSet bit;
        // length-2+ recurses). Type names like "CBodyComponent" come through without a `*`
        // suffix despite being pointers in the source schema — the proto strips it.
        if (childSerializerName is not null)
        {
            return FieldShape.Ptr;
        }

        return FieldShape.Atomic;
    }
}
