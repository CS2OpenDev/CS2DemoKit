#region

using System.Text.Json;
using CS2DemoKit.Analysis.Output;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pure-function tests for <see cref="CsvOutputFormatter" /> and <see cref="JsonOutputFormatter" />
///     over hand-built <see cref="MetricTable" />s — no demo file, no evaluator. Verifies column
///     ordering (dimensions before values, in declared order), RFC 4180 quoting, type preservation,
///     and missing-cell handling.
/// </summary>
[Category("Unit")]
public class OutputFormatterTests
{
    private static MetricTable SampleTable() =>
        new(
            "player_round_stats",
            ["round_number", "player_name"],
            ["kills", "adr"],
            [
                new MetricRow(
                    new Dictionary<string, object?>
                    {
                        ["round_number"] = 1,
                        ["player_name"] = "Alice"
                    },
                    new Dictionary<string, object?>
                    {
                        ["kills"] = 2,
                        ["adr"] = 88.5
                    }),
                new MetricRow(
                    new Dictionary<string, object?>
                    {
                        ["round_number"] = 2,
                        ["player_name"] = "Bob"
                    },
                    new Dictionary<string, object?>
                    {
                        ["kills"] = 0,
                        ["adr"] = 0.0
                    })
            ]);

    // ── CSV ──────────────────────────────────────────────────────────────────

    /// <summary>Csv_emits header then rows in declared column order.</summary>
    [Test]
    public async Task Csv_EmitsHeaderThenRowsInDeclaredColumnOrder()
    {
        string csv = new CsvOutputFormatter().Format(SampleTable());

        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(3); // header + 2 rows
        await Assert.That(lines[0]).IsEqualTo("round_number,player_name,kills,adr");
        await Assert.That(lines[1]).IsEqualTo("1,Alice,2,88.5");
        await Assert.That(lines[2]).IsEqualTo("2,Bob,0,0");
    }

    /// <summary>Csv_uses crlf line endings.</summary>
    [Test]
    public async Task Csv_UsesCrlfLineEndings()
    {
        string csv = new CsvOutputFormatter().Format(SampleTable());
        await Assert.That(csv.Contains("\r\n", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Csv_quotes fields containing comma quote or newline.</summary>
    [Test]
    public async Task Csv_QuotesFieldsContainingCommaQuoteOrNewline()
    {
        MetricTable table = new(
            "t",
            ["name"],
            ["note"],
            [
                new MetricRow(
                    new Dictionary<string, object?>
                    {
                        ["name"] = "Smith, John"
                    },
                    new Dictionary<string, object?>
                    {
                        ["note"] = "say \"hi\"\nbye"
                    })
            ]);

        string csv = new CsvOutputFormatter().Format(table);
        string dataLine = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];

        // Comma triggers quoting; embedded quotes are doubled; the embedded newline is preserved inside quotes.
        await Assert.That(dataLine).IsEqualTo("\"Smith, John\",\"say \"\"hi\"\"\nbye\"");
    }

    /// <summary>Csv_missing cell renders as empty.</summary>
    [Test]
    public async Task Csv_MissingCellRendersAsEmpty()
    {
        MetricTable table = new(
            "t",
            ["a"],
            ["x", "y"],
            [
                new MetricRow(
                    new Dictionary<string, object?>
                    {
                        ["a"] = "row"
                    },
                    new Dictionary<string, object?>
                    {
                        ["x"] = 5
                    }) // y missing
            ]);

        string dataLine = new CsvOutputFormatter().Format(table).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
        await Assert.That(dataLine).IsEqualTo("row,5,");
    }

    // ── JSON ─────────────────────────────────────────────────────────────────

    /// <summary>Json_emits table envelope with dimension and value columns.</summary>
    [Test]
    public async Task Json_EmitsTableEnvelopeWithColumns()
    {
        string json = new JsonOutputFormatter().Format(SampleTable());

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("name").GetString()).IsEqualTo("player_round_stats");
        await Assert.That(root.GetProperty("dimension_columns").GetArrayLength()).IsEqualTo(2);
        await Assert.That(root.GetProperty("value_columns").GetArrayLength()).IsEqualTo(2);
        await Assert.That(root.GetProperty("rows").GetArrayLength()).IsEqualTo(2);
    }

    /// <summary>Json_preserves value types as numbers.</summary>
    [Test]
    public async Task Json_PreservesValueTypesAsNumbers()
    {
        string json = new JsonOutputFormatter().Format(SampleTable());
        using JsonDocument doc = JsonDocument.Parse(json);

        JsonElement firstRow = doc.RootElement.GetProperty("rows")[0];
        JsonElement values = firstRow.GetProperty("values");
        JsonElement dims = firstRow.GetProperty("dimensions");

        // kills is an int → JSON number; adr is a double → JSON number; round_number stays a number.
        await Assert.That(values.GetProperty("kills").ValueKind).IsEqualTo(JsonValueKind.Number);
        await Assert.That(values.GetProperty("kills").GetInt32()).IsEqualTo(2);
        await Assert.That(values.GetProperty("adr").GetDouble()).IsEqualTo(88.5).Within(0.0001);
        await Assert.That(dims.GetProperty("round_number").GetInt32()).IsEqualTo(1);
        await Assert.That(dims.GetProperty("player_name").GetString()).IsEqualTo("Alice");
    }

    /// <summary>Json_missing cell emitted as null so every row has the same keys.</summary>
    [Test]
    public async Task Json_MissingCellEmittedAsNull()
    {
        MetricTable table = new(
            "t",
            ["a"],
            ["x", "y"],
            [
                new MetricRow(
                    new Dictionary<string, object?>
                    {
                        ["a"] = "row"
                    },
                    new Dictionary<string, object?>
                    {
                        ["x"] = 5
                    }) // y missing
            ]);

        string json = new JsonOutputFormatter().Format(table);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement values = doc.RootElement.GetProperty("rows")[0].GetProperty("values");

        await Assert.That(values.GetProperty("x").GetInt32()).IsEqualTo(5);
        await Assert.That(values.GetProperty("y").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    /// <summary>File extension_matches format.</summary>
    [Test]
    public async Task FileExtension_MatchesFormat()
    {
        await Assert.That(new CsvOutputFormatter().FileExtension).IsEqualTo("csv");
        await Assert.That(new JsonOutputFormatter().FileExtension).IsEqualTo("json");
    }
}
