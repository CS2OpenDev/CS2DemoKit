#region

using CS2DemoKit.Parser.Entities;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Per-field encoding parameters extracted from <c>ProtoFlattenedSerializerField_t</c>.
///     Passed to decoder factories to configure the exact bit-level read strategy.
///     Adapted from demofile-net (MIT).
/// </summary>
public readonly record struct FieldEncodingInfo(
    string? VarEncoder,
    int BitCount,
    int EncodeFlags,
    float? LowValue,
    float? HighValue)
{
    /// <summary>Extracts the encoding metadata fields from a <see cref="RuntimeField" /> into this record.</summary>
    public static FieldEncodingInfo From(RuntimeField field) => new(
        field.Encoder,
        field.BitCount,
        field.EncodeFlags,
        field.LowValue,
        field.HighValue);
}
