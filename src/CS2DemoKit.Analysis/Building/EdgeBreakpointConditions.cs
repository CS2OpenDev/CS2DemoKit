#region

using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;

#endregion

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     Compiles and evaluates conditional <em>edge</em> breakpoints: an <c>event.&lt;field&gt;</c>
///     predicate over the edge's game event or net message, evaluated against the already-decoded
///     subject at each message the edge fired — no graph re-evaluation. For a game event the subject
///     is the <c>GameEvent</c> fire (compile with the envelope as <c>parameterType</c>): wire fields
///     reach through its payload and per-fire transport (<c>event.tick</c>) resolves the way it does
///     in a ruleset. A net message has no envelope — its payload is the subject. The symmetric
///     counterpart to <see cref="NodeBreakpointConditions" /> for the edge case.
///     <para>
///         An edge fires on exactly one event type, so the condition language here is the rich
///         event-field one (<see cref="ExpressionCompiler.CompileEventCondition" />): comparisons of
///         <c>event.&lt;field&gt;</c> against literals or other event fields
///         (<c>event.IsHeadshot == true</c>, <c>event.Weapon == "ak47"</c>). Player-relative
///         references (<c>== player</c>, <c>player.entity.*</c>) need a per-player slot and are out of
///         scope here — they resolve to nothing and the condition is rejected at validation.
///     </para>
/// </summary>
public static class EdgeBreakpointConditions
{
    /// <summary>
    ///     The <c>event.&lt;field&gt;</c> identifiers a condition on this event may reference, for the
    ///     editor autocomplete. Sorted, distinct.
    /// </summary>
    public static IReadOnlyList<string> FieldIdentifiers(IReadOnlyDictionary<string, EventFieldAccessor> fields)
    {
        SortedSet<string> ids = new(StringComparer.Ordinal);
        foreach (string name in fields.Keys)
        {
            ids.Add("event." + name);
        }

        return ids.ToList();
    }

    /// <summary>
    ///     The identifiers a player/entity-aware edge condition may reference, for autocomplete:
    ///     <c>event.&lt;field&gt;</c>, the bare <c>player</c> (selected slot), <c>player.entity.&lt;provider&gt;</c>
    ///     (selected player's entity), and <c>&lt;SlotField&gt;.entity.&lt;provider&gt;</c> for each event
    ///     field naming a player (ending in "Slot") × each registered provider. Pass
    ///     <paramref name="includeEntityReads" /> = <c>false</c> to keep the bare <c>player</c>
    ///     slot-comparison token but drop the entity-read grammar (the scope-aware editor authors entity
    ///     reads through structured rows, so the free-text event-match box no longer suggests them).
    ///     Sorted, distinct.
    /// </summary>
    public static IReadOnlyList<string> FieldIdentifiers(
        IReadOnlyDictionary<string, EventFieldAccessor> fields,
        PerPlayerEntityValueProviderRegistry providers,
        bool includeEntityReads = true)
    {
        SortedSet<string> ids = new(StringComparer.Ordinal)
        {
            "player"
        };
        foreach (string name in fields.Keys)
        {
            ids.Add("event." + name);
        }

        if (includeEntityReads)
        {
            List<string> providerNames = providers.All.Select(p => p.Name).ToList();
            foreach (string provider in providerNames)
            {
                ids.Add($"player.{provider}");
            }

            foreach (string slotField in fields
                         .Where(kv => ExpressionCompiler.IsPlayerSlotField(kv.Key, kv.Value.FieldType))
                         .Select(kv => kv.Key))
            {
                foreach (string provider in providerNames)
                {
                    ids.Add($"{slotField}.{provider}");
                }
            }
        }

        return ids.ToList();
    }

    /// <summary>
    ///     Validates an edge condition against the event type's fields. Blank → <c>null</c> (the
    ///     default: break on every fire). Otherwise compiles via
    ///     <see cref="ExpressionCompiler.CompileEventCondition" /> and returns the error message, or
    ///     <c>null</c> when valid. Shares the exact compile path <see cref="Compile" /> uses.
    /// </summary>
    public static string? Validate(
        string? condition, Type eventType, IReadOnlyDictionary<string, EventFieldAccessor> fields,
        Type? parameterType = null)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return null;
        }

        try
        {
            ExpressionCompiler.CompileEventCondition(condition!, eventType, fields,
                parameterType: parameterType);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    ///     Validates an edge condition that may reference the filter's selected player (<c>player</c>) or
    ///     entity reads, routing through <see cref="ExpressionCompiler.CompileEdgePlayerEntityCondition" />
    ///     — the same compile path the host's hit computation uses, so a validated condition can't then
    ///     fail to compile at scan time. Returns the error message, or <c>null</c> when valid (blank → null).
    ///     Pass the current selected slot (even a negative sentinel) so a <c>player</c> condition still
    ///     validates — the host short-circuits negative slots to no hits.
    /// </summary>
    public static string? Validate(
        string? condition, Type eventType, IReadOnlyDictionary<string, EventFieldAccessor> fields,
        int? selectedPlayerSlot, PerPlayerEntityValueProviderRegistry? providers,
        Type? parameterType = null)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return null;
        }

        try
        {
            ExpressionCompiler.CompileEdgePlayerEntityCondition(
                condition!, eventType, fields, selectedPlayerSlot, providers, parameterType);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    ///     Compiles the condition to a <c>Func&lt;TEvent, bool&gt;</c> predicate (returned as a
    ///     <see cref="Delegate" /> — invoke with the subject: the fire when
    ///     <paramref name="parameterType" /> is the <c>GameEvent</c> envelope, the payload otherwise),
    ///     or <c>null</c> if it doesn't compile (validation blocks saving invalid conditions, so this
    ///     is the defensive path). Pass the envelope type for a game-event edge so per-fire transport
    ///     (<c>event.tick</c>) resolves the way it does in a ruleset; net-message edges omit it —
    ///     they have no envelope.
    /// </summary>
    public static Delegate? Compile(
        string condition, Type eventType, IReadOnlyDictionary<string, EventFieldAccessor> fields,
        Type? parameterType = null)
    {
        try
        {
            return ExpressionCompiler.CompileEventCondition(condition, eventType, fields,
                parameterType: parameterType);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Filters the edge's applied (fired) message indices to those whose subject satisfies
    ///     <paramref name="predicate" />. <paramref name="payloadAt" /> resolves the decoded subject
    ///     for a message index — the fire for a game event, the payload for a net message, matching
    ///     what the predicate compiled against (<c>null</c> = skip).
    ///     <para>
    ///         A predicate that <b>throws</b> on a payload (the expression language has <c>/</c> and
    ///         <c>%</c>, so <c>event.a / event.b</c> compiles but divides by zero when <c>b == 0</c>)
    ///         treats that index as a non-match — it never propagates out to crash the caller.
    ///     </para>
    /// </summary>
    public static List<int> FilterApplied(IReadOnlyList<int> applied, Delegate predicate, Func<int, object?> payloadAt)
    {
        List<int> hits = [];
        foreach (int i in applied)
        {
            object? payload = payloadAt(i);
            if (payload is null)
            {
                continue;
            }

            try
            {
                if (predicate.DynamicInvoke(payload) is true)
                {
                    hits.Add(i);
                }
            }
            catch
            {
                // Runtime throw (divide-by-zero on a 0 divisor, etc.) → non-match. The condition
                // validated at edit time, so a throw is data-dependent, not a user error.
            }
        }

        return hits;
    }

    /// <summary>
    ///     Like <see cref="FilterApplied" /> but for the player/entity edge substrate: the predicate is a
    ///     <c>Func&lt;TEvent, IEntityValueAt, bool&gt;</c>. <paramref name="accessorAt" /> supplies the
    ///     per-fire entity accessor positioned at that fire's PRE-FRAME state (a no-op accessor for
    ///     pure-event / bare-<c>player</c> predicates, which never read it). A <c>null</c> accessor or a
    ///     runtime throw treats the index as a non-match — parity with <see cref="FilterApplied" />.
    /// </summary>
    public static List<int> FilterAppliedWithEntities(
        IReadOnlyList<int> applied, Delegate predicate,
        Func<int, object?> payloadAt, Func<int, IEntityValueAt?> accessorAt)
    {
        List<int> hits = [];
        foreach (int i in applied)
        {
            object? payload = payloadAt(i);
            if (payload is null)
            {
                continue;
            }

            IEntityValueAt? accessor = accessorAt(i);
            if (accessor is null)
            {
                continue;
            }

            try
            {
                if (predicate.DynamicInvoke(payload, accessor) is true)
                {
                    hits.Add(i);
                }
            }
            catch
            {
                // Runtime throw → non-match (validated at edit time; data-dependent, not a user error).
            }
        }

        return hits;
    }
}
