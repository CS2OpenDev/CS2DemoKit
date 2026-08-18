#region

using System.Text;
using System.Text.Json;

#endregion

namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     Formats a <see cref="MetricTable" /> as JSON using <c>System.Text.Json</c>, reusing the
///     GoldenStats conventions (indented, snake_case property names, nulls preserved as <c>null</c>).
///     The document shape is:
///     <code>
///     {
///       "name": "player_round_stats",
///       "dimension_columns": [ ... ],
///       "value_columns": [ ... ],
///       "rows": [ { "dimensions": { ... }, "values": { ... } }, ... ]
///     }
///     </code>
///     Cell values are written verbatim (numbers as numbers, bools as bools, strings as strings),
///     and column ordering follows the table's declared dimension/value lists.
/// </summary>
public sealed class JsonOutputFormatter : IOutputFormatter
{
    private static readonly JsonWriterOptions _writerOptions = new()
    {
        Indented = true
    };

    /// <inheritdoc />
    public string FileExtension => "json";

    /// <inheritdoc />
    public string Format(MetricTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, _writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("name", table.Name);

            WriteStringArray(writer, "dimension_columns", table.DimensionColumns);
            WriteStringArray(writer, "value_columns", table.ValueColumns);

            writer.WriteStartArray("rows");
            foreach (MetricRow row in table.Rows)
            {
                writer.WriteStartObject();

                writer.WriteStartObject("dimensions");
                WriteCells(writer, table.DimensionColumns, row.Dimensions);
                writer.WriteEndObject();

                writer.WriteStartObject("values");
                WriteCells(writer, table.ValueColumns, row.Values);
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (string v in values)
        {
            writer.WriteStringValue(v);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    ///     Write the cells for one column group in declared column order. A column absent from the
    ///     row's dictionary is emitted as JSON <c>null</c> so every row has the same key set.
    /// </summary>
    private static void WriteCells(Utf8JsonWriter writer, IReadOnlyList<string> columns, IReadOnlyDictionary<string, object?> cells)
    {
        foreach (string col in columns)
        {
            cells.TryGetValue(col, out object? value);
            writer.WritePropertyName(col);
            WriteValue(writer, value);
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
