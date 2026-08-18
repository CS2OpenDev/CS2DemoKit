#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Scopes;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     Builds nested <see cref="IScopeSymbol" /> namespace trees from dotted value paths
///     (<c>player.health</c>, <c>round.bomb.was_planted</c>). Intermediate segments become
///     <see cref="ScopeSymbol.Namespace" /> nodes and leaves become <see cref="ScopeSymbol.Value" />
///     nodes. Used by <see cref="CatalogScopeAdapter" /> to fold the flat provider/context/enrichment
///     paths into the <c>player</c>/<c>round</c>/<c>match</c>/<c>enrich</c> roots.
/// </summary>
internal sealed class NamespaceTreeBuilder
{
    private readonly Dictionary<string, Node> _roots = new(StringComparer.Ordinal);

    /// <summary>Adds a value path, creating intermediate namespaces as needed.</summary>
    /// <param name="path">The dotted path (at least two segments: a root and a leaf).</param>
    /// <param name="type">The leaf value's type.</param>
    /// <exception cref="InvalidOperationException">Two paths disagree on whether a segment is a namespace or a value.</exception>
    internal void Add(string path, RulesType type) => Add(path, type, false);

    /// <summary>Adds a value path only if that exact leaf is not already present (used for optional injections).</summary>
    /// <param name="path">The dotted path.</param>
    /// <param name="type">The leaf value's type.</param>
    internal void AddIfAbsent(string path, RulesType type)
    {
        if (!Contains(path))
        {
            Add(path, type, false);
        }
    }

    /// <summary>Builds the namespace symbol for one root (e.g. <c>player</c>); empty when the root has no paths.</summary>
    /// <param name="root">The root segment name.</param>
    /// <returns>The namespace symbol.</returns>
    internal IScopeSymbol Build(string root) =>
        _roots.TryGetValue(root, out Node? node)
            ? node.ToSymbol(root)
            : ScopeSymbol.Namespace(root);

    private bool Contains(string path)
    {
        string[] segments = path.Split('.');
        if (!_roots.TryGetValue(segments[0], out Node? node))
        {
            return false;
        }

        for (int i = 1; i < segments.Length; i++)
        {
            if (!node!.Children.TryGetValue(segments[i], out Node? child))
            {
                return false;
            }

            node = child;
        }

        return true;
    }

    private void Add(string path, RulesType type, bool overwrite)
    {
        string[] segments = path.Split('.');
        if (segments.Length < 2)
        {
            throw new InvalidOperationException($"scope path '{path}' needs at least a root and a leaf segment.");
        }

        if (!_roots.TryGetValue(segments[0], out Node? node))
        {
            node = new Node();
            _roots[segments[0]] = node;
        }

        for (int i = 1; i < segments.Length - 1; i++)
        {
            if (!node.Children.TryGetValue(segments[i], out Node? child))
            {
                child = new Node();
                node.Children[segments[i]] = child;
            }

            node = child;
        }

        string leaf = segments[^1];
        if (node.Children.TryGetValue(leaf, out Node? existing) && existing.Children.Count > 0)
        {
            throw new InvalidOperationException(
                $"scope path '{path}' names a value, but '{leaf}' is already a namespace with members.");
        }

        node.Children[leaf] = new Node
        {
            ValueType = type
        };
    }

    private sealed class Node
    {
        internal Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);

        internal RulesType? ValueType { get; init; }

        internal ScopeSymbol ToSymbol(string name)
        {
            if (Children.Count == 0)
            {
                return ValueType is { } type
                    ? ScopeSymbol.Value(name, type)
                    : ScopeSymbol.Namespace(name);
            }

            IScopeSymbol[] members = Children
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value.ToSymbol(pair.Key))
                .ToArray();
            return ScopeSymbol.Namespace(name, members);
        }
    }
}
