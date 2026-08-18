#region

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

#endregion

namespace CS2DemoKit.RulesCatalog;

/// <summary>
///     Deserialization DTOs + loader for <c>data/views.yaml</c>, the curated v2 view vocabulary
///     (compiler-plan §5). Kept in the generator project — the parsed shape is an internal
///     staging model; <see cref="CatalogBuilder" /> verifies it against the live registries and
///     projects it into the committed catalog's <c>views</c> family.
/// </summary>
internal static class ViewsFile
{
    private const string ResourceName = "CS2DemoKit.RulesCatalog.data.views.yaml";

    /// <summary>Reads + parses the embedded <c>views.yaml</c>. Strict: an unknown key throws.</summary>
    public static ViewsDoc Load()
    {
        using Stream stream = typeof(ViewsFile).Assembly.GetManifestResourceStream(ResourceName)
                              ?? throw new InvalidOperationException(
                                  $"embedded views resource '{ResourceName}' missing — check the "
                                  + "EmbeddedResource include in CS2DemoKit.RulesCatalog.csproj");
        using StreamReader reader = new(stream);

        IDeserializer deserializer = new DeserializerBuilder()
            // snake_case keys (event, binding, availability, …); dictionary keys (role/facet
            // names) are taken verbatim. NOT IgnoreUnmatchedProperties — a typo in the curated
            // file must fail the generator, not silently drop a view attribute.
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        return deserializer.Deserialize<ViewsDoc>(reader)
               ?? throw new InvalidOperationException("views.yaml deserialized to null");
    }

    internal sealed class ViewsDoc
    {
        public Dictionary<string, ViewDto> Views { get; set; } = new();
    }

    internal sealed class ViewDto
    {
        public string? Event { get; set; }

        /// <summary>
        ///     Optional logical-event key, decoupled from <see cref="Event" /> (the wire event). When
        ///     set, the generator resolves this view's per-profile concrete events against
        ///     <c>"$" + Logical</c> instead of <c>"$" + Event</c>; role/field-facet checks still run
        ///     against <see cref="Event" />. Needed when a profile binds the logical concept under a
        ///     key that differs from the wire name (e.g. <c>HeGrenadeDetonate</c> →
        ///     <c>$he_grenade_detonate</c> → wire <c>hegrenade_detonate</c>). Absent ⇒ falls back to
        ///     <see cref="Event" />, so existing views are byte-identical.
        /// </summary>
        public string? Logical { get; set; }

        public string? Binding { get; set; }
        public string? Actor { get; set; }
        public string? Result { get; set; }
        public Dictionary<string, string> Roles { get; set; } = new();
        public List<string> Baked { get; set; } = new();
        public Dictionary<string, FacetDto> Facets { get; set; } = new();
        public string? Availability { get; set; }
    }

    internal sealed class FacetDto
    {
        public string? Type { get; set; }
        public string? Field { get; set; }
        public string? Enrichment { get; set; }
        public string? Expr { get; set; }
    }
}
