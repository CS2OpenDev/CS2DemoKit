namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     The v2 document model: one <c>&lt;id&gt;.rules.yaml</c> file
///     is one ruleset. This is the YAML-independent structural form the loader maps to, stage-1
///     Expand multiplies (<c>for_each:</c>), and the structural validator checks — all before any
///     catalog resolution or planning.
/// </summary>
/// <param name="Id">The ruleset id (the <c>ruleset:</c> key).</param>
/// <param name="Title">The optional display title.</param>
/// <param name="Summary">The optional human-readable summary.</param>
/// <param name="For">The materialization scope (<c>for: match | each_player</c>).</param>
/// <param name="Enabled">
///     Whether the ruleset is enabled (default <c>true</c>); a disabled ruleset drops after tier
///     overlay.
/// </param>
/// <param name="Use">The qualified rulesets this file may reference — a validation allowlist.</param>
/// <param name="Exports">
///     The stat/highlight ids this file exports for cross-ruleset reads. A
///     <c>null</c> list means <b>everything</b> is exported (the absent-<c>exports:</c> default,
///     advisory lint only); a non-null list is the exported subset — a qualified <c>ruleset.stat</c>
///     read of an id outside it is an attributed not-exported error.
/// </param>
/// <param name="Params">The declared <c>params:</c>, in source order.</param>
/// <param name="Defines">The declared <c>define:</c> entries, in source order.</param>
/// <param name="Stats">The declared <c>stats:</c>, in source order.</param>
/// <param name="Highlights">The declared <c>highlights:</c>, in source order.</param>
/// <param name="Show">The optional <c>show:</c> surfacing block.</param>
/// <param name="CatalogVersion">
///     The optional <c>catalog_version:</c> provenance field — a free-form string pinning the catalog
///     a blueprint was authored against. Human/tooling metadata: accepted and stored, never a build
///     input.
/// </param>
/// <param name="MinAppVersion">
///     The optional <c>min_app_version:</c> provenance field — a free-form string pinning the minimum
///     app version. Human/tooling metadata: accepted and stored, never a build input.
/// </param>
/// <param name="Position">The document-absolute position of the ruleset (the root node).</param>
public sealed record RulesetDoc(
    string Id,
    string? Title,
    string? Summary,
    RulesetScope For,
    bool Enabled,
    IReadOnlyList<string> Use,
    IReadOnlyList<string>? Exports,
    IReadOnlyList<ParamDef> Params,
    IReadOnlyList<DefineDef> Defines,
    IReadOnlyList<StatDef> Stats,
    IReadOnlyList<HighlightDef> Highlights,
    ShowDef? Show,
    string? CatalogVersion,
    string? MinAppVersion,
    SourcePosition Position);

/// <summary>The two v2 ruleset materialization scopes (<c>for:</c>).</summary>
public enum RulesetScope
{
    /// <summary>Unset. Never produced by the mapper.</summary>
    None = 0,

    /// <summary><c>for: match</c> — one ruleset instance for the whole demo, no implicit player binding.</summary>
    Match,

    /// <summary><c>for: each_player</c> — one instance per player; views bind to the ruleset's player.</summary>
    EachPlayer
}
