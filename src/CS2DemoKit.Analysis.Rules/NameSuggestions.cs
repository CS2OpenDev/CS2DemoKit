namespace CS2DemoKit.Analysis.Rules;

/// <summary>
///     Did-you-mean candidate ranking for resolution errors: case-insensitive Levenshtein
///     distance ≤ 2 (the spec §8 convention, shared with <c>rules check</c>), ranked by
///     distance then ordinal, capped at three suggestions.
/// </summary>
internal static class NameSuggestions
{
    private const int MaxDistance = 2;
    private const int MaxSuggestions = 3;

    /// <summary>Ranks near-miss candidates for a name that failed to resolve.</summary>
    /// <param name="written">The name the author wrote.</param>
    /// <param name="candidates">The names that would have resolved.</param>
    /// <returns>Up to three candidates within edit distance 2, best first.</returns>
    internal static IReadOnlyList<string> Suggest(string written, IEnumerable<string> candidates)
    {
        List<(string Name, int Distance)> hits = [];
        foreach (string candidate in candidates)
        {
            int distance = Levenshtein(written, candidate, MaxDistance);
            if (distance <= MaxDistance)
            {
                hits.Add((candidate, distance));
            }
        }

        return hits
            .OrderBy(h => h.Distance)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select(h => h.Name)
            .ToArray();
    }

    private static int Levenshtein(string a, string b, int cap)
    {
        if (Math.Abs(a.Length - b.Length) > cap)
        {
            return int.MaxValue;
        }

        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
