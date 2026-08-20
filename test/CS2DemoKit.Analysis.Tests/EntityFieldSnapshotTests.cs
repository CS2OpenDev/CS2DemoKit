#region

using System.Globalization;
using System.Text.Json;
using CS2DemoKit.Analysis.GoldenStats;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Field-level parser regression tests (snapshot-fixture path).
///     For each demo with a committed <c>entity-fields.ours.golden.json</c>,
///     re-runs our parser at the snapshot's pinned ticks and asserts every
///     (slot, class, field) matches the fixture. Drift = a parser change has
///     altered observable entity state at the field level.
///     <para>
///         <b>Workflow:</b> the EntityFieldDiff tool produces these fixtures
///         (validated against demofile-net at write time). The tests below
///         consume them. demofile-net is intentionally NOT in this test
///         project's dependency graph — the constraint
///         <c>feedback_demofile_net_comparison_only.md</c> is honored, and
///         the audit's F5 design was adjusted accordingly.
///     </para>
///     <para>
///         <b>Fixture refresh is a maintainer task today.</b> The EntityFieldDiff
///         tool (which produces these fixtures via its <c>--write-snapshot</c>
///         flag) lives under <c>tools/DemoViewer.NET.EntityFieldDiff/</c> and
///         is gitignored — it ProjectReferences demofile-net in a sibling
///         repo (<c>../demofile-net/</c>), so it doesn't build for anyone who
///         hasn't cloned that repo alongside this one. Result: only a
///         maintainer who has the local setup can refresh fixtures. If you
///         need a refresh, ask a maintainer; if you want to do it yourself,
///         clone <c>SteamDatabase/demofile-net</c> as a sibling and the tool
///         will build. The dfn cross-check at write time confirms the new
///         snapshot is trustworthy before commit.
///     </para>
/// </summary>
[Category("Oracle")]
public class EntityFieldSnapshotTests
{
    /// <summary>
    ///     Discovers every demo under <c>tests/fixtures/&lt;demo-id&gt;/</c>
    ///     that has an <c>entity-fields.ours.golden.json</c>. Each becomes
    ///     one parameterised test case. When the demo file (in
    ///     <c>demos/</c>) is missing on a machine, that case skips cleanly
    ///     rather than failing.
    /// </summary>
    public static IEnumerable<string> DemoIdsWithSnapshot()
    {
        foreach (string demoId in GoldenStatsTestHelper.AllDemoIds())
        {
            string path = Path.Combine(GoldenStatsTestHelper.FindFixtureDir(demoId),
                "entity-fields.ours.golden.json");
            if (File.Exists(path))
            {
                yield return demoId;
            }
        }
    }

    /// <summary>Ours matches snapshot.</summary>
    [Test]
    [MethodDataSource(nameof(DemoIdsWithSnapshot))]
    public async Task OursMatchesSnapshot(string demoId)
    {
        // Load the snapshot.
        string snapPath = Path.Combine(GoldenStatsTestHelper.FindFixtureDir(demoId),
            "entity-fields.ours.golden.json");
        EntityFieldsSnapshot snap = EntityFieldsSnapshotSerializer.ReadFromFile(snapPath);

        // Locate the demo on disk. The snapshot's `demo` field is the bare
        // filename; we use the same lookup the parser tests use to find it.
        string demoFile = DemoTestHelper.RequireDemo(snap.DemoFileName);

        // Re-run our parser and capture a fresh snapshot at the same ticks.
        ParsedDemo parsed = DemoTestHelper.GetOrParse(demoFile);
        int[] ticks = snap.Ticks.Keys
            .Select(k => int.Parse(k, CultureInfo.InvariantCulture))
            .OrderBy(t => t)
            .ToArray();
        EntityFieldsSnapshot fresh = EntityFieldsSnapshot.Capture(parsed, snap.DemoFileName, ticks);

        // Diff snapshot vs fresh capture. Accumulate divergences; report all
        // in one assertion failure with structured detail.
        List<string> divergences = new();
        int rowsCompared = 0;
        int fieldsCompared = 0;

        foreach ((string tickKey, List<EntityFieldRow> expectedRows) in snap.Ticks)
        {
            if (!fresh.Ticks.TryGetValue(tickKey, out List<EntityFieldRow>? actualRows))
            {
                divergences.Add($"tick={tickKey}: MISSING-IN-FRESH (was {expectedRows.Count} rows in fixture)");
                continue;
            }

            // Build slot-keyed lookups for cross-comparison.
            Dictionary<int, EntityFieldRow> expectedBySlot = expectedRows.ToDictionary(r => r.Slot);
            Dictionary<int, EntityFieldRow> actualBySlot = actualRows.ToDictionary(r => r.Slot);
            HashSet<int> allSlots = new(expectedBySlot.Keys);
            allSlots.UnionWith(actualBySlot.Keys);

            foreach (int slot in allSlots.OrderBy(s => s))
            {
                bool inExpected = expectedBySlot.TryGetValue(slot, out EntityFieldRow? exp);
                bool inActual = actualBySlot.TryGetValue(slot, out EntityFieldRow? act);
                if (!inExpected)
                {
                    divergences.Add($"tick={tickKey} slot={slot}: ONLY-IN-FRESH class={act!.ClassName}");
                    continue;
                }

                if (!inActual)
                {
                    divergences.Add($"tick={tickKey} slot={slot}: MISSING-IN-FRESH class={exp!.ClassName}");
                    continue;
                }

                rowsCompared++;

                if (exp!.ClassName != act!.ClassName)
                {
                    divergences.Add($"tick={tickKey} slot={slot}: CLASS-MISMATCH expected={exp.ClassName} actual={act.ClassName}");
                    continue;
                }

                // Compare every field present in either side. Missing on one
                // side is a divergence — the snapshot pins the field set.
                HashSet<string> allFields = new(exp.Fields.Keys);
                allFields.UnionWith(act.Fields.Keys);
                foreach (string field in allFields.OrderBy(f => f, StringComparer.Ordinal))
                {
                    bool inExpField = exp.Fields.TryGetValue(field, out object? expVal);
                    bool inActField = act.Fields.TryGetValue(field, out object? actVal);
                    if (!inExpField)
                    {
                        divergences.Add($"tick={tickKey} slot={slot} field={field}: ONLY-IN-FRESH value={Fmt(actVal)}");
                        continue;
                    }

                    if (!inActField)
                    {
                        divergences.Add($"tick={tickKey} slot={slot} field={field}: MISSING-IN-FRESH (snapshot had {Fmt(expVal)})");
                        continue;
                    }

                    fieldsCompared++;

                    if (!ValuesEqual(expVal, actVal))
                    {
                        divergences.Add($"tick={tickKey} slot={slot} class={exp.ClassName} field={field}: expected={Fmt(expVal)} actual={Fmt(actVal)}");
                    }
                }
            }
        }

        Console.WriteLine($"{demoId}: rows compared={rowsCompared}, fields compared={fieldsCompared}, divergences={divergences.Count}");
        if (divergences.Count > 0)
        {
            Console.WriteLine(string.Join('\n', divergences.Take(30)));
        }

        // Floor first. "No divergences" is also what an empty snapshot produces, so without this a
        // fixture with zero ticks passes green having compared nothing.
        await Assert.That(fieldsCompared).IsGreaterThan(0)
            .Because($"the snapshot for '{demoId}' must actually pin fields, not just fail to disagree");
        await Assert.That(divergences.Count).IsEqualTo(0);
    }

    private static string Fmt(object? v) => v switch
    {
        null => "<null>",
        string s => $"\"{s}\"",
        _ => v.ToString() ?? "<?>"
    };

    private static bool TryToLong(object o, out long v)
    {
        switch (o)
        {
            case sbyte s:
                v = s;
                return true;
            case byte b:
                v = b;
                return true;
            case short s:
                v = s;
                return true;
            case ushort u:
                v = u;
                return true;
            case int i:
                v = i;
                return true;
            case uint u:
                v = u;
                return true;
            case long l:
                v = l;
                return true;
            case ulong ul when ul <= long.MaxValue:
                v = (long)ul;
                return true;
            default:
                v = 0;
                return false;
        }
    }

    private static object? UnwrapJsonElement(object? v)
    {
        if (v is not JsonElement el)
        {
            return v;
        }

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number when el.TryGetInt64(out long l) => l,
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.Null => null,
            _ => el.ToString()
        };
    }

    /// <summary>
    ///     Value equality across the wire-type variants that
    ///     <see cref="EntityFieldsSnapshot.Capture" /> emits and the JSON
    ///     deserialiser reconstructs. Capture normalises to long/double/bool/
    ///     string; on re-read, System.Text.Json materialises numbers as
    ///     <c>System.Text.Json.JsonElement</c>. Coerce both sides to
    ///     comparable forms.
    /// </summary>
    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        // Coerce JsonElement (read-side) back to primitives so the comparison
        // works against fresh-capture values which are bare CLR objects.
        object? aN = UnwrapJsonElement(a);
        object? bN = UnwrapJsonElement(b);
        if (aN is null || bN is null)
        {
            return aN is null && bN is null;
        }

        // Bool comparison (booleans don't survive numeric coercion).
        if (aN is bool ab && bN is bool bb)
        {
            return ab == bb;
        }

        if (aN is bool || bN is bool)
        {
            return false;
        }

        // Integer-family: compare on canonical long.
        if (TryToLong(aN, out long al) && TryToLong(bN, out long bl))
        {
            return al == bl;
        }

        // Floats: small epsilon. The capture only ever writes doubles when the
        // source was float/double/decimal, so direct equality is fine for the
        // common integer-only field set we currently capture.
        if (aN is double ad && bN is double bd)
        {
            return Math.Abs(ad - bd) < 1e-9;
        }

        // Fallback: string compare. Both ulong-as-string (m_steamID) and any
        // unknown types land here.
        return string.Equals(aN.ToString(), bN.ToString(), StringComparison.Ordinal);
    }
}
