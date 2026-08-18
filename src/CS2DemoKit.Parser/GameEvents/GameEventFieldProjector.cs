#region

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

using CS2OpenSchema;

#endregion

namespace CS2DemoKit.Parser.GameEvents;

/// <summary>
///     Projects an SDK event record into the flat (name, value, wire type) tuples the UI and the
///     rules layer consume.
/// </summary>
/// <remarks>
///     <para>
///         This replaces 272 generated <c>GetDecodedFields()</c> overrides. The generator emitted
///         one per event because it already knew each record's shape at build time; reading it back
///         off the SDK's records needs reflection, but only once per type — the accessor list is
///         built on first sight of a type and cached for the process.
///     </para>
///     <para>
///         Not on a hot path: nothing calls this during parsing. It runs when the Parser tab
///         renders a message, when the Analysis message view materialises, and when the rules
///         catalog is generated.
///     </para>
///     <para>
///         The wire type comes from the SDK's <c>[GameEventFieldType]</c> attribute, which carries
///         the original KV1 tag (<c>bool</c>, <c>short</c>, <c>ehandle</c>,
///         <c>player_controller_and_pawn</c>, …). That is strictly better than inferring it from
///         the CLR type: <c>player_controller_and_pawn</c> and a plain <c>short</c> are both
///         <see cref="short" /> once materialised, and only the tag tells them apart.
///     </para>
/// </remarks>
internal static class GameEventFieldProjector
{
    private static readonly ConcurrentDictionary<Type, Accessor[]> _cache = new();

    /// <summary>Project a payload record, or an empty list when there is no payload.</summary>
    public static IReadOnlyList<(string Name, string Value, string WireType)> Project(object? payload)
    {
        if (payload is null)
        {
            return [];
        }

        Accessor[] accessors = _cache.GetOrAdd(payload.GetType(), BuildAccessors);
        if (accessors.Length == 0)
        {
            return [];
        }

        var fields = new (string, string, string)[accessors.Length];
        for (var i = 0; i < accessors.Length; i++)
        {
            Accessor a = accessors[i];
            fields[i] = (a.Name, Format(a.Get(payload)), a.WireType);
        }

        return fields;
    }

    /// <summary>
    ///     Declared property order is the emission order — the SDK emits alphabetised and stable,
    ///     so this is deterministic without sorting here.
    /// </summary>
    private static Accessor[] BuildAccessors(Type type)
    {
        PropertyInfo[] props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var list = new List<Accessor>(props.Length);

        foreach (PropertyInfo p in props)
        {
            // `EqualityContract` is compiler-generated on every record and is not a field.
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || p.Name == "EqualityContract")
            {
                continue;
            }

            string wireType = p.GetCustomAttribute<GameEventFieldTypeAttribute>()?.TypeTag
                              ?? InferWireType(p.PropertyType);

            list.Add(new Accessor(p.Name, wireType, p.GetValue));
        }

        return [.. list];
    }

    /// <summary>Fallback for a property with no KV1 tag — synthesized or hand-authored records.</summary>
    private static string InferWireType(Type t) => Type.GetTypeCode(t) switch
    {
        TypeCode.Boolean => "bool",
        TypeCode.Single or TypeCode.Double => "float",
        TypeCode.String => "string",
        TypeCode.UInt64 => "uint64",
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
            or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 => "int",
        _ => ""
    };

    /// <summary>
    ///     Formats exactly as the retired generated overrides did — strings quoted, bools
    ///     True/False, floats "G" invariant — so the UI and the catalog see no change in value
    ///     rendering, only in field naming.
    /// </summary>
    private static string Format(object? value) => value switch
    {
        null => "",
        bool b => b ? "True" : "False",
        float f => f.ToString("G", CultureInfo.InvariantCulture),
        double d => d.ToString("G", CultureInfo.InvariantCulture),
        string s => $"\"{s}\"",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private sealed record Accessor(string Name, string WireType, Func<object?, object?> Get);
}
