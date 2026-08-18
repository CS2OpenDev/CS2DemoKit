#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace CS2DemoKit.Analysis.Rules.Hashing;

/// <summary>
///     The v2 resolved-identity node hasher — the dedup key of the shared state graph. The
///     preimage is the ordered spec §6 row list; any change to its serialization after the
///     hash freeze is a breaking change audited by the preimage-snapshot golden test.
///     Rows are length-prefixed (UTF-8 byte counts), so no payload can masquerade as a row
///     boundary. Positions, display names, and output destinations are deliberately absent:
///     hash-equal must mean behaviorally interchangeable under reference-identity node
///     sharing.
/// </summary>
public static class RuleHasher
{
    private const string PreimagePrefix = "dvr2";

    /// <summary>Computes the SHA-256 resolved-identity hash of a node.</summary>
    /// <param name="node">The node's preimage fields.</param>
    /// <param name="statHashes">Resolves the expression's stat references to their nodes' hashes (row 6).</param>
    /// <returns>The 32-byte hash.</returns>
    public static byte[] ComputeHash(RuleNodeDescriptor node, IStatHashSource statHashes) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(BuildPreimage(node, statHashes)));

    /// <summary>Computes the hash as lowercase hex, for goldens and diagnostics.</summary>
    /// <param name="node">The node's preimage fields.</param>
    /// <param name="statHashes">Resolves the expression's stat references to their nodes' hashes (row 6).</param>
    /// <returns>The 64-char lowercase hex hash.</returns>
    public static string ComputeHashHex(RuleNodeDescriptor node, IStatHashSource statHashes) =>
        Convert.ToHexStringLower(ComputeHash(node, statHashes));

    /// <summary>
    ///     Builds the exact preimage text that is hashed — the payload of the
    ///     preimage-snapshot golden. Row layout:
    ///     <c>dvr2|1:len:kind|2:len:type|3:len:per|4:len:events|5:len:ast|7:len:gate|8:len:keep|9:len:salt[|10:len:actor]</c>
    ///     (row 6, the referenced-stat hashes, is embedded inside row 5's AST serialization).
    ///     Row 5 carries the trigger-condition AST; for <c>sum:</c>/<c>capture:</c> nodes it also
    ///     carries an appended, length-framed value-selector slot as <c>(cond … | value …)</c>.
    ///     Row 8 additionally carries tally thresholds and streak window/min-streak kind args.
    ///     Row 10 (the view's actor-role token) is emitted ONLY when present — a null
    ///     <see cref="RuleNodeDescriptor.ActorBinding" /> (every v1 caller) omits it entirely, so the
    ///     v1 preimage bytes are unchanged.
    /// </summary>
    /// <param name="node">The node's preimage fields.</param>
    /// <param name="statHashes">Resolves the expression's stat references to their nodes' hashes.</param>
    /// <returns>The preimage text.</returns>
    /// <exception cref="ArgumentException">A required field is unset (programmer misuse, never user input).</exception>
    public static string BuildPreimage(RuleNodeDescriptor node, IStatHashSource statHashes)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(statHashes);
        if (string.IsNullOrEmpty(node.StatId))
        {
            throw new ArgumentException("a node needs a non-empty StatId", nameof(node));
        }

        if (node.Kind == RuleNodeKind.None || node.Per == ScopeAxis.None)
        {
            throw new ArgumentException(
                $"node '{node.StatId}' needs a concrete Kind and Per axis to hash", nameof(node));
        }

        // Row 4 is order-insensitive by contract: sort + dedup.
        string events = string.Join(",",
            node.ConcreteEvents.Distinct(StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal));

        // Rows 5+6: the canonical AST(s) with embedded referenced-stat hashes. For a
        // sum:/capture: the trigger condition and the value selector pack into the two-slot
        // (cond … | value …) form.
        string ast = BuildRow5(node, statHashes);

        string gate = node.GateHash is { } gateHash ? Convert.ToHexStringLower(gateHash.Span) : "";

        StringBuilder keep = new(KeepName(node.Keep));
        if (node.BucketKeyParts is { Count: > 0 } keyParts)
        {
            // Order-preserving (a composite key [a, b] must NOT dedup with [b, a]) and joined on a
            // Unit Separator (U+001F) that no rendered key-expression text contains — a visible comma
            // could otherwise let a part like "foo(a, b)" forge a boundary. A single part joins to
            // itself (no separator), so every pre-C8 single-key bucket hashes byte-identically.
            keep.Append(";keys=").Append(string.Join('\u001f', keyParts));
        }

        if (!string.IsNullOrEmpty(node.BucketReducer))
        {
            keep.Append(";reducer=").Append(node.BucketReducer);
        }

        // Row 8 tally kind-args: the bucket thresholds as (Min, Target) pairs, sorted by
        // (Min, Target) and deduped (order-insensitive). Both components are identity-bearing —
        // Target is the emit-node id each boundary writes to, so different targets write to
        // different counters (v1's hasher hashes both). Two tallies differing in any threshold's
        // min OR target hash apart. Serialized as v1's <min>:<target> form (Target is a node id,
        // an identifier charset, so the ':'/',' delimiters are not forgeable in practice).
        if (node.TallyThresholds is { Count: > 0 } thresholds)
        {
            keep.Append(";tally=").Append(string.Join(",",
                thresholds.Distinct()
                    .OrderBy(t => t.Min).ThenBy(t => t.Target, StringComparer.Ordinal)
                    .Select(t => $"{t.Min.ToString(CultureInfo.InvariantCulture)}:{t.Target}")));
        }

        // Row 8 streak kind-args: window (ticks) + minimum streak length.
        if (node.StreakWindow is { } window)
        {
            keep.Append(";streak.window=").Append(window.ToString(CultureInfo.InvariantCulture));
        }

        if (node.StreakMinStreak is { } minStreak)
        {
            keep.Append(";streak.min=").Append(minStreak.ToString(CultureInfo.InvariantCulture));
        }

        // Row 8 compute cadence: a live: compute appends a `;live` marker so it hashes
        // apart from its non-live twin (different cadence = not interchangeable). Emitted ONLY when
        // Live is true, so a non-live compute and every earlier / v1 caller keep byte-identical row-8
        // bytes — the same additive discipline as the tally/streak/bucket kind-args above.
        if (node.Live)
        {
            keep.Append(";live");
        }

        // Row 9: a stat with no inputs hashes its own id so two empty counters stay distinct. A
        // value selector is a row-5 input just like the trigger condition, so it counts here —
        // otherwise two structurally identical value-only captures would each be id-salted and
        // fail to dedup.
        bool hasInputs = events.Length > 0 || node.Expression is not null
                                           || node.ValueSelector is not null || node.GateHash is not null;
        string salt = hasInputs ? "" : node.StatId;

        StringBuilder text = new(PreimagePrefix, 256);
        AppendRow(text, 1, KindName(node.Kind));
        AppendRow(text, 2, node.ValueType.ToString());
        AppendRow(text, 3, AxisName(node.Per));
        AppendRow(text, 4, events);
        AppendRow(text, 5, ast);
        AppendRow(text, 7, gate);
        AppendRow(text, 8, keep.ToString());
        AppendRow(text, 9, salt);

        // Row 10: the view's actor-role binding. Serialized ONLY when non-null, so every v1 caller
        // (which passes null) produces the exact pre-row-10 preimage bytes — byte-identical hashes,
        // the same additive discipline as the tally/bucket/streak kind-args on row 8. When present it
        // discriminates same-shaped stats whose view binds a different actor slot (count: kill vs
        // count: assist), which rows 1-9 cannot see (the slot equality is applied at edge-build time,
        // not in the row-5 trigger AST — §4.2).
        if (node.ActorBinding is { } actorBinding)
        {
            AppendRow(text, 10, actorBinding);
        }

        return text.ToString();
    }

    private static void AppendRow(StringBuilder text, int row, string payload) =>
        text.Append('|')
            .Append(row.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(Encoding.UTF8.GetByteCount(payload).ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(payload);

    /// <summary>
    ///     Serializes row 5. Single-AST kinds emit the trigger-condition preimage verbatim
    ///     (byte-identical to a lone <c>Expression</c>). Multi-AST kinds (a <c>sum:</c>/
    ///     <c>capture:</c> with a <see cref="RuleNodeDescriptor.ValueSelector" />) emit the
    ///     two-slot form <c>(cond &lt;len&gt;:&lt;cond&gt; | value &lt;len&gt;:&lt;value&gt;)</c>
    ///     (spec §6 row 5). Both slots are the value selector's/condition's serialized text
    ///     (not a hash — no forward-reference problem, unlike row 6) and are length-framed so a
    ///     user-authored string literal cannot masquerade as the <c>" | value "</c> delimiter.
    /// </summary>
    private static string BuildRow5(RuleNodeDescriptor node, IStatHashSource statHashes)
    {
        string cond = node.Expression is null ? "" : ExpressionHasher.BuildPreimage(node.Expression, statHashes);
        if (node.ValueSelector is null)
        {
            return cond;
        }

        string value = ExpressionHasher.BuildPreimage(node.ValueSelector, statHashes);
        return new StringBuilder("(cond ")
            .Append(Encoding.UTF8.GetByteCount(cond).ToString(CultureInfo.InvariantCulture)).Append(':').Append(cond)
            .Append(" | value ")
            .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append(':').Append(value)
            .Append(')')
            .ToString();
    }

    private static string KindName(RuleNodeKind kind) =>
        kind switch
        {
            RuleNodeKind.Flag => "flag",
            RuleNodeKind.Count => "count",
            RuleNodeKind.Sum => "sum",
            RuleNodeKind.Capture => "capture",
            RuleNodeKind.Bucket => "bucket",
            RuleNodeKind.Compute => "compute",
            RuleNodeKind.Highlight => "highlight",
            RuleNodeKind.Tally => "tally",
            RuleNodeKind.Streak => "streak",
            RuleNodeKind.Rate => "rate",
            RuleNodeKind.Burst => "burst",
            _ => "none"
        };

    private static string AxisName(ScopeAxis axis) =>
        axis switch
        {
            ScopeAxis.Match => "match",
            ScopeAxis.Round => "round",
            ScopeAxis.PlayerMatch => "player_match",
            ScopeAxis.PlayerRound => "player_round",
            _ => "none"
        };

    private static string KeepName(KeepKind keep) =>
        keep switch
        {
            KeepKind.First => "first",
            KeepKind.Last => "last",
            KeepKind.List => "list",
            KeepKind.Min => "min",
            KeepKind.Max => "max",
            _ => ""
        };
}
