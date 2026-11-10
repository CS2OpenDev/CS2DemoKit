#region

using CS2DemoKit.Analysis.Rules;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The Catalog friendly-type-string → <see cref="RulesType" /> mapping of the scope-environment
///     adapter. The generator emits <c>v2Type</c> alongside every event field,
///     provider, and context, but the adapter re-derives from the friendly type so an unknown type
///     string is a <b>loud build error, never a silent skip</b> — the catalog cannot drift a field
///     into an untyped state without failing the load. There is no unsigned language type (spec
///     §3.2): unsigned wire fields present as <see cref="RulesTypeKind.Int" />.
/// </summary>
public static class FriendlyTypeMap
{
    /// <summary>
    ///     The friendly types a provider may declare and a rule read without loss: <see cref="Map" />'s set
    ///     minus the 64-bit and unsigned integers. Those stay in the map because event fields carry them
    ///     (an unsigned field is a pawn handle, kept apart from slots), but a node read does not widen them
    ///     to int, so they neither compare with int literals nor divide safely.
    /// </summary>
    public static IReadOnlySet<string> ProviderReadableTypes { get; } = new HashSet<string>(
        ["int", "byte", "sbyte", "short", "ushort", "float", "double", "bool", "string"], StringComparer.Ordinal);

    /// <summary>Whether <paramref name="friendlyType" /> is an integer width <see cref="Map" /> accepts.</summary>
    public static bool IsIntegerWidth(string friendlyType) => friendlyType is "int" or "uint" or "ulong" or "long"
        or "byte" or "sbyte" or "short" or "ushort";

    /// <summary>
    ///     The friendly type name the catalog writes for <paramref name="type" />, or null when the type is
    ///     outside the set <see cref="Map" /> accepts. The generator and catalog extension rows both name
    ///     types through this, so the two cannot disagree.
    /// </summary>
    /// <param name="type">A provider's or event field's CLR type.</param>
    /// <returns>The friendly name, or null.</returns>
    public static string? FriendlyName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            _ when type == typeof(int) => "int",
            // The SDK's event records type each field to its KV1 tag rather than widening everything
            // to int, so byte/sbyte/short/ushort reach the generator.
            _ when type == typeof(byte) => "byte",
            _ when type == typeof(sbyte) => "sbyte",
            _ when type == typeof(short) => "short",
            _ when type == typeof(ushort) => "ushort",
            _ when type == typeof(long) => "long",
            _ when type == typeof(ulong) => "ulong",
            _ when type == typeof(uint) => "uint",
            _ when type == typeof(float) => "float",
            _ when type == typeof(double) => "double",
            _ when type == typeof(bool) => "bool",
            _ when type == typeof(string) => "string",
            _ => null
        };
    }

    /// <summary>Maps a Catalog friendly type name to its language-level <see cref="RulesType" />.</summary>
    /// <param name="friendlyType">
    ///     The friendly type as the generator wrote it (<c>bool</c>, <c>int</c>, <c>uint</c>,
    ///     <c>ulong</c>, <c>long</c>, <c>float</c>, <c>double</c>, <c>string</c>).
    /// </param>
    /// <returns>The corresponding <see cref="RulesType" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="friendlyType" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The friendly type is outside the closed set — a generator/build error, surfaced loudly
    ///     so the catalog can never silently ship an untypable field.
    /// </exception>
    public static RulesType Map(string friendlyType)
    {
        ArgumentNullException.ThrowIfNull(friendlyType);
        return friendlyType switch
        {
            "bool" => RulesType.Bool,
            // byte/sbyte/short/ushort appear because the SDK's event records type each field to
            // its KV1 wire tag instead of widening everything to int the way our retired generator
            // did. v2 has one integer type (spec §3.2), so every width collapses to Int.
            "int" or "uint" or "ulong" or "long"
                or "byte" or "sbyte" or "short" or "ushort" => RulesType.Int,
            "float" or "double" => RulesType.Float,
            "string" => RulesType.String,
            _ => throw new InvalidOperationException(
                $"catalog friendly type '{friendlyType}' has no v2 RulesType mapping — "
                + "the scope-environment adapter maps only bool/int/uint/ulong/long/byte/sbyte/short/ushort/float/double/string "
                + "(spec §3.2: no unsigned language type). This is a generator/catalog error.")
        };
    }
}
