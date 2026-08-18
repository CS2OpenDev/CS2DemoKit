#region

using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Checking;

#endregion

namespace CS2DemoKit.Analysis.Rules.Hashing;

/// <summary>
///     Hashes a checked expression with resolved identity: the preimage is the canonical
///     serialization (spec §6 row 5 — normalize first, so defines are inlined and durations
///     folded) with every stat reference replaced by the referenced node's own structural
///     hash from <see cref="IStatHashSource" /> (row 6). Names of stat references do not
///     appear in the preimage at all — a bare and a qualified spelling of the same node hash
///     identically, and the same text resolving to different nodes hashes apart.
/// </summary>
public static class ExpressionHasher
{
    private const string PreimagePrefix = "dv2-expr|";

    /// <summary>Computes the SHA-256 resolved-identity hash of a checked expression.</summary>
    /// <param name="expression">The checked (normalized) expression.</param>
    /// <param name="statHashes">Resolves stat references to their nodes' hash bytes.</param>
    /// <returns>The 32-byte hash.</returns>
    public static byte[] ComputeHash(CheckedExpression expression, IStatHashSource statHashes) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(BuildPreimage(expression, statHashes)));

    /// <summary>Computes the hash as lowercase hex, for goldens and diagnostics.</summary>
    /// <param name="expression">The checked (normalized) expression.</param>
    /// <param name="statHashes">Resolves stat references to their nodes' hash bytes.</param>
    /// <returns>The 64-char lowercase hex hash.</returns>
    public static string ComputeHashHex(CheckedExpression expression, IStatHashSource statHashes) =>
        Convert.ToHexStringLower(ComputeHash(expression, statHashes));

    /// <summary>
    ///     Builds the exact preimage text that is hashed — exposed so the preimage-snapshot
    ///     golden (a hash-freeze artifact) and the workbench can show why two nodes
    ///     dedup or don't.
    /// </summary>
    /// <param name="expression">The checked (normalized) expression.</param>
    /// <param name="statHashes">Resolves stat references to their nodes' hash bytes.</param>
    /// <returns>The preimage text.</returns>
    /// <exception cref="InvalidOperationException">
    ///     A reference node lacks a resolution (the AST was not checked by this expression) or
    ///     the stat-hash source returned empty bytes — both programmer misuse, never user input.
    /// </exception>
    public static string BuildPreimage(CheckedExpression expression, IStatHashSource statHashes)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(statHashes);

        StringBuilder text = new(PreimagePrefix, 128);
        CanonicalWriter.Append(text, expression.Root, (builder, reference) =>
        {
            if (!expression.TryGetResolution(reference, out ResolvedReference? resolved))
            {
                throw new InvalidOperationException(
                    $"reference '{reference.Path}' has no resolution in this checked expression");
            }

            if (!resolved.IsStatReference)
            {
                builder.Append("(ref ").Append(resolved.Path).Append(')');
                return;
            }

            ReadOnlyMemory<byte> statHash = statHashes.GetStatHash(resolved);
            if (statHash.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"the stat-hash source returned no hash for stat '{resolved.StatPath}'");
            }

            // Row 6: the referenced node's own hash, never its name. Pseudo-member tails
            // (.count / .set / [n] target position) stay as text after the hash.
            builder.Append("(stat ").Append(Convert.ToHexStringLower(statHash.Span));
            foreach (string segment in resolved.TailSegments)
            {
                builder.Append(' ').Append(segment);
            }

            builder.Append(')');
        });

        return text.ToString();
    }
}
