#region

using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.Entities.SchemaLens;

#endregion

namespace CS2DemoKit.Parser.Entities.SdkAbstractions;

/// <summary>
///     Builds SDK <see cref="EntityClassBinding" /> manifests at runtime from the loaded
///     Schema Lens state — the stand-in for the manifests the upstream emitter will one day
///     ship beside its generated wrappers.
///     <para>
///         Ordinal space: <see cref="EntityClassBinding.CanonicalPaths" /> is the class's
///         canonical Lens paths sorted with <see cref="StringComparer.Ordinal" /> — the
///         deterministic "ordinal-sorted canonical Lens paths" numbering the SDK#6 thread
///         agreed on. The ordinals are meaningless outside a (binding, wrapper) pair emitted
///         together; a consumer binds against the array, never against hard-coded numbers.
///     </para>
///     <para>
///         Aliases: DVN's <see cref="LensState.AliasMap" /> stores identity entries
///         (canonical → canonical) as a lookup convenience; those are excluded here because a
///         contract alias whose key is also a canonical path would shadow the live field
///         (<c>BindingConformance</c> rejects it). Only genuine historical spellings survive.
///         Non-identity entries are passed through as-is — if Lens data ever carries an alias
///         targeting a removed field, conformance validation should shout rather than this
///         builder silently dropping it.
///     </para>
///     <para>
///         <see cref="EntityClassBinding.NetName" /> follows the codegen's naming convention
///         (the hand-maintained NET-name table the retired slots emitter once owned, which
///         this builder cannot reference — it lives in the tool, not the library): strip
///         exactly one leading <c>C</c> (<c>CCSPlayerPawn → CSPlayerPawn</c>,
///         <c>CAK47 → AK47</c>, <c>CC4 → C4</c>). Every entry in that table conforms to the
///         rule; a test pins the correspondence.
///     </para>
///     <para>
///         Note: <c>EntityClassBinding</c> 0.1.1 carries no schema-pinning members (no Lens
///         hash, no schema build), so <see cref="LensState.CanonicalHash" /> has nowhere to
///         travel — recorded as a contract observation in the SDK#6 findings report.
///     </para>
/// </summary>
public static class LensBindingBuilder
{
    /// <summary>
    ///     Builds one binding per active Lens class, ordered by engine class name. Run
    ///     <c>BindingConformance.ThrowIfInvalid</c> over the result at startup — it costs
    ///     microseconds and turns silent mis-binding into an exception with a sentence
    ///     attached.
    /// </summary>
    public static IReadOnlyList<EntityClassBinding> BuildAll(LensState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        List<EntityClassBinding> bindings = new(state.Classes.Count);
        foreach (string className in state.Classes.Order(StringComparer.Ordinal))
        {
            bindings.Add(Build(state, className));
        }

        return bindings;
    }

    /// <summary>Builds the binding for one Lens class.</summary>
    public static EntityClassBinding Build(LensState state, string className)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(className);

        Dictionary<string, FieldRule> fields = state.Fields.TryGetValue(className, out Dictionary<string, FieldRule>? f)
            ? f
            : new Dictionary<string, FieldRule>();

        string[] canonicalPaths = fields.Keys.Order(StringComparer.Ordinal).ToArray();

        Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        if (state.AliasMap.TryGetValue(className, out Dictionary<string, string>? aliasMap))
        {
            foreach (KeyValuePair<string, string> alias in aliasMap)
            {
                if (!string.Equals(alias.Key, alias.Value, StringComparison.Ordinal))
                {
                    aliases[alias.Key] = alias.Value;
                }
            }
        }

        List<int> handleOrdinals = new();
        for (int i = 0; i < canonicalPaths.Length; i++)
        {
            if (fields[canonicalPaths[i]].Transform == LensTransform.HandleIndex)
            {
                handleOrdinals.Add(i);
            }
        }

        return new EntityClassBinding(
            className,
            DeriveNetName(className),
            canonicalPaths,
            aliases,
            handleOrdinals);
    }

    /// <summary>
    ///     The codegen naming convention: drop one leading <c>C</c>. The authoritative table
    ///     was the retired slots emitter's EngineToNetName table (deleted in the SDK cutover); every entry
    ///     there follows this rule.
    /// </summary>
    public static string DeriveNetName(string engineClass)
    {
        ArgumentNullException.ThrowIfNull(engineClass);

        return engineClass.Length > 1 && engineClass[0] == 'C'
            ? engineClass[1..]
            : engineClass;
    }
}
