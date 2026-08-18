#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     One <c>define:</c> entry: a named list, trigger, expression, or map.
///     The <c>define:</c> scope is "named triggers and lists" plus expression-bodied macros and
///     string-keyed lookup <b>maps</b> read through <c>ref[key]</c>.
/// </summary>
/// <param name="Name">The define's name; part of the ruleset's shared id namespace.</param>
/// <param name="Body">The define body — a list, a trigger, an expression, or a map.</param>
/// <param name="Position">The document-absolute position of the define.</param>
public sealed record DefineDef(string Name, DefineBody Body, SourcePosition Position);

/// <summary>The body of a <see cref="DefineDef" /> — one of four forms.</summary>
public abstract record DefineBody
{
    /// <summary>Creates a define body at the given source position.</summary>
    /// <param name="position">The document-absolute position of the body node.</param>
    protected DefineBody(SourcePosition position) => Position = position;

    /// <summary>The document-absolute position of the body node.</summary>
    public SourcePosition Position { get; }
}

/// <summary>A list-valued define: <c>util_weapons: [hegrenade, inferno, molotov]</c>.</summary>
/// <param name="Items">The list elements' exact source texts, in order.</param>
/// <param name="Pos">The body node's position.</param>
public sealed record ListDefineBody(IReadOnlyList<string> Items, SourcePosition Pos) : DefineBody(Pos);

/// <summary>A trigger-valued define: a mapping with <c>on:</c> (+ optional <c>match:</c> / <c>where:</c> / <c>while:</c>).</summary>
/// <param name="Trigger">The parsed trigger.</param>
/// <param name="Pos">The body node's position.</param>
public sealed record TriggerDefineBody(TriggerDef Trigger, SourcePosition Pos) : DefineBody(Pos);

/// <summary>An expression-valued define: a scalar expression string reused as a macro.</summary>
/// <param name="Text">The expression source text; unparsed in 2.2a.</param>
/// <param name="Pos">The body node's position.</param>
public sealed record ExpressionDefineBody(string Text, SourcePosition Pos) : DefineBody(Pos);

/// <summary>The uniform value type of a <see cref="MapDefineBody" /> (spec §3.4: all-number or all-string).</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "These are the spec §3.4 map value categories (number/string); the names mirror the language vocabulary.")]
public enum MapValueType
{
    /// <summary>
    ///     All values are numbers (integers or decimals) — the map types as <c>map&lt;int&gt;</c>/<c>map&lt;float&gt;</c>
    ///     .
    /// </summary>
    Number,

    /// <summary>All values are strings — the map types as <c>map&lt;string&gt;</c>.</summary>
    String
}

/// <summary>One <c>key: value</c> row of a <see cref="MapDefineBody" />; both are the exact source texts.</summary>
/// <param name="Key">The string key.</param>
/// <param name="Value">
///     The value's source text (a number or a string, per the map's <see cref="MapDefineBody.ValueType" />
///     ).
/// </param>
public readonly record struct MapDefineEntry(string Key, string Value);

/// <summary>
///     A map-valued define: a string-keyed lookup table, e.g.
///     <c>weapon_class: {ak47: rifle, awp: sniper}</c>. Read only through <c>ref[key]</c> subscript
///     (a miss yields <c>null</c>, mirroring list <c>[n]</c> out-of-range — spec §3.4). Values are
///     uniform: all numbers or all strings (a mixed map is a structural error, validated by
///     <c>RulesetStructuralValidator</c>).
/// </summary>
/// <param name="Entries">The key/value rows, in author order.</param>
/// <param name="ValueType">
///     The map's uniform value type; <c>null</c> when the values were not uniform (a structural
///     error).
/// </param>
/// <param name="Pos">The body node's position.</param>
public sealed record MapDefineBody(IReadOnlyList<MapDefineEntry> Entries, MapValueType? ValueType, SourcePosition Pos)
    : DefineBody(Pos);
