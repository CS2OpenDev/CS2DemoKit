#region

using CS2DemoKit.Analysis.Rules.Checking;

#endregion

namespace CS2DemoKit.Analysis.Rules.Hashing;

/// <summary>
///     Supplies the resolved structural hash of the node a stat reference points at —
///     preimage row 6 of spec §6: every stat reference inside an AST contributes the
///     referenced node's own hash, NOT its name, so identical text resolving to different
///     nodes hashes differently (text-keyed hashing is corruption under v2 scoped
///     namespaces). The v2 compiler implements this over its node graph, hashing
///     dependencies first (stat-reference cycles are a build error, so the recursion
///     terminates); tests fake it.
/// </summary>
public interface IStatHashSource
{
    /// <summary>Returns the referenced stat node's resolved structural hash bytes.</summary>
    /// <param name="reference">
    ///     A resolution with <see cref="ResolvedReference.IsStatReference" /> true; key it by
    ///     <see cref="ResolvedReference.StatPath" /> (bare and qualified spellings of the same
    ///     node must return the same bytes).
    /// </param>
    /// <returns>The node's hash bytes (non-empty; typically 32 bytes of SHA-256).</returns>
    ReadOnlyMemory<byte> GetStatHash(ResolvedReference reference);
}
