namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     A column's <c>as:</c> display formatting for a tick-valued stat.
///     The raw projected value is a tick count; <c>as:</c> reshapes it at the demo's
///     tick rate. <see cref="None" /> (no <c>as:</c>) leaves the value byte-identical to today.
/// </summary>
public enum ColumnValueFormat
{
    /// <summary>No <c>as:</c> — the projected value is unchanged (the default).</summary>
    None = 0,

    /// <summary><c>as: ticks</c> — the raw integer tick value.</summary>
    Ticks,

    /// <summary><c>as: seconds</c> — the tick value divided by the tick rate (a double).</summary>
    Seconds,

    /// <summary><c>as: time</c> — the tick value rendered as <c>m:ss</c> at the tick rate.</summary>
    Time
}

/// <summary>
///     The <c>show:</c> block: the single surfacing declaration replacing
///     v1's <c>columns:</c> and <c>outputs:</c>. The mapper maps its structure; the surfacing
///     semantics (board defaults, list flattening) live in <c>ShowLowering</c>.
/// </summary>
/// <param name="Scoreboard">The <c>scoreboard:</c> entries, in source order.</param>
/// <param name="Tables">The custom <c>tables:</c>, in source order.</param>
/// <param name="Position">The document-absolute position of the show block.</param>
public sealed record ShowDef(
    IReadOnlyList<ScoreboardEntry> Scoreboard,
    IReadOnlyList<TableDef> Tables,
    SourcePosition Position);

/// <summary>One <c>scoreboard:</c> entry — a stat reference surfaced as a column.</summary>
/// <param name="Stat">The referenced stat or highlight (a highlight ref surfaces its <c>.count</c>).</param>
/// <param name="Label">The optional column label.</param>
/// <param name="Group">The optional display group the UI clusters columns under.</param>
/// <param name="Boards">
///     The optional board list (<c>[round]</c> / <c>[match]</c>); <c>null</c> = default from the stat's
///     <c>per:</c>.
/// </param>
/// <param name="As">
///     The optional <c>as:</c> display formatting for a tick-valued column (default
///     <see cref="ColumnValueFormat.None" />).
/// </param>
/// <param name="Position">The document-absolute position of the entry.</param>
public sealed record ScoreboardEntry(
    string Stat,
    string? Label,
    string? Group,
    IReadOnlyList<string>? Boards,
    ColumnValueFormat As,
    SourcePosition Position);

/// <summary>One custom <c>tables:</c> entry — a named export table over a closed dimension.</summary>
/// <param name="Name">The table's name (its map key).</param>
/// <param name="Per">
///     The table dimension (e.g. <c>player_round</c>); an opaque key in 2.2a, validated against the registry
///     in 2.2d.
/// </param>
/// <param name="Columns">The table's columns, in source order.</param>
/// <param name="Position">The document-absolute position of the table.</param>
public sealed record TableDef(
    string Name,
    string? Per,
    IReadOnlyList<TableColumn> Columns,
    SourcePosition Position);

/// <summary>One <c>columns:</c> entry inside a <see cref="TableDef" />.</summary>
/// <param name="Stat">The referenced stat or highlight.</param>
/// <param name="Label">The optional column label.</param>
/// <param name="As">
///     The optional <c>as:</c> display formatting for a tick-valued column (default
///     <see cref="ColumnValueFormat.None" />).
/// </param>
/// <param name="Position">The document-absolute position of the column.</param>
public sealed record TableColumn(string Stat, string? Label, ColumnValueFormat As, SourcePosition Position);
