#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace CS2DemoKit.Analysis.Rules;

/// <summary>
///     The spec §3.1 type vocabulary. <see cref="Duration" /> and <see cref="Instant" /> are
///     checker-level types (both are int ticks at runtime); <see cref="List" /> and
///     <see cref="Map" /> are container kinds whose element kind lives in
///     <see cref="RulesType.ElementKind" />.
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "These are the published spec §3.1 language type names (int/float/string); diagnostics must render them verbatim.")]
public enum RulesTypeKind
{
    /// <summary>Unknown / error type. Produced only while recovering from a checker error; never a valid slot type.</summary>
    None = 0,

    /// <summary>The type of the <c>null</c> literal (the missing value, spec §3.3).</summary>
    Null,

    /// <summary><c>true</c> / <c>false</c>: flags, comparisons, <c>.set</c>.</summary>
    Bool,

    /// <summary>64-bit signed integer: counters, sums, slots, ticks, most event fields.</summary>
    Int,

    /// <summary>IEEE double: quantized floats, <c>compute:</c> results.</summary>
    Float,

    /// <summary>UTF-8 string: names, weapon classes, bucket keys.</summary>
    String,

    /// <summary>A tick count (int at runtime): duration literals, instant − instant.</summary>
    Duration,

    /// <summary>A tick position (int at runtime): <c>event.tick</c>, <c>match.tick</c>, captures thereof.</summary>
    Instant,

    /// <summary>Immutable list of a scalar element kind: <c>keep: list</c> captures, <c>define:</c> lists.</summary>
    List,

    /// <summary>String-keyed map of a scalar element kind: <c>define:</c> maps (<c>ref[key]</c> lookup).</summary>
    Map
}

/// <summary>
///     A complete language-level type: a <see cref="RulesTypeKind" /> plus, for lists and
///     maps, the scalar element kind. <see cref="ToString" /> renders the language-level
///     names used verbatim in diagnostics (<c>duration</c>, <c>list&lt;int&gt;</c>) — CLR
///     type names never appear in user-facing messages (spec §8).
/// </summary>
/// <param name="Kind">The type's kind.</param>
/// <param name="ElementKind">
///     Element kind for <see cref="RulesTypeKind.List" /> / <see cref="RulesTypeKind.Map" />;
///     <see cref="RulesTypeKind.None" /> otherwise (and for the empty list literal, whose
///     element kind is unknown).
/// </param>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "These are the published spec §3.1 language type names (int/float/string); diagnostics must render them verbatim.")]
public readonly record struct RulesType(RulesTypeKind Kind, RulesTypeKind ElementKind = RulesTypeKind.None)
{
    /// <summary>The <c>bool</c> type.</summary>
    public static RulesType Bool { get; } = new(RulesTypeKind.Bool);

    /// <summary>The <c>int</c> type.</summary>
    public static RulesType Int { get; } = new(RulesTypeKind.Int);

    /// <summary>The <c>float</c> type.</summary>
    public static RulesType Float { get; } = new(RulesTypeKind.Float);

    /// <summary>The <c>string</c> type.</summary>
    public static RulesType String { get; } = new(RulesTypeKind.String);

    /// <summary>The <c>duration</c> type (tick count).</summary>
    public static RulesType Duration { get; } = new(RulesTypeKind.Duration);

    /// <summary>The <c>instant</c> type (tick position).</summary>
    public static RulesType Instant { get; } = new(RulesTypeKind.Instant);

    /// <summary>The type of the <c>null</c> literal.</summary>
    public static RulesType Null { get; } = new(RulesTypeKind.Null);

    /// <summary>Creates a <c>list&lt;element&gt;</c> type.</summary>
    /// <param name="element">The scalar element kind.</param>
    /// <returns>The list type.</returns>
    public static RulesType ListOf(RulesTypeKind element) => new(RulesTypeKind.List, element);

    /// <summary>Creates a string-keyed <c>map&lt;element&gt;</c> type.</summary>
    /// <param name="element">The scalar value kind.</param>
    /// <returns>The map type.</returns>
    public static RulesType MapOf(RulesTypeKind element) => new(RulesTypeKind.Map, element);

    /// <summary>Renders the language-level type name used in diagnostics and hash preimages.</summary>
    /// <returns>The name, e.g. <c>int</c>, <c>duration</c>, <c>list&lt;string&gt;</c>.</returns>
    public override string ToString() =>
        Kind switch
        {
            RulesTypeKind.List => ElementKind == RulesTypeKind.None ? "list" : $"list<{KindName(ElementKind)}>",
            RulesTypeKind.Map => ElementKind == RulesTypeKind.None ? "map" : $"map<{KindName(ElementKind)}>",
            _ => KindName(Kind)
        };

    private static string KindName(RulesTypeKind kind) =>
        kind switch
        {
            RulesTypeKind.Null => "null",
            RulesTypeKind.Bool => "bool",
            RulesTypeKind.Int => "int",
            RulesTypeKind.Float => "float",
            RulesTypeKind.String => "string",
            RulesTypeKind.Duration => "duration",
            RulesTypeKind.Instant => "instant",
            RulesTypeKind.List => "list",
            RulesTypeKind.Map => "map",
            _ => "unknown"
        };
}
