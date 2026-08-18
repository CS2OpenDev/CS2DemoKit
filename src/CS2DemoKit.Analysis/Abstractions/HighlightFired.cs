namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     A self-contained record of one v2 highlight firing (rising edge), emitted at the firing
///     instant with everything the Highlights pipeline needs — no snapshot replay required, so it
///     is fully populated in bare mode (<c>AnalysisOptions.CaptureSnapshots = false</c>). This is
///     the rich sibling of the timeline's <see cref="RuleChainEvent" />: where the timeline entry
///     carries only the unqualified <c>_chain_&lt;id&gt;</c> name and a tick, this record restores
///     the ruleset qualifier, stamps round attribution from the live <c>round_number</c> node, and
///     carries the rendered <c>title:</c> — the surfacing layer named in
///     <c>CheckedHighlight</c>.
/// </summary>
/// <param name="RulesetId">
///     The declaring ruleset's id (e.g. <c>kast</c>). Together with <paramref name="HighlightId" />
///     this restores the qualified <c>{ruleset}.{highlight}</c> identity the timeline's
///     <c>_chain_&lt;id&gt;</c> node name loses.
/// </param>
/// <param name="HighlightId">The highlight's id within its ruleset (e.g. <c>post_plant_double</c>).</param>
/// <param name="FrameIndex">Zero-based index of the demo frame in which the highlight fired.</param>
/// <param name="Tick">
///     Server tick of the firing frame — the demo/frame clock, identical in semantics to
///     <see cref="RuleChainEvent.Tick" /> (pre-game frames carry a large negative sentinel,
///     gameplay frames run 1, 2, …; no <c>ServerStartTick</c> subtraction is needed or wanted).
/// </param>
/// <param name="PlayerSlot">
///     The materialized subject player's slot. Every lowered highlight is per-player (game-scoped
///     highlight lowering throws at build time), so this is always a real slot.
/// </param>
/// <param name="PlayerName">
///     The subject player's RAW in-demo name, exactly as resolved at materialization time — never
///     sanitized (downstream consumers such as CSVG's <c>spec_player</c> need the exact demo
///     spelling; sanitize at display time only).
/// </param>
/// <param name="RoundNumber">
///     The live <c>round_number</c> context node's value at the firing instant (0 = warmup /
///     unknown, matching the snapshot projector's convention).
/// </param>
/// <param name="RenderedTitle">
///     The highlight's <c>title:</c> template with its <c>{…}</c> holes rendered against live node
///     values at the firing instant (<c>{player.name}</c> = the raw player name,
///     <c>{round.number}</c> = <paramref name="RoundNumber" />, bare stat ids = the template's
///     local nodes). Unresolvable holes render as their literal <c>{…}</c> text — never a throw.
/// </param>
/// <param name="Score">
///     The authored ranking weight (0–100) from the highlight's <c>score:</c> key — folds rarity ×
///     coolness so the reel can order firings (higher = surfaced first). Defaults to 50 when
///     <c>score:</c> is unspecified.
/// </param>
/// <param name="Kind">
///     The editorial track (<see cref="HighlightKind" />) from the highlight's <c>kind:</c> key:
///     a skill <c>Highlight</c> (default), a <c>Funny</c> moment, or a <c>Lowlight</c>. Routes the
///     firing into the right reel and keeps lowlights/memes out of the main skill reel.
/// </param>
/// <param name="Group">
///     The supersession family from the highlight's <c>group:</c> key, or <c>null</c>. Firings sharing
///     a group are collapsed at the surfacing layer to the single highest-<see cref="Score" /> one per
///     player+round — so a tiered family (3K/4K/ace) surfaces only its top tier, not every threshold.
/// </param>
/// <param name="ClipStartTick">
///     The frame-clock tick of the FIRST contributing event of the round for a count-based highlight
///     (e.g. the first kill of a 4K), or <c>null</c>. A count highlight fires at the COMPLETING event's
///     tick (<see cref="Tick" />), so a multi-kill spanning more than the reel lead-in would clip its
///     early kills; the clip window reaches back to this tick (still floored by round start) so the
///     whole sequence is captured. <c>null</c> = no earlier reach (the pre-existing lead-in behavior),
///     the safe fallback whenever a first-tick could not be determined (net-triggered counts, ambiguous
///     multi-stat reads, old cached records).
/// </param>
public sealed record HighlightFired(
    string RulesetId,
    string HighlightId,
    int FrameIndex,
    int Tick,
    int PlayerSlot,
    string PlayerName,
    int RoundNumber,
    string RenderedTitle,
    int Score,
    HighlightKind Kind,
    string? Group = null,
    int? ClipStartTick = null);
