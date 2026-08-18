#region

using CS2DemoKit.Parser.Entities.SchemaLens;

#endregion

namespace CS2DemoKit.Codegen;

/// <summary>
///     Deterministic per-(class, lane) slot allocator for the Schema Lens codegen
///     pipeline. Given a post-replay <see cref="LensState" />, produces a
///     map of <c>(class, canonical_field_name) → slot_index</c> per lane, where:
///     <list type="bullet">
///         <item>Slots start at <c>0</c> per (class, lane) and are dense (no gaps).</item>
///         <item>
///             Within each (class, lane), canonical engine field names are sorted
///             lexicographically with <see cref="StringComparer.Ordinal" /> and
///             assigned indices <c>0, 1, 2, ...</c> in that order.
///         </item>
///         <item>
///             Re-running the planner on the same <see cref="LensState" /> produces
///             a byte-identical <see cref="SlotPlan" /> — guaranteed by the cover
///             determinism test in <c>CS2DemoKit.Parser.Entities.Tests</c>.
///         </item>
///     </list>
///     <para>
///         <b>Hash neutrality.</b> The planner's output is <i>not</i> part of the
///         canonical-form serialization (see <see cref="SchemaLensCanonicalForm" />)
///         — the slot index is a deterministic function of the rest of the
///         <see cref="LensState" />, so including it in the hash would be redundant
///         and force a hash rev every time the sort algorithm changed. The genesis
///         <c>schemaHash</c> therefore survives the introduction of slot planning.
///     </para>
///     <para>
///         <b>Wide-int object-lane override.</b> Some fields are declared
///         <c>wireType:int</c> in the genesis migration (documenting intent) but the
///         CS2 runtime transmits them as <c>uint64</c>/<c>int64</c> on the wire.
///         <see cref="EntityTracker.BuildFieldDescs" /> "honours the wire" — it produces
///         an object decoder (TryCreateInt returns null for uint64), so the value lands
///         on the object lane as a boxed <c>ulong</c>, ignoring the Lens-declared int lane
///         when there is no coercion transform. Since the derivation emits HONEST lanes
///         (rule.WireType states what the runtime actually does), the planner routes by
///         the rule directly — no correction table.
///     </para>
/// </summary>
public static class SchemaLensSlotPlanner
{


    /// <summary>
    ///     Produces the slot plan for the given <paramref name="state" />.
    ///     Pure function — no I/O, no statics, no globals; deterministic on identical input.
    /// </summary>
    public static SlotPlan Plan(LensState state)
    {
        Dictionary<string, ClassPlan> classes = new();

        // Iterate classes in canonical (ordinal) order so the planner's traversal
        // mirrors SchemaLensCanonicalForm.Serialize. The classes ordering doesn't
        // affect slot assignment (slots are per (class, lane)) but it makes the
        // plan deterministic across re-runs even at the outer-dict level.
        foreach (string className in state.Fields.Keys.OrderBy(c => c, StringComparer.Ordinal))
        {
            Dictionary<string, FieldRule> fieldMap = state.Fields[className];

            // Collect canonical field names per lane.
            List<string> intFields = new();
            List<string> floatFields = new();
            List<string> objectFields = new();

            foreach ((string canonical, FieldRule rule) in fieldMap)
            {
                WireType effectiveLane = rule.WireType;

                switch (effectiveLane)
                {
                    case WireType.IntLane:
                        intFields.Add(canonical);
                        break;
                    case WireType.FloatLane:
                        floatFields.Add(canonical);
                        break;
                    case WireType.ObjectLane:
                        objectFields.Add(canonical);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"SchemaLensSlotPlanner: unknown wire type {effectiveLane} on " +
                            $"'{className}.{canonical}'. The canonical-form vocabulary changed without " +
                            $"a planner update.");
                }
            }

            // Sort within each lane ordinally. The cover determinism test asserts
            // identical output across 10 re-runs on identical input; the ordinal
            // comparison guarantees cross-machine determinism (Windows file enumeration
            // vs. macOS file enumeration vs. CI Linux).
            intFields.Sort(StringComparer.Ordinal);
            floatFields.Sort(StringComparer.Ordinal);
            objectFields.Sort(StringComparer.Ordinal);

            classes[className] = new ClassPlan(
                AssignDenseSlots(intFields),
                AssignDenseSlots(floatFields),
                AssignDenseSlots(objectFields));
        }

        return new SlotPlan(classes);
    }

    /// <summary>
    ///     Looks up the slot index for the given canonical field on its effective lane,
    ///     returning <c>-1</c> if the class/lane has no entry. The runtime sentinel
    ///     <c>-1</c> means "no codegen slot" — the runtime allocator auto-increments
    ///     in that case (zero-behaviour-change fallback).
    /// </summary>
    public static int LookupSlot(SlotPlan plan, string className, FieldRule rule, string canonical)
    {
        if (!plan.Classes.TryGetValue(className, out ClassPlan? classPlan))
        {
            return -1;
        }

        WireType effectiveLane = rule.WireType;

        IReadOnlyDictionary<string, int> lane = effectiveLane switch
        {
            WireType.IntLane => classPlan.IntSlots,
            WireType.FloatLane => classPlan.FloatSlots,
            WireType.ObjectLane => classPlan.ObjectSlots,
            _ => throw new InvalidOperationException(
                $"SchemaLensSlotPlanner: unknown wire type {effectiveLane} on " +
                $"'{className}.{canonical}'.")
        };

        return lane.TryGetValue(canonical, out int slot) ? slot : -1;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, int> AssignDenseSlots(IReadOnlyList<string> sortedFields)
    {
        Dictionary<string, int> result = new(sortedFields.Count);
        for (int i = 0; i < sortedFields.Count; i++)
        {
            result[sortedFields[i]] = i;
        }

        return result;
    }

    /// <summary>
    ///     The plan: per-class, per-lane sequence of <c>(canonical_field_name, slot_index)</c>
    ///     pairs. The <see cref="ClassPlan" /> exposes three dictionaries (one per lane)
    ///     keyed by canonical name. Slot indices are dense from <c>0</c>.
    /// </summary>
    /// <param name="Classes">Class-name → per-lane slot maps.</param>
    public sealed record SlotPlan(IReadOnlyDictionary<string, ClassPlan> Classes);

    /// <summary>
    ///     Per-class lane plans. Each lane's dictionary maps canonical engine field
    ///     name (e.g. <c>"m_iHealth"</c>) → assigned slot index (e.g. <c>3</c>).
    ///     Lanes a class doesn't use carry an empty dictionary.
    /// </summary>
    public sealed record ClassPlan(
        IReadOnlyDictionary<string, int> IntSlots,
        IReadOnlyDictionary<string, int> FloatSlots,
        IReadOnlyDictionary<string, int> ObjectSlots);
}
