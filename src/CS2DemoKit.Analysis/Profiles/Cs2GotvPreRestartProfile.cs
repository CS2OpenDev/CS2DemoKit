#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Profiles;

/// <summary>
///     GOTV recordings whose per-round end marker is <c>cs_pre_restart</c> rather than
///     <c>round_officially_ended</c> — third-party tournament servers (ESL, BLAST) recording through
///     SourceTV. Identical to <see cref="Cs2GotvProfile" /> in every other respect.
/// </summary>
/// <remarks>
///     These demos advertise themselves exactly like Valve matchmaking — client name
///     <c>SourceTV Demo</c> — so the header-based <see cref="DemoSourceClassifier" /> cannot tell
///     them apart, and they classify as <see cref="DemoSourceKind.GotvMatchmaking" />. Their server
///     name is the only header hint (<c>ESL Match Server #1</c>, <c>BLAST.tv Premier CS2 Server</c>),
///     which is far too brittle to key on. What DOES separate them is the wire vocabulary:
///     <c>round_officially_ended</c> fires ZERO times across a full match while <c>cs_pre_restart</c>
///     fires once per round. <see cref="DemoSourceProfileRegistry.Resolve(DemoProfile, IReadOnlySet{string})" />
///     selects this profile on exactly that evidence.
///     <para>
///         Why a separate profile instead of listing both markers on <see cref="Cs2GotvProfile" />:
///         a profile's <c>$round_end</c> events must be MUTUALLY EXCLUSIVE PER ROUND. The v2
///         <c>count:</c> planner emits one unguarded Increment edge per concrete event
///         (<c>RuleChainBuilder.RulesetsV2</c>, <c>RuleNodeKind.Count</c>), so a Valve demo — where
///         <c>round_officially_ended</c> AND <c>cs_pre_restart</c> both fire every round — would
///         count every round twice. Measured: a 16-round match scored 26–6 across 32 "rounds".
///         Keeping one marker per profile preserves that invariant by construction.
///     </para>
///     <para>
///         This profile is deliberately NOT registered in <see cref="DemoSourceProfileRegistry" />'s
///         kind-matched list: it shares <see cref="DemoSourceKind.GotvMatchmaking" /> with
///         <see cref="Cs2GotvProfile" />, so kind-based resolution cannot choose between them. It is
///         reached only through the vocabulary-aware overload. The rules catalog still carries it —
///         the generator reflects over every non-abstract profile in the assembly.
///     </para>
/// </remarks>
public class Cs2GotvPreRestartProfile : Cs2GotvProfile
{
    /// <inheritdoc />
    public override DemoFeatureSet Features =>
        DemoFeatureSet.HasPlayerBlind
        | DemoFeatureSet.HasCsPreRestart
        | DemoFeatureSet.HasWeaponReload
        | DemoFeatureSet.HasWeaponZoom;

    /// <inheritdoc />
    public override LogicalEventBinding? RoundEnd =>
        // Same shape as the base profile — one per-round marker plus the terminal match-summary
        // marker for the final round, which has no per-round marker of its own.
        LogicalEventBinding.FirstWins(
            "cs_pre_restart",
            "cs_win_panel_match");

    /// <summary>These demos carry no per-round official-end event at all.</summary>
    public override LogicalEventBinding? RoundOfficiallyEnded => null;
}
