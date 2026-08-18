#region

using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Compile;

/// <summary>
///     Drives the resolved-identity hasher (<see cref="RuleHasher" />) over a
///     <see cref="CheckedStat" /> — the planner's dedup key (spec §6 preimage). It packs the
///     stat's checked ASTs into a <see cref="RuleNodeDescriptor" />: the
///     trigger condition is row 5, a <c>sum:</c>/<c>capture:</c> value selector is the appended
///     row-5 slot, and a <c>while:</c> gate hashes to row 7's <see cref="RuleNodeDescriptor.GateHash" />
///     (distinct from the trigger). The compound <c>(For × Per)</c> scope is row 3, so a per-player
///     <c>per: round</c> stat and its <c>per: match</c> twin do NOT dedup. The view's implicit actor-role
///     binding is row 10, so a <c>count: kill</c> (actor = killer) and a <c>count: assist</c>
///     (actor = assister) — identical on rows 1-9 — do NOT dedup either (their per-slot role difference
///     is otherwise invisible to the hashed AST, §4.2). Two stats with equal hashes are behaviorally
///     interchangeable and the planner shares one node for them.
/// </summary>
public static class V2StatHasher
{
    /// <summary>Packs a checked stat into its resolved-identity preimage descriptor.</summary>
    /// <param name="stat">The checked stat.</param>
    /// <param name="statHashes">Resolves stat references (row 6) and the <c>while:</c> gate to their hashes.</param>
    /// <returns>The preimage descriptor.</returns>
    public static RuleNodeDescriptor Descriptor(CheckedStat stat, IStatHashSource statHashes)
    {
        ArgumentNullException.ThrowIfNull(stat);
        ArgumentNullException.ThrowIfNull(statHashes);

        ReadOnlyMemory<byte>? gateHash = stat.WhileGate is null
            ? null
            : ExpressionHasher.ComputeHash(stat.WhileGate, statHashes);

        // Row 10: the view's actor-role token. The identity-bearing pair is (ResolvedView,
        // SuppressActorBinding):
        //   * ResolvedView == null  -> null: raw/net/expression/compute nodes have no implicit actor,
        //     so they keep today's identity (row 10 absent, byte-identical to pre-fix).
        //   * SuppressActorBinding  -> "suppressed": `match: { actor: any }` turned the view's actor
        //     off, so identity no longer comes from the slot binding but from the explicit `where:`
        //     already baked into the row-5 TriggerCondition. A fixed token keeps every actor-suppressed
        //     stat on the same footing (their where: clauses discriminate them, not the view).
        //   * otherwise             -> ResolvedView: kill / assist / death bind different actor slots
        //     (killer / assister / victim) yet share rows 1-9, so the view name is what splits them.
        string? actorBinding = stat.ResolvedView is null
            ? null
            : stat.SuppressActorBinding
                ? "suppressed"
                : stat.ResolvedView;

        return new RuleNodeDescriptor(
            stat.StatId,
            stat.Kind,
            stat.ValueType,
            stat.Scope,
            stat.ConcreteEvents,
            stat.TriggerCondition,
            stat.ValueSelector,
            gateHash,
            stat.Keep,
            stat.BucketKeyParts,
            stat.BucketReducer,
            stat.TallyThresholds,
            stat.StreakWindow,
            stat.StreakMinStreak,
            actorBinding,
            // The compute's live cadence is identity-bearing (row 8) — a live and a
            // non-live compute over the same formula are NOT interchangeable, so they must not dedup.
            stat.Live);
    }

    /// <summary>Computes the stat's 32-byte resolved-identity hash.</summary>
    /// <param name="stat">The checked stat.</param>
    /// <param name="statHashes">Resolves stat references / the <c>while:</c> gate to their hashes.</param>
    /// <returns>The hash bytes.</returns>
    public static byte[] Hash(CheckedStat stat, IStatHashSource statHashes) =>
        RuleHasher.ComputeHash(Descriptor(stat, statHashes), statHashes);
}

/// <summary>
///     An <see cref="IStatHashSource" /> backed by a path → hash map the planner fills as it hashes
///     nodes in dependency order (spec §6 row 6: a stat reference contributes the referenced node's
///     own hash, not its name). Both the bare and qualified spellings of a node key the same bytes.
///     A reference to a not-yet-hashed node is a planner bug (the 2.2b cycle pre-pass guarantees a
///     dependency order), surfaced loudly.
/// </summary>
public sealed class MapStatHashSource : IStatHashSource
{
    private readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _byPath;

    /// <summary>Creates the source over the planner's accumulating path → hash map.</summary>
    /// <param name="byPath">The live map the planner writes each hashed node's bytes into.</param>
    public MapStatHashSource(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> byPath)
    {
        ArgumentNullException.ThrowIfNull(byPath);
        _byPath = byPath;
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> GetStatHash(ResolvedReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.StatPath is { } path && _byPath.TryGetValue(path, out ReadOnlyMemory<byte> hash))
        {
            return hash;
        }

        throw new InvalidOperationException(
            $"v2 planner: stat reference '{reference.Path}' was hashed before the node it points at "
            + "(the dependency-ordered hashing invariant was violated).");
    }
}
