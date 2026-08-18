#region

using System.Globalization;
using System.Text;

#endregion

namespace CS2DemoKit.Analysis.Output;

/// <summary>
///     Formats a <see cref="MetricTable" /> as RFC 4180 CSV: a header row followed by one row per
///     <see cref="MetricRow" />. Dimension columns come first, then value columns — both in the
///     table's declared order. No external dependencies; numbers are written with invariant culture
///     and fields are quoted only when they contain a comma, quote, or newline.
/// </summary>
public sealed class CsvOutputFormatter : IOutputFormatter
{
    /// <inheritdoc />
    public string FileExtension => "csv";

    /// <inheritdoc />
    public string Format(MetricTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        StringBuilder sb = new();

        // Header: dimensions then values, in declared order.
        bool firstHeader = true;
        foreach (string col in table.DimensionColumns)
        {
            AppendField(sb, col, ref firstHeader);
        }

        foreach (string col in table.ValueColumns)
        {
            AppendField(sb, col, ref firstHeader);
        }

        sb.Append("\r\n");

        // Data rows.
        foreach (MetricRow row in table.Rows)
        {
            bool firstCell = true;
            foreach (string col in table.DimensionColumns)
            {
                row.Dimensions.TryGetValue(col, out object? value);
                AppendField(sb, FormatCell(value), ref firstCell);
            }

            foreach (string col in table.ValueColumns)
            {
                row.Values.TryGetValue(col, out object? value);
                AppendField(sb, FormatCell(value), ref firstCell);
            }

            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string field, ref bool first)
    {
        if (!first)
        {
            sb.Append(',');
        }

        sb.Append(Quote(field));
        first = false;
    }

    /// <summary>Render a cell value to its CSV text. Null → empty; numbers use invariant culture.</summary>
    private static string FormatCell(object? value) =>
        value switch
        {
            null => string.Empty,
            bool b => b ? "true" : "false",
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    /// <summary>RFC 4180 quoting: wrap in double-quotes and double any embedded quote when needed.</summary>
    private static string Quote(string field)
    {
        bool needsQuote = field.Contains(',', StringComparison.Ordinal)
                          || field.Contains('"', StringComparison.Ordinal)
                          || field.Contains('\n', StringComparison.Ordinal)
                          || field.Contains('\r', StringComparison.Ordinal);

        if (!needsQuote)
        {
            return field;
        }

        return string.Concat("\"", field.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
    }
}
