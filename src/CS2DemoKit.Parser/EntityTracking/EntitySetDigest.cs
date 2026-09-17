#region

using System.Globalization;
using System.Numerics;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     A stable digest of an entity set: FNV-1a 64 over every live slot in ascending index order,
///     folding index, class, serial, PVS state, then each field by ordinal key with a canonical
///     value text. Two replays that agree here hold the same world; the digest is what a test or a
///     benchmark compares when it claims two decode paths are identical.
/// </summary>
public static class EntitySetDigest
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static ulong Compute(EntitySet entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ulong hash = FnvOffsetBasis;
        foreach ((int index, EntityState entity) in entities.AllIndexed())
        {
            Mix(ref hash, index.ToString(CultureInfo.InvariantCulture));
            Mix(ref hash, entity.ClassName);
            Mix(ref hash, entity.Serial.ToString(CultureInfo.InvariantCulture));
            Mix(ref hash, entity.IsInPvs ? "1" : "0");
            foreach (KeyValuePair<string, object?> field in entity.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                Mix(ref hash, field.Key);
                Mix(ref hash, Canonical(field.Value));
            }
        }

        return hash;
    }

    /// <summary>Text that distinguishes every representable value; floats by their bits, not their rounding.</summary>
    public static string Canonical(object? value) => value switch
    {
        null => "~null",
        float f => "f" + BitConverter.SingleToInt32Bits(f).ToString(CultureInfo.InvariantCulture),
        double d => "d" + BitConverter.DoubleToInt64Bits(d).ToString(CultureInfo.InvariantCulture),
        bool b => b ? "T" : "F",
        string s => "s" + s,
        Vector3 v => "v" + BitConverter.SingleToInt32Bits(v.X).ToString(CultureInfo.InvariantCulture)
                         + "," + BitConverter.SingleToInt32Bits(v.Y).ToString(CultureInfo.InvariantCulture)
                         + "," + BitConverter.SingleToInt32Bits(v.Z).ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static void Mix(ref ulong hash, string text)
    {
        foreach (char c in text)
        {
            hash = (hash ^ (byte)(c & 0xFF)) * FnvPrime;
            hash = (hash ^ (byte)(c >> 8)) * FnvPrime;
        }

        // Field separator, so "ab" + "c" and "a" + "bc" stay distinct.
        hash = (hash ^ 0xFFu) * FnvPrime;
    }
}
