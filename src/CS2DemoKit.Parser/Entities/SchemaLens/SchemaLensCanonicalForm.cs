#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace CS2DemoKit.Parser.Entities.SchemaLens;

/// <summary>
///     Deterministic canonical-form serialization of a <see cref="LensState" />, used as the
///     input to the sha256 hash stored in each migration's <c>schemaHash</c> field.
/// </summary>
/// <remarks>
///     <para>
///         Format version: <c>canon-v1</c>. This version string is included inside the hashed
///         bytes so that any future change to the serialization algorithm produces a different hash
///         without confusion. Changing the serialization is a one-way door (R10): every
///         existing migration's <c>schemaHash</c> becomes invalid. Do not alter this format
///         without a coordinated migration of all existing hashes.
///     </para>
///     <para>
///         Ordering rules to guarantee determinism on identical input regardless of dictionary
///         iteration order (R1):
///         <list type="bullet">
///             <item>Class names sorted lexicographically (ordinal).</item>
///             <item>
///                 Within each class, canonical field names sorted lexicographically (ordinal)
///                 in the <c>fields</c> block.
///             </item>
///             <item>
///                 Within each class, alias engine-names sorted lexicographically (ordinal) in
///                 the <c>aliases</c> block.
///             </item>
///             <item>Active class names in the <c>classes</c> block sorted lexicographically.</item>
///         </list>
///     </para>
///     <para>
///         Scalar serialization rules to guarantee cross-platform determinism:
///         <list type="bullet">
///             <item>
///                 <c>FallbackDefault</c> is serialized with an explicit type tag to avoid
///                 ambiguity between <c>100</c> (int) and <c>100.0</c> (float).
///             </item>
///             <item>Floats use invariant-culture <c>R</c> (round-trip) format.</item>
///             <item>Booleans are lowercase: <c>true</c> / <c>false</c>.</item>
///             <item>Null is the literal string <c>null</c>.</item>
///             <item>Enum values are serialized by name (e.g. <c>Int</c>, <c>BoolFromInt</c>).</item>
///         </list>
///     </para>
/// </remarks>
public static class SchemaLensCanonicalForm
{
    private const string FormatVersion = "canon-v1";

    /// <summary>
    ///     Produces the deterministic canonical-form string for the given <paramref name="state" />.
    ///     The returned string is the exact input to the sha256 hash.
    /// </summary>
    public static string Serialize(LensState state)
    {
        StringBuilder sb = new();
        sb.AppendLine(FormatVersion);

        // ── classes (active set) ─────────────────────────────────────────────
        sb.AppendLine("classes:");
        foreach (string cls in state.Classes.OrderBy(c => c, StringComparer.Ordinal))
        {
            sb.Append("  ").AppendLine(cls);
        }

        // ── fields (per-class canonical field rules) ─────────────────────────
        sb.AppendLine("fields:");
        foreach (string cls in state.Fields.Keys.OrderBy(c => c, StringComparer.Ordinal))
        {
            sb.Append("  class: ").AppendLine(cls);
            Dictionary<string, FieldRule> fieldMap = state.Fields[cls];
            foreach (string canonical in fieldMap.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                FieldRule rule = fieldMap[canonical];
                sb.Append("    field: ").AppendLine(canonical);
                sb.Append("      wireType: ").AppendLine(rule.WireType.ToString());
                sb.Append("      transform: ").AppendLine(rule.Transform.ToString());
            }
        }

        // ── aliases (per-class engine-name → canonical-name map) ─────────────
        sb.AppendLine("aliases:");
        foreach (string cls in state.AliasMap.Keys.OrderBy(c => c, StringComparer.Ordinal))
        {
            sb.Append("  class: ").AppendLine(cls);
            Dictionary<string, string> aliasMap = state.AliasMap[cls];
            foreach (string engineName in aliasMap.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                sb.Append("    ").Append(engineName).Append(" -> ").AppendLine(aliasMap[engineName]);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Computes the sha256 of the canonical-form serialization of <paramref name="state" />
    ///     and returns it as a lowercase hex string prefixed with <c>sha256:</c>.
    /// </summary>
    public static string ComputeHash(LensState state)
    {
        string canonical = Serialize(state);
        byte[] bytes = Encoding.UTF8.GetBytes(canonical);
        byte[] hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }


    // ── Private helpers ───────────────────────────────────────────────────────

}
