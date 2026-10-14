#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The hidden stat behind an event table (<c>per: player_event</c>, <c>team_event</c>,
///     <c>match_event</c>): a match-scoped <c>count:</c> of the table's <c>on:</c> trigger, added to
///     the document before resolve so it lowers, binds and materializes like any other stat. Every
///     increase of an instance's count is one row of the table.
/// </summary>
public static class EventTableSequence
{
    /// <summary>
    ///     The id prefix of a sequence stat. <c>#</c> cannot start an expression reference, so no
    ///     stat or show entry can name one.
    /// </summary>
    public const string IdPrefix = "#seq_";

    /// <summary>The per-player event grain.</summary>
    public const string PlayerEvent = "player_event";

    /// <summary>The per-side event grain.</summary>
    public const string TeamEvent = "team_event";

    /// <summary>The match-level event grain.</summary>
    public const string MatchEvent = "match_event";

    /// <summary>Whether <paramref name="per" /> is one of the three event grains.</summary>
    /// <param name="per">A table's <c>per:</c>.</param>
    /// <returns><c>true</c> for <c>player_event</c>, <c>team_event</c> and <c>match_event</c>.</returns>
    public static bool IsEventGrain(string? per) => per is PlayerEvent or TeamEvent or MatchEvent;

    /// <summary>The id of the sequence stat behind table <paramref name="tableName" />.</summary>
    /// <param name="tableName">The table's name.</param>
    /// <returns>The reserved stat id.</returns>
    public static string StatId(string tableName) => IdPrefix + tableName;

    /// <summary>Whether <paramref name="statId" /> is a sequence stat rather than one a ruleset declared.</summary>
    /// <param name="statId">A stat id.</param>
    /// <returns><c>true</c> for an id this class generated.</returns>
    public static bool IsSequenceStat(string statId) =>
        statId.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>
    ///     <paramref name="doc" /> with one sequence stat appended per event table that has an
    ///     <c>on:</c>; the same instance when there is none.
    /// </summary>
    internal static RulesetDoc WithSequenceStats(RulesetDoc doc)
    {
        if (doc.Show is not { Tables.Count: > 0 } show)
        {
            return doc;
        }

        List<StatDef>? added = null;
        foreach (TableDef table in show.Tables)
        {
            if (!IsEventGrain(table.Per) || string.IsNullOrWhiteSpace(table.On))
            {
                continue;
            }

            string id = StatId(table.Name);
            if (doc.Stats.Any(s => s.Id == id) || added?.Any(s => s.Id == id) == true)
            {
                continue;
            }

            added ??= [];
            added.Add(new StatDef(id, StatKind.Count, table.On, PerScope.Match, null, null, null, null, null,
                table.OnPosition));
        }

        return added is null ? doc : doc with { Stats = [.. doc.Stats, .. added] };
    }
}
