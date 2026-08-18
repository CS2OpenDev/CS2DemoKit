#region

using System.Text.Json;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.Entities.SchemaLens;

#endregion

namespace CS2DemoKit.Codegen;

/// <summary>
///     Derives the <see cref="LensState" /> from the pinned <c>CS2OpenDev.Sdk.Entities</c>
///     package instead of local migration JSONs — the SDK is the single curation authority
///     for CS2 object definitions, and schema-drift history lives in ITS migration files.
///     <para>
///         Two inputs, one output:
///     </para>
///     <list type="bullet">
///         <item>
///             <see cref="EntityWrapperRegistry.Bindings" /> (compile-time package reference)
///             supplies the classes, the prefix-flattened canonical path list per class, and the
///             alias tables that bridge wire spellings to schema-true canonicals.
///         </item>
///         <item>
///             The SDK's <c>schema-lens/state.json</c> supplies the one per-canonical fact the
///             binding does not carry: <c>schemaType</c>, which decides the storage lane.
///             Until the nupkg embeds it (SDK#44), the path is passed
///             explicitly via <c>--state</c>.
///         </item>
///     </list>
///     <para>
///         The <c>schemaType → (lane, transform)</c> mapping in <see cref="MapType" /> is
///         DVN's runtime storage policy: lanes state what the decoder's honour-the-wire
///         routing actually does, not what a legacy declaration said. An unmapped schemaType
///         is a LOUD failure, never a guess — a new SDK emit introducing one forces a
///         deliberate mapping decision here.
///     </para>
/// </summary>
public static class SchemaLensSdkDeriver
{
    /// <summary>Thrown when derivation cannot proceed (missing metadata, unmapped type, inconsistency).</summary>
    public sealed class LensDerivationException(string message) : Exception(message);

    /// <summary>
    ///     Builds the derived <see cref="LensState" /> (hash finalized) from the package bindings
    ///     plus the SDK state file at <paramref name="stateJsonPath" />.
    /// </summary>
    public static LensState Derive(string stateJsonPath)
    {
        SdkStateIndex index = SdkStateIndex.Load(stateJsonPath);
        LensState state = new();

        foreach (EntityClassBinding binding in EntityWrapperRegistry.Bindings
                     .OrderBy(b => b.EngineClass, StringComparer.Ordinal))
        {
            string cls = binding.EngineClass;
            state.Classes.Add(cls);
            Dictionary<string, FieldRule> fields = new(StringComparer.Ordinal);
            Dictionary<string, string> aliases = new(StringComparer.Ordinal);
            state.Fields[cls] = fields;
            state.AliasMap[cls] = aliases;

            foreach (string canonical in binding.CanonicalPaths)
            {
                string schemaType = index.Require(cls, canonical);
                (WireType wire, LensTransform transform) = MapType(schemaType, cls, canonical);
                fields[canonical] = new FieldRule(wire, transform);
                aliases[canonical] = canonical;
            }

            foreach ((string spelling, string canonical) in binding.Aliases)
            {
                if (!fields.ContainsKey(canonical))
                {
                    // An alias to a path outside this binding's curated set — nothing to lane.
                    continue;
                }

                aliases[spelling] = canonical;
            }

            // Wire-flattening rule (interim): the engine serializer flattens the body-component
            // origin leaves to "CBodyComponent.m_cellX" etc., but the SDK's alias tables carry
            // only the schema-true nested canonical — measured 2026-08-15: 18/162 legacy
            // spellings unmatched, all this pattern. Filed upstream (add the wire aliases to the
            // emitted bindings + state.json); when that ships, this loop becomes a no-op.
            foreach (string canonical in binding.CanonicalPaths)
            {
                const string originPrefix = "m_CBodyComponent.m_pSceneNode.m_vecOrigin.";
                if (canonical.StartsWith(originPrefix, StringComparison.Ordinal))
                {
                    aliases.TryAdd("CBodyComponent." + canonical[originPrefix.Length..], canonical);
                }
            }
        }

        state.CanonicalHash = SchemaLensCanonicalForm.ComputeHash(state);
        return state;
    }

    /// <summary>
    ///     DVN's storage policy: which lane a schema type occupies, plus the handle marker.
    ///     Lanes are HONEST — they state the lane the decoder's honour-the-wire routing
    ///     actually uses (bools/enums as ints on the int lane; wide 64-bit values boxed on
    ///     the object lane; handles boxed on the object lane with the <c>HandleIndex</c>
    ///     marker), so there is no downstream "effective lane" correction table. Nullability
    ///     truth lives in seen bits, not per-rule defaults — the migration-era default
    ///     ceremony fed pre-population plumbing nothing ever read.
    /// </summary>
    private static (WireType Wire, LensTransform Transform) MapType(
        string schemaType, string cls, string canonical)
    {
        string s = schemaType.Replace(" ", "");

        if (s.StartsWith("CHandle<", StringComparison.Ordinal))
        {
            // Boxed raw wire handle; masking/sentinels belong to handle resolution.
            return (WireType.ObjectLane, LensTransform.HandleIndex);
        }

        switch (s)
        {
            case "bool" // bools live as Int32 on the int lane (wire convention)
                or "int8" or "int16" or "int32" or "uint8" or "uint16" or "uint32"
                or "GameTick_t"
                or "CSPlayerState" or "PlayerConnectedState": // schema enums decode as ints
                return (WireType.IntLane, LensTransform.None);
            case "float32" or "float64" or "GameTime_t" or "CNetworkedQuantizedFloat":
                return (WireType.FloatLane, LensTransform.None);
            case "uint64" or "CInButtonState": // wide 64-bit values are boxed on the object lane
                return (WireType.ObjectLane, LensTransform.None);
            case "CUtlString" or "Vector" or "VectorWS" or "QAngle"
                or "CNetworkOriginCellCoordQuantizedVector" or "CNetworkVelocityVector":
                return (WireType.ObjectLane, LensTransform.None);
        }

        // Structural object-lane forms: pointers, arrays, char buffers, templated containers.
        if (s.EndsWith('*') || s.Contains('[') || s.Contains('<')
            || s.StartsWith("CUtl", StringComparison.Ordinal)
            || s.StartsWith("CNetwork", StringComparison.Ordinal))
        {
            return (WireType.ObjectLane, LensTransform.None);
        }

        throw new LensDerivationException(
            $"Unmapped schemaType '{schemaType}' for {cls}.{canonical}. Add a deliberate "
            + "mapping to SchemaLensSdkDeriver.MapType — do not guess a lane.");
    }

    /// <summary>
    ///     Global canonical-path → metadata index over the SDK state file. Canonical paths are
    ///     unique per declaring class; a cross-class spelling collision with DIFFERENT schema
    ///     types would make the global index ambiguous, so it fails loudly.
    /// </summary>
    private sealed class SdkStateIndex
    {
        private readonly Dictionary<string, string> _byCanonical = new(StringComparer.Ordinal);
        private string _sourcePath = "";

        public static SdkStateIndex Load(string stateJsonPath)
        {
            if (!File.Exists(stateJsonPath))
            {
                throw new LensDerivationException(
                    $"SDK state file not found: {stateJsonPath}. Pass --state <path-to-schema-lens/state.json> "
                    + "(sibling CS2OpenDev-SDK checkout) until the nupkg embeds it.");
            }

            SdkStateIndex index = new() { _sourcePath = stateJsonPath };
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(stateJsonPath));
            foreach (JsonProperty cls in doc.RootElement.GetProperty("classes").EnumerateObject())
            {
                if (!cls.Value.TryGetProperty("fields", out JsonElement fields))
                {
                    continue;
                }

                foreach (JsonProperty f in fields.EnumerateObject())
                {
                    string schemaType = f.Value.GetProperty("schemaType").GetString()!;
                    if (index._byCanonical.TryGetValue(f.Name, out string? existing)
                        && existing.Replace(" ", "") != schemaType.Replace(" ", ""))
                    {
                        throw new LensDerivationException(
                            $"Canonical path '{f.Name}' declared with conflicting schema types "
                            + $"('{existing}' vs '{schemaType}') in {stateJsonPath}.");
                    }

                    index._byCanonical[f.Name] = schemaType;
                }
            }

            return index;
        }

        public string Require(string cls, string canonical)
        {
            if (_byCanonical.TryGetValue(canonical, out string? schemaType))
            {
                return schemaType;
            }

            throw new LensDerivationException(
                $"Binding path {cls}.{canonical} has no metadata in {_sourcePath} — the package "
                + "and the state file are out of sync (bump both from the same SDK release).");
        }
    }
}
