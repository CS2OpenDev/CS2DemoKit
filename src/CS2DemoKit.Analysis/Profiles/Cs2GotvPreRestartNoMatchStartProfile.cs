#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Profiles;

/// <summary>
///     A <see cref="Cs2GotvPreRestartProfile" /> recording that also carries no match-start marker,
///     because the capture began after the server had already fired it. Identical in every other
///     respect.
/// </summary>
/// <remarks>
///     <c>match_live</c> activates on <c>$match_start</c>, which binds to <c>begin_new_match</c>.
///     A recording that never fires it leaves the node dead for the whole file, and since
///     <c>round_active</c> and <c>round_number</c> are both parented on <c>match_live</c>, one dead
///     node takes every death, round-scoped and per-round stat with it.
///     <para>
///         The fallback is the first round freeze-end, and it is bound HERE rather than on the base
///         builtin because a pre-match round fires one too. Measured over a 388-demo corpus: the two
///         genuine FaceIt server recordings fire their first <c>round_freeze_end</c> at the knife
///         round, 2,493 and 3,916 ticks before <c>begin_new_match</c>, so an unconditional freeze-end
///         activation counts the knife round's deaths as live ones. Every demo that reaches this
///         profile fires no <c>begin_new_match</c>, no <c>round_announce_warmup</c>, and at most one
///         <c>round_announce_match_start</c>, so none of them carries a pre-match round.
///     </para>
/// </remarks>
public sealed class Cs2GotvPreRestartNoMatchStartProfile : Cs2GotvPreRestartProfile
{
    /// <inheritdoc />
    public override LogicalEventBinding? MatchLiveFallback =>
        LogicalEventBinding.Of("round_freeze_end");
}
