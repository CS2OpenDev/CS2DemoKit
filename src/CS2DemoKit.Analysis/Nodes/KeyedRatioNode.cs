#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     The node behind a v2 <c>rate:</c> stat (G3, per-key ratios): a <b>derived</b> per-key ratio over
///     two same-keyed <see cref="KeyedCounterNode" />s — <c>of:</c> (the numerator bucket) over
///     <c>per:</c> (the denominator bucket). It holds no state of its own and has no trigger edge; it
///     divides the two buckets per-key on read.
///     <para>
///         <b>Semantics (locked):</b>
///         <list type="bullet">
///             <item>
///                 Output iterates the <b>denominator</b> (<c>per:</c>) key set — a rate is defined
///                 only where the population base exists. (A union would invent phantom 0-denominator keys;
///                 an intersection would drop legitimate 0%-numerator rows like <c>knife</c> = kills but no
///                 headshots.)
///             </item>
///             <item>
///                 A numerator-<b>missing</b> key ⇒ numerator 0 ⇒ ratio <c>0.0</c> (a real row, e.g.
///                 <c>knife</c> → 0 / 2 = 0.0).
///             </item>
///             <item>
///                 A denominator key present but <c>== 0</c> ⇒ ratio undefined ⇒ the key is
///                 <b>skipped</b> (no row). A count bucket can't be 0, but a <c>sum</c>/<c>last</c>/<c>min</c>
///                 bucket can, so the guard is real.
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>Snapshot participation: none</b> (<see cref="ISnapshotExcludedNode" />, like its source
///         buckets). It samples <b>per-game only</b>: <see cref="Output.KeyedStatsProjector" /> reads the
///         live node after evaluation. Consequently a rate is match-scoped (the resolver forces
///         <c>per: match</c>).
///     </para>
/// </summary>
public sealed class KeyedRatioNode : StateNode, ISnapshotExcludedNode
{
    private readonly KeyedCounterNode _denominator;
    private readonly KeyedCounterNode _numerator;

    /// <summary>Creates a rate node dividing <paramref name="numerator" /> by <paramref name="denominator" /> per key.</summary>
    /// <param name="ruleId">The rate stat's id (projectors key output tables off this id).</param>
    /// <param name="name">The display name (falls back to the id).</param>
    /// <param name="numerator">The <c>of:</c> bucket supplying per-key numerators.</param>
    /// <param name="denominator">The <c>per:</c> bucket whose key set defines the output rows.</param>
    /// <param name="subtitle">Optional secondary display label (the player name).</param>
    public KeyedRatioNode(string ruleId, string name, KeyedCounterNode numerator, KeyedCounterNode denominator,
        string? subtitle = null)
    {
        RuleId = ruleId;
        Name = name;
        Subtitle = subtitle;
        _numerator = numerator;
        _denominator = denominator;
    }

    /// <summary>
    ///     The rule id this node was materialized from. <see cref="StateNode.Name" /> is the
    ///     <em>display</em> name (<c>name:</c> / <c>label:</c> falls back to the id) — projectors key
    ///     output tables off this id so table names stay stable when a display name is added later.
    /// </summary>
    public string RuleId { get; }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string? Subtitle { get; }

    /// <summary>
    ///     The per-key ratios, computed live on read over the <b>denominator</b> key set: for each
    ///     denominator key with a non-zero value, <c>numerator[key] / denominator[key]</c> (a missing
    ///     numerator counts as 0). A denominator key whose value is 0 is skipped (undefined ratio).
    /// </summary>
    public IReadOnlyDictionary<string, double> Buckets
    {
        get
        {
            Dictionary<string, double> ratios = new(StringComparer.Ordinal);
            foreach ((string key, double denom) in _denominator.Buckets)
            {
                if (denom == 0.0)
                {
                    continue; // undefined ratio — skip the key (no row)
                }

                double numer = _numerator.Buckets.TryGetValue(key, out double n) ? n : 0.0;
                ratios[key] = numer / denom;
            }

            return ratios;
        }
    }

    /// <inheritdoc />
    /// <remarks>Active once at least one denominator key has a non-zero value (i.e. at least one rate row exists).</remarks>
    public override bool IsActive
    {
        get
        {
            foreach ((string _, double denom) in _denominator.Buckets)
            {
                if (denom != 0.0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>Keyed values don't collapse to one scalar, so there is no single display value (per-key detail is the output).</remarks>
    public override string? GetDisplayValue() => null;

    /// <inheritdoc />
    public override float? GetNumericValue() => null;
}
