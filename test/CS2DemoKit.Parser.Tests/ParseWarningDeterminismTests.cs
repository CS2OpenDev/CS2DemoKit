using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Determinism of <see cref="ParsedDemo.Warnings" />: the same demo must produce the same
///     warnings regardless of what ran before it, or on which thread.
///     <para>
///         Issue #13 reported a count that varied across identical parses and was reproducible
///         only by chance. Both mechanisms below reproduce deterministically once the channel is
///         driven directly rather than waited on.
///     </para>
/// </summary>
[Category("Unit")]
public class ParseWarningDeterminismTests
{
    /// <summary>
    ///     The residue path. A parse that throws never reaches <c>Drain</c>, so its warnings stay
    ///     on the thread; seeding one stands in for that dead parse without needing a demo that
    ///     throws at the right moment.
    /// </summary>
    [Test]
    public async Task Parse_AfterAThrownParseLeftResidue_DoesNotInheritIt()
    {
        string path = DemoTestHelper.RequireDemo();

        ParseDiagnostics.Reset();
        ParsedDemo clean = MemoryMappedDemoSource.ParseFile(path);
        int baseline = clean.Warnings.Count;

        ParseDiagnostics.Warn("test-residue", "left behind by a parse that threw");
        ParseDiagnostics.Warn("test-residue", "and a second one");

        ParsedDemo after = MemoryMappedDemoSource.ParseFile(path);

        await Assert.That(after.Warnings.Count).IsEqualTo(baseline)
            .Because("a parse's warning count must not depend on what ran before it on this thread");
        await Assert.That(after.Warnings.Any(w => w.Code == "test-residue")).IsFalse()
            .Because("the residue belonged to a different parse");
    }

    [Test]
    public async Task Parse_RepeatedOnTheSameThread_ProducesTheSameWarnings()
    {
        string path = DemoTestHelper.RequireDemo();

        ParsedDemo first = MemoryMappedDemoSource.ParseFile(path);
        ParsedDemo second = MemoryMappedDemoSource.ParseFile(path);

        // Joined rather than compared as collections: this must be order-sensitive, and the
        // collection assertions in this suite are not.
        string Fingerprint(ParsedDemo d) =>
            string.Join("\n", d.Warnings.Select(w => $"{w.Code}|{w.Message}|{w.Count}"));

        await Assert.That(second.Warnings.Count).IsEqualTo(first.Warnings.Count);
        await Assert.That(Fingerprint(second)).IsEqualTo(Fingerprint(first))
            .Because("identical input, identical diagnostics, in the same order");
    }

    /// <summary>
    ///     Drop totals are merged from per-thread partials, so the dictionary's enumeration order
    ///     reflects thread completion order. Two dictionaries with the same contents inserted in
    ///     opposite orders stand in for two runs that scheduled differently.
    /// </summary>
    [Test]
    public async Task RankDropTypes_IsIndependentOfInsertionOrder()
    {
        // Every count tied, so ordering is decided entirely by the tie-break.
        string[] types = ["svc_Alpha", "svc_Bravo", "svc_Charlie", "svc_Delta", "svc_Echo"];

        Dictionary<string, int> forward = new();
        foreach (string t in types)
        {
            forward[t] = 3;
        }

        Dictionary<string, int> reversed = new();
        foreach (string t in types.Reverse())
        {
            reversed[t] = 3;
        }

        string a = string.Join(",", DemoParser.RankDropTypes(forward).Select(kv => kv.Key));
        string b = string.Join(",", DemoParser.RankDropTypes(reversed).Select(kv => kv.Key));

        await Assert.That(a).IsEqualTo(b)
            .Because("which types land in the emitted top 8 must not depend on thread scheduling");
        await Assert.That(a).IsEqualTo(string.Join(",", types))
            .Because("the tie-break is the type name, ordinal");
    }

    // Count still dominates the name: the tie-break must only decide ties.
    [Test]
    public async Task RankDropTypes_OrdersByCountBeforeName()
    {
        Dictionary<string, int> totals = new() { ["svc_Zulu"] = 90, ["svc_Alpha"] = 2, ["svc_Mike"] = 40 };

        string ranked = string.Join(",", DemoParser.RankDropTypes(totals).Select(kv => kv.Key));

        await Assert.That(ranked).IsEqualTo("svc_Zulu,svc_Mike,svc_Alpha");
    }
}
