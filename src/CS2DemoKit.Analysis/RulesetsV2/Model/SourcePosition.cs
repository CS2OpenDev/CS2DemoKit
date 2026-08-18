#region

using System.Globalization;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     The position of a v2 document element or diagnostic, in the spec §8 <c>file(line,col)</c>
///     form. Unlike the semantic core's <c>SourceSpan</c>
///     (which is relative to a single expression scalar and carries no file), a
///     <see cref="SourcePosition" /> is document-absolute: it names the file and the 1-based
///     line/column the YAML node started at. Every v2 model record carries one so that a
///     structural diagnostic can point at exactly the offending stat, param, or match binding.
/// </summary>
/// <param name="File">Absolute path of the source file; <c>null</c> for in-memory YAML.</param>
/// <param name="Line">1-based line of the element's first character (as reported by the YAML parser).</param>
/// <param name="Column">1-based column of the element's first character.</param>
public readonly record struct SourcePosition(string? File, int Line, int Column)
{
    /// <summary>A position with no known location; used for synthesized elements that have no source node.</summary>
    public static SourcePosition None => new(null, 0, 0);

    /// <summary>Formats as <c>file(line,col)</c> for diagnostic prefixes and test output.</summary>
    /// <returns>The formatted location string.</returns>
    public override string ToString()
    {
        string name = File is null ? "<inline yaml>" : Path.GetFileName(File);
        return string.Create(CultureInfo.InvariantCulture, $"{name}({Line},{Column})");
    }
}
