#region

using System.Runtime.CompilerServices;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace CS2DemoKit.Analysis.Catalog;

/// <summary>
///     Extra provider rows for the rules catalog, so a ruleset can name a provider the host registered
///     itself. The embedded catalog is never modified: <see cref="CatalogResource.Load(CatalogExtension?)" />
///     returns a merged copy, and only a build, validation or fingerprint that is handed the extension
///     sees the rows. The providers themselves go in the registries as before
///     (<see cref="AnalysisOptions.PerPlayerEntityProviders" />, <see cref="AnalysisOptions.EntityProviders" />);
///     the extension only makes them nameable.
///     <para>
///         A rules path may nest under its root: <c>player.dv.has_helmet</c>, <c>match.dv.t_timeouts_left</c>.
///         A per-player row is a role-handle member at the same relative path (<c>victim.dv.has_helmet</c>).
///     </para>
///     <para>
///         <see cref="Create" /> refuses a row the resolver or the lowering could not serve: a provider name
///         outside <c>entity.</c> (a read is lowered to <c>player.&lt;provider name&gt;</c>), a scope other than
///         <c>perPlayer</c> or <c>singleton</c>, a rules path outside that scope's root (<c>player.</c>,
///         <c>match.</c>) or under the reserved <c>player.cmd.</c>, an unmapped type, a 64-bit or unsigned
///         integer (declare int), a per-player type the
///         per-pawn digest cannot store (only int, bool, float and string), and any name or path that
///         collides with the embedded catalog, an injected symbol or another row.
///     </para>
/// </summary>
public sealed class CatalogExtension
{
    // The per-pawn digest's lanes; any other per-player type throws when a run builds its columns, so it
    // is refused here instead.
    private static readonly string[] _perPlayerTypes =
        [.. DigestColumnLayout.StorableTypes.Select(t => FriendlyTypeMap.FriendlyName(t)!)];

    private static readonly ConditionalWeakTable<CatalogExtension, CatalogRoot> _merged = new();

    private CatalogExtension(IReadOnlyList<CatalogProvider> providers) => Providers = providers;

    /// <summary>The extra rows, in the order given.</summary>
    public IReadOnlyList<CatalogProvider> Providers { get; }

    /// <summary>Validates <paramref name="providers" /> against the embedded catalog and against each other.</summary>
    /// <param name="providers">The rows. Each needs a <see cref="CatalogProvider.V2Name" />.</param>
    /// <returns>The extension.</returns>
    /// <exception cref="ArgumentException">A row is refused; the message names it and why.</exception>
    public static CatalogExtension Create(IEnumerable<CatalogProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        List<CatalogProvider> rows = [.. providers];
        CatalogRoot embedded = CatalogResource.Load();

        HashSet<string> names = new(embedded.Providers.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        HashSet<string> paths = new(StringComparer.Ordinal);
        paths.UnionWith(embedded.Providers.Select(p => p.V2Name).OfType<string>());
        paths.UnionWith(embedded.Contexts.Select(c => c.V2Name).OfType<string>());
        paths.UnionWith(CatalogScopeAdapter.InjectedPaths);

        foreach (CatalogProvider row in rows)
        {
            ArgumentNullException.ThrowIfNull(row, nameof(providers));
            Check(row);
            if (!names.Add(row.Name))
            {
                throw Refused(row, $"the provider name '{row.Name}' is already in the catalog");
            }

            string path = row.V2Name!;
            if (!paths.Add(path))
            {
                throw Refused(row, $"the rules path '{path}' is already in the catalog");
            }
        }

        // A path that is a value cannot also be a namespace, in either direction.
        foreach (CatalogProvider row in rows)
        {
            string path = row.V2Name!;
            if (paths.FirstOrDefault(p => p.StartsWith(path + ".", StringComparison.Ordinal)) is { } under)
            {
                throw Refused(row, $"the rules path '{path}' is a namespace of '{under}'");
            }

            for (int dot = path.IndexOf('.', StringComparison.Ordinal);
                 dot >= 0;
                 dot = path.IndexOf('.', dot + 1))
            {
                string prefix = path[..dot];
                if (prefix.Contains('.', StringComparison.Ordinal) && paths.Contains(prefix))
                {
                    throw Refused(row, $"'{prefix}' is a value, so '{path}' cannot sit under it");
                }
            }
        }

        return new CatalogExtension(rows);
    }

    /// <summary>A <c>perPlayer</c> row for a registered per-player provider.</summary>
    /// <param name="provider">The provider, as registered in the per-player registry.</param>
    /// <param name="rulesPath">The path rulesets read it by, under <c>player.</c>.</param>
    /// <param name="note">An author-facing caveat, or null.</param>
    /// <param name="unit">The unit of one, or null.</param>
    /// <returns>The row.</returns>
    public static CatalogProvider PerPlayer(IPerPlayerEntityValueProvider provider, string rulesPath,
        string? note = null, string? unit = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Row(provider.Name, "perPlayer", provider.ValueType, rulesPath, note, unit);
    }

    /// <summary>A <c>singleton</c> row for a registered game-wide provider.</summary>
    /// <param name="provider">The provider, as registered in the entity registry.</param>
    /// <param name="rulesPath">The path rulesets read it by, under <c>match.</c>.</param>
    /// <param name="note">An author-facing caveat, or null.</param>
    /// <param name="unit">The unit of one, or null.</param>
    /// <returns>The row.</returns>
    public static CatalogProvider Singleton(IEntityValueProvider provider, string rulesPath,
        string? note = null, string? unit = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Row(provider.ContextName, "singleton", provider.ValueType, rulesPath, note, unit);
    }

    /// <summary>
    ///     The embedded catalog with these rows added, sorted by name like the generated file. Built once per
    ///     extension.
    /// </summary>
    internal CatalogRoot Merge(CatalogRoot embedded) =>
        _merged.GetValue(this, ext => embedded with
        {
            Providers = [.. embedded.Providers.Concat(ext.Providers).OrderBy(p => p.Name, StringComparer.Ordinal)]
        });

    private static CatalogProvider Row(string name, string scope, Type valueType, string rulesPath, string? note,
        string? unit)
    {
        ArgumentNullException.ThrowIfNull(rulesPath);
        string friendly = FriendlyTypeMap.FriendlyName(valueType)
                          ?? throw new ArgumentException(
                              $"provider '{name}' has value type {valueType.Name}, which the rules language has no type for",
                              nameof(valueType));
        return new CatalogProvider(name, scope, friendly, rulesPath, FriendlyTypeMap.Map(friendly).Kind.ToString(), unit,
            note);
    }

    private static void Check(CatalogProvider row)
    {
        if (string.IsNullOrWhiteSpace(row.Name) || !row.Name.StartsWith("entity.", StringComparison.Ordinal)
                                                || row.Name.Length == "entity.".Length)
        {
            throw Refused(row, "the provider name must start with 'entity.', which is how a read is lowered");
        }

        string root = row.Scope switch
        {
            "perPlayer" => "player.",
            "singleton" => "match.",
            _ => throw Refused(row, $"scope '{row.Scope}' cannot be extended; use perPlayer or singleton")
        };

        string path = row.V2Name ?? throw Refused(row, "the row has no rules path (V2Name)");
        if (!path.StartsWith(root, StringComparison.Ordinal) || path.Length == root.Length)
        {
            throw Refused(row, $"a {row.Scope} rules path must sit under '{root}', not '{path}'");
        }

        if (path.StartsWith($"player.{CatalogScopeAdapter.CommandMember}.", StringComparison.Ordinal))
        {
            throw Refused(row, "'player.cmd.' is reserved for the user-command reads");
        }

        foreach (string segment in path.Split('.'))
        {
            if (segment.Length == 0 || !IsIdentifier(segment))
            {
                throw Refused(row, $"'{path}' is not a dotted path of identifiers");
            }
        }

        RulesType type;
        try
        {
            type = FriendlyTypeMap.Map(row.ClrType);
        }
        catch (InvalidOperationException)
        {
            throw Refused(row, $"type '{row.ClrType}' has no rules type");
        }

        if (!FriendlyTypeMap.ProviderReadableTypes.Contains(row.ClrType))
        {
            throw Refused(row,
                $"type '{row.ClrType}' is not one a provider may declare for rules "
                + $"({string.Join(", ", FriendlyTypeMap.ProviderReadableTypes.Order(StringComparer.Ordinal))}); "
                + "declare an integer provider as int");
        }

        if (row.Scope == "perPlayer" && !_perPlayerTypes.Contains(row.ClrType, StringComparer.Ordinal))
        {
            throw Refused(row,
                $"type '{row.ClrType}' cannot be stored per player; use {string.Join(", ", _perPlayerTypes)}");
        }

        if (row.V2Type is { } v2Type && !string.Equals(v2Type, type.Kind.ToString(), StringComparison.Ordinal))
        {
            throw Refused(row, $"V2Type '{v2Type}' does not match type '{row.ClrType}' ({type.Kind})");
        }
    }

    private static bool IsIdentifier(string segment) =>
        (char.IsAsciiLetter(segment[0]) || segment[0] == '_')
        && segment.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static ArgumentException Refused(CatalogProvider row, string why) =>
        new($"catalog extension row '{row.Name}': {why}");
}
