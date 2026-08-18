#region

using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Entities.SdkAbstractions;

/// <summary>
///     Per-class translation table from the SDK contract's ordinal space to DVN's storage:
///     <c>contract ordinal → Lens canonical path → (lane, slot)</c> — the table the accepted
///     SDK#6 proposal (§8.1, the reader-over-EntityState mapping) promised.
///     <para>
///         Built once per (binding, shape) pair at class-bind time; nothing here is touched on
///         a read except two array indexes. The subtlety the proposal called out lives here:
///         DVN's <see cref="ClassShape.PathToSlot" /> is keyed by the <b>wire</b> spelling of
///         the demo being replayed (the <c>Fields</c> projection must reproduce wire-name keys),
///         while the contract's ordinals are keyed by the <b>canonical</b> Lens spelling. The
///         binding's alias table bridges the two: each ordinal resolves through a candidate list
///         — the canonical path first, then every historical spelling aliased to it — and the
///         first candidate the shape knows wins. On a current demo the canonical spelling hits;
///         on a pre-rename demo the alias spelling hits; either way the ordinal reads the field.
///     </para>
///     <para>
///         An ordinal none of whose candidates appear in the shape resolves to
///         <see cref="SlotAddr.Fallback" /> and is probed against the entity's fallback
///         dictionary at read time (array elements and unmapped paths live there; so does
///         everything when no shape is bound at all — the tracker's all-fallback mode).
///     </para>
/// </summary>
public sealed class LensOrdinalMap
{
    private readonly ResolvedOrdinal[] _ordinals;
    private readonly Dictionary<string, int> _pathToOrdinal;

    private LensOrdinalMap(ResolvedOrdinal[] ordinals, Dictionary<string, int> pathToOrdinal)
    {
        _ordinals = ordinals;
        _pathToOrdinal = pathToOrdinal;
    }

    /// <summary>Number of ordinals in the binding's space (== <c>binding.CanonicalPaths.Count</c>).</summary>
    public int Count => _ordinals.Length;

    /// <summary>
    ///     Builds the table for one class. <paramref name="shape" /> may be <c>null</c>
    ///     (all-fallback mode — every ordinal probes the fallback dictionary).
    /// </summary>
    internal static LensOrdinalMap Build(EntityClassBinding binding, ClassShape? shape)
    {
        ArgumentNullException.ThrowIfNull(binding);

        // Reverse the alias table once: canonical path → historical spellings targeting it.
        // Sorted for a deterministic probe order (the set is tiny — renames are rare).
        Dictionary<string, List<string>>? aliasesByTarget = null;
        if (binding.Aliases.Count > 0)
        {
            aliasesByTarget = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> alias in binding.Aliases)
            {
                if (!aliasesByTarget.TryGetValue(alias.Value, out List<string>? spellings))
                {
                    aliasesByTarget[alias.Value] = spellings = new List<string>();
                }

                spellings.Add(alias.Key);
            }

            foreach (List<string> spellings in aliasesByTarget.Values)
            {
                spellings.Sort(StringComparer.Ordinal);
            }
        }

        ResolvedOrdinal[] ordinals = new ResolvedOrdinal[binding.CanonicalPaths.Count];
        Dictionary<string, int> pathToOrdinal = new(binding.CanonicalPaths.Count + binding.Aliases.Count,
            StringComparer.Ordinal);

        for (int i = 0; i < binding.CanonicalPaths.Count; i++)
        {
            string canonical = binding.CanonicalPaths[i];

            string[] candidates;
            if (aliasesByTarget is not null && aliasesByTarget.TryGetValue(canonical, out List<string>? spellings))
            {
                candidates = new string[spellings.Count + 1];
                candidates[0] = canonical;
                spellings.CopyTo(candidates, 1);
            }
            else
            {
                candidates = [canonical];
            }

            SlotAddr addr = SlotAddr.Fallback;
            if (shape is not null)
            {
                foreach (string candidate in candidates)
                {
                    if (shape.PathToSlot.TryGetValue(candidate, out SlotAddr a))
                    {
                        addr = a;
                        break;
                    }
                }
            }

            ordinals[i] = new ResolvedOrdinal(addr, candidates);

            // Every known spelling routes by-engine-path reads into this ordinal's candidate
            // walk, so a caller using the canonical spelling reads an old demo and vice versa.
            pathToOrdinal[canonical] = i;
            for (int c = 1; c < candidates.Length; c++)
            {
                pathToOrdinal.TryAdd(candidates[c], i);
            }
        }

        return new LensOrdinalMap(ordinals, pathToOrdinal);
    }

    /// <summary>Maps any known spelling — canonical or historical — to its ordinal.</summary>
    public bool TryGetOrdinal(string enginePath, out int ordinal)
        => _pathToOrdinal.TryGetValue(enginePath, out ordinal);

    /// <summary>
    ///     Returns the resolved storage address and candidate spellings for
    ///     <paramref name="ordinal" />, or <c>false</c> when it lies outside the binding's space
    ///     (the contract's degrade-don't-crash case for stale wrappers).
    /// </summary>
    internal bool TryGetResolved(int ordinal, out SlotAddr addr, out string[] candidates)
    {
        if ((uint)ordinal >= (uint)_ordinals.Length)
        {
            addr = SlotAddr.Fallback;
            candidates = [];
            return false;
        }

        ResolvedOrdinal resolved = _ordinals[ordinal];
        addr = resolved.Addr;
        candidates = resolved.Candidates;
        return true;
    }

    private readonly record struct ResolvedOrdinal(SlotAddr Addr, string[] Candidates);
}
