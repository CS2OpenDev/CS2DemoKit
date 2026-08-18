#region

using System.Diagnostics.CodeAnalysis;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     One <c>params:</c> entry: a typed consumer input rendered as a form and
///     compiled to a constant. The mapper checks the default against the declared type and against
///     the optional <c>min</c>/<c>max</c> bounds; the resolver binds it to a literal.
/// </summary>
/// <param name="Name">The param name; part of the ruleset's shared id namespace.</param>
/// <param name="Type">The declared type (<c>int | float | bool | string | duration</c>).</param>
/// <param name="Default">
///     The parsed default value (<see cref="long" /> for int, <see cref="double" /> for float,
///     <see cref="bool" /> for bool, <see cref="string" /> for string/duration raw text), or
///     <c>null</c> when the default was absent or unparseable.
/// </param>
/// <param name="Min">The optional inclusive lower bound (numeric/duration only), as a double.</param>
/// <param name="Max">The optional inclusive upper bound (numeric/duration only), as a double.</param>
/// <param name="Position">The document-absolute position of the param.</param>
public sealed record ParamDef(
    string Name,
    ParamType Type,
    object? Default,
    double? Min,
    double? Max,
    SourcePosition Position);

/// <summary>The five v2 param types.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "These are the published spec §3.1 language type names (int/float/string); the vocabulary must render verbatim.")]
public enum ParamType
{
    /// <summary>Unset. Never produced by the mapper.</summary>
    None = 0,

    /// <summary><c>int</c> — a 64-bit signed integer.</summary>
    Int,

    /// <summary><c>float</c> — an IEEE double.</summary>
    Float,

    /// <summary><c>bool</c> — a boolean.</summary>
    Bool,

    /// <summary><c>string</c> — a UTF-8 string.</summary>
    String,

    /// <summary><c>duration</c> — a duration literal (tick count at runtime).</summary>
    Duration
}
