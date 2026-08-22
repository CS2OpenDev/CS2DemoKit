#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Canonical rendering of everything the rules engine produces for one demo, plus a short digest
///     of it. Used by <see cref="RulesOutputGoldenTests" /> to detect any change in rules output.
///     <para>
///         The full rendering runs to roughly 2,000 lines and names every player in the match, so it
///         is never committed: the golden holds structural counts and a hash of it instead. That
///         keeps real Steam names out of the repository and the fixture under a kilobyte. When a hash
///         mismatch needs diagnosing, write the full rendering out on both sides and diff those.
///     </para>
/// </summary>
internal static class RulesOutputDigest
{
    /// <summary>
    ///     Runs the four shipped rulesets and renders the result deterministically.
    ///     <para>
    ///         Rows are sorted by their rendered text before hashing. The engine's emission order is
    ///         not a contract, and leaving it unsorted would let a reordering read as a value change.
    ///     </para>
    /// </summary>
    public static string Render(ParsedDemo demo)
    {
        var rules = YamlConfigLoader.LoadShippedEmbedded();
        BuildResult build = DemoAnalysis.Build(demo, rules.Rulesets);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, build);

        StringBuilder sb = new();
        sb.Append("frames=").Append(demo.Frames.Count).Append('\n');
        sb.Append("gameEvents=").Append(demo.AllGameEvents.Count).Append('\n');
        sb.Append("highlights=").Append(run.Highlights.Count).Append('\n');
        sb.Append("ruleChainEvents=").Append(run.Timeline.Events.Count).Append('\n');

        sb.Append("[chains]\n");
        foreach (var g in run.Timeline.Events
                     .GroupBy(e => e.ChainName)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append(g.Key).Append('=').Append(g.Count()).Append('\n');
        }

        sb.Append("[highlights]\n");
        foreach (string line in run.Highlights
                     .Select(h => $"{h.RulesetId}/{h.HighlightId}|{h.Tick}|{h.PlayerSlot}|{h.RoundNumber}|{h.RenderedTitle}")
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            sb.Append(line).Append('\n');
        }

        sb.Append("[nodes]\n");
        if (run.Snapshots is { } snaps)
        {
            foreach (string line in snaps.FinalTrackedNodes
                         .Select(n => $"{n.Name}|{n.Subtitle ?? "-"}|{n.IsActive}|{n.GetDisplayValue() ?? "-"}|" +
                                      $"{(n.GetNumericValue() is { } f ? f.ToString("F6", CultureInfo.InvariantCulture) : "-")}")
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                sb.Append(line).Append('\n');
            }

            sb.Append("materializedPlayers=").Append(snaps.MaterializedPlayers.Count).Append('\n');
        }

        sb.Append("[tables]\n");
        foreach (MetricTable t in run.ProjectConfiguredOutputs(demo).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            sb.Append("== ").Append(t.Name).Append('\n');
            sb.Append("dims=").Append(string.Join(",", t.DimensionColumns)).Append('\n');
            sb.Append("vals=").Append(string.Join(",", t.ValueColumns)).Append('\n');
            foreach (string line in t.Rows
                         .Select(r =>
                             string.Join("|", t.DimensionColumns.Select(c => Fmt(r.Dimensions.GetValueOrDefault(c)))) +
                             "->" +
                             string.Join("|", t.ValueColumns.Select(c => Fmt(r.Values.GetValueOrDefault(c)))))
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                sb.Append(line).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     The committed form: structural counts plus a hash of the full rendering. Counts say what
    ///     shape changed, the hash says whether any value did, and neither carries a player name.
    /// </summary>
    public static string Digest(string rendered)
    {
        StringBuilder sb = new();
        foreach (string line in rendered.Split('\n'))
        {
            // Structural lines only. Node and highlight rows carry player names.
            if (line.StartsWith("frames=", StringComparison.Ordinal)
                || line.StartsWith("gameEvents=", StringComparison.Ordinal)
                || line.StartsWith("highlights=", StringComparison.Ordinal)
                || line.StartsWith("ruleChainEvents=", StringComparison.Ordinal)
                || line.StartsWith("materializedPlayers=", StringComparison.Ordinal)
                || line.StartsWith("dims=", StringComparison.Ordinal)
                || line.StartsWith("vals=", StringComparison.Ordinal)
                || line.StartsWith("== ", StringComparison.Ordinal))
            {
                sb.Append(line).Append('\n');
            }
        }

        sb.Append("nodeCount=").Append(SectionCount(rendered, "[nodes]", "materializedPlayers=")).Append('\n');
        sb.Append("chains\n");
        foreach (string line in Section(rendered, "[chains]", "[highlights]"))
        {
            sb.Append(line).Append('\n');
        }

        sb.Append("sha256=").Append(Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rendered))).ToLowerInvariant()).Append('\n');
        return sb.ToString();
    }

    private static IEnumerable<string> Section(string rendered, string start, string end)
    {
        bool inside = false;
        foreach (string line in rendered.Split('\n'))
        {
            if (line == start)
            {
                inside = true;
                continue;
            }

            if (line == end)
            {
                yield break;
            }

            if (inside && line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private static int SectionCount(string rendered, string start, string endPrefix)
    {
        int n = 0;
        bool inside = false;
        foreach (string line in rendered.Split('\n'))
        {
            if (line == start)
            {
                inside = true;
                continue;
            }

            if (inside)
            {
                if (line.StartsWith(endPrefix, StringComparison.Ordinal) || line == "[tables]")
                {
                    break;
                }

                if (line.Length > 0)
                {
                    n++;
                }
            }
        }

        return n;
    }

    private static string Fmt(object? v) => v switch
    {
        null => "<null>",
        double d => d.ToString("F6", CultureInfo.InvariantCulture),
        float f => f.ToString("F6", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "<null>"
    };
}
