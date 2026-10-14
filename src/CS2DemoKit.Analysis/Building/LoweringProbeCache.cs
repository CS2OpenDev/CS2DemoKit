#region

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     The exclusions the per-player lowering probe produced, by build input. A host builds the same
///     ruleset set once per demo, and the probe's slot-0 materialization is the cost being skipped.
/// </summary>
internal sealed class LoweringProbeCache
{
    private const int Capacity = 8;

    public static LoweringProbeCache Shared { get; } = new();

    private readonly object _gate = new();

    // Most recently used first.
    private readonly List<(UInt128 Key, ExcludedRuleset[] Exclusions)> _entries = [];

    public bool TryGet(UInt128 key, out ExcludedRuleset[] exclusions)
    {
        lock (_gate)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Key == key)
                {
                    (UInt128, ExcludedRuleset[]) hit = _entries[i];
                    _entries.RemoveAt(i);
                    _entries.Insert(0, hit);
                    exclusions = hit.Item2;
                    return true;
                }
            }
        }

        exclusions = [];
        return false;
    }

    public void Set(UInt128 key, ExcludedRuleset[] exclusions)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Key == key);
            _entries.Insert(0, (key, exclusions));
            if (_entries.Count > Capacity)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
        }
    }

    /// <summary>
    ///     The key: every field of every ruleset in the build (positions included, since the cached
    ///     diagnostics carry them), the compiler options, the tick rate and profile, the event and
    ///     net-message registrations, each entity provider's name, class, field and value type, and
    ///     whether a visibility engine is set.
    ///     Null when an expression cannot be serialized, which leaves the probe to run.
    /// </summary>
    public static UInt128? ComputeKey(
        IReadOnlyList<CheckedRuleset> rulesets,
        RulesetCompilerOptions options,
        int tickRate,
        Type profileType,
        EventRegistry registry,
        EntityValueProviderRegistry? entityProviders,
        PerPlayerEntityValueProviderRegistry? perPlayerEntityProviders,
        bool hasVisibilityEngine)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        KeyWriter w = new(hash);
        try
        {
            w.Bool(options.UseEnvLowering);
            w.Int(tickRate);
            w.Str(profileType.FullName);
            w.Bool(hasVisibilityEngine);

            foreach (string name in registry.EventNames)
            {
                w.Str(name);
                w.Str(registry.GetEvent(name)?.EventType.FullName);
            }

            w.Int(-1);
            foreach (string name in registry.NetMessageNames)
            {
                w.Str(name);
                w.Str(registry.GetNetMessage(name)?.PayloadType.FullName);
            }

            w.Int(-1);
            if (entityProviders is not null)
            {
                foreach (IEntityValueProvider p in entityProviders.All)
                {
                    w.Str(p.ContextName);
                    w.Str(p.GetType().FullName);
                    w.Str(p.EntityClass);
                    w.Str(p.FieldName);
                    w.Int((int)p.EmitOn);
                    w.Str(p.DefaultValue?.GetType().FullName);
                }
            }

            w.Int(-1);
            if (perPlayerEntityProviders is not null)
            {
                foreach (IPerPlayerEntityValueProvider p in perPlayerEntityProviders.All)
                {
                    w.Str(p.Name);
                    w.Str(p.GetType().FullName);
                    w.Str(p.EntityClass);
                    w.Str(p.FieldName);
                    w.Str(p.ValueType.FullName);
                }
            }

            w.Int(-1);
            foreach (CheckedRuleset rs in rulesets)
            {
                WriteRuleset(w, rs);
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return MemoryMarshal.Read<UInt128>(digest);
    }

    private static void WriteRuleset(KeyWriter w, CheckedRuleset rs)
    {
        w.Str(rs.Id.Id);
        w.Int((int)rs.Id.Scope);
        w.Int((int)rs.For);
        w.Int(rs.Stats.Count);
        foreach (CheckedStat s in rs.Stats)
        {
            w.Str(s.Ruleset.Id);
            w.Str(s.StatId);
            w.Int((int)s.Kind);
            w.Str(s.ValueType.ToString());
            w.Int((int)s.Scope);
            w.Strs(s.ConcreteEvents);
            w.Expr(s.TriggerCondition);
            w.Expr(s.ValueSelector);
            w.Expr(s.WhileGate);
            w.Int((int)s.Keep);
            w.Int(s.TallyThresholds?.Count ?? -1);
            foreach ((int min, string target) in s.TallyThresholds ?? [])
            {
                w.Int(min);
                w.Str(target);
            }

            w.Int(s.StreakWindow ?? int.MinValue);
            w.Int(s.StreakMinStreak ?? int.MinValue);
            w.Strs(s.BucketKeyParts);
            w.Str(s.BucketReducer);
            w.Strs(s.DeclaredReads);
            w.EntityReads(s.EntityReads);
            w.Str(s.ResolvedView);
            w.Bool(s.SuppressActorBinding);
            w.Str(s.Label);
            w.Pos(s.Position);
            w.Bool(s.Live);
            w.Str(s.RateOf);
            w.Str(s.RatePer);
            w.Str(s.Format);
            w.Int((int)s.Clock);
        }

        w.Int(rs.Highlights.Count);
        foreach (CheckedHighlight h in rs.Highlights)
        {
            w.Str(h.Ruleset.Id);
            w.Str(h.HighlightId);
            w.Int((int)h.Scope);
            w.Int((int)h.CountScope);
            w.Expr(h.When);
            w.Str(h.Title);
            w.Int(h.Score);
            w.Int((int)h.Kind);
            w.Str(h.Group);
            w.Strs(h.DeclaredReads);
            w.EntityReads(h.EntityReads);
            w.Pos(h.Position);
        }

        w.Bool(rs.Show is not null);
        if (rs.Show is { } show)
        {
            w.Pos(show.Position);
            w.Int(show.Scoreboard.Count);
            foreach (ScoreboardEntry e in show.Scoreboard)
            {
                w.Str(e.Stat);
                w.Str(e.Label);
                w.Str(e.Group);
                w.Strs(e.Boards);
                w.Int((int)e.As);
                w.Pos(e.Position);
            }

            w.Int(show.Tables.Count);
            foreach (TableDef t in show.Tables)
            {
                w.Str(t.Name);
                w.Str(t.Per);
                w.Pos(t.Position);
                w.Int(t.Columns.Count);
                foreach (TableColumn c in t.Columns)
                {
                    w.Str(c.Stat);
                    w.Str(c.Label);
                    w.Int((int)c.As);
                    w.Pos(c.Position);
                }
            }
        }
    }

    private readonly struct KeyWriter(IncrementalHash hash)
    {
        public void Int(int value)
        {
            Span<byte> b = stackalloc byte[4];
            MemoryMarshal.Write(b, in value);
            hash.AppendData(b);
        }

        public void Bool(bool value) => Int(value ? 1 : 0);

        // Length-prefixed, with -1 for null, so adjacent fields cannot run together.
        public void Str(string? value)
        {
            Int(value?.Length ?? -1);
            if (value is not null)
            {
                hash.AppendData(MemoryMarshal.AsBytes(value.AsSpan()));
            }
        }

        public void Strs(IReadOnlyList<string>? values)
        {
            Int(values?.Count ?? -1);
            foreach (string v in values ?? [])
            {
                Str(v);
            }
        }

        public void Pos(SourcePosition p)
        {
            Str(p.File);
            Int(p.Line);
            Int(p.Column);
        }

        public void EntityReads(IReadOnlyList<EntityProviderReference> reads)
        {
            Int(reads.Count);
            foreach (EntityProviderReference r in reads)
            {
                Str(r.Path);
                Str(r.ProviderName);
                Str(r.Subject);
                Str(r.RoleSlotField);
                Bool(r.IsSingleton);
                Int(r.LookBackTicks);
            }
        }

        public void Expr(CheckedExpression? e)
        {
            Bool(e is not null);
            if (e is null)
            {
                return;
            }

            Str(e.ResultType.ToString());
            Str(ExpressionHasher.BuildPreimage(e, StatPathSource.Instance));
            Int(e.References.Count);
            foreach (ResolvedReference r in e.References)
            {
                Str(r.Path);
                Str(r.Type.ToString());
            }
        }
    }

    // Stands in a stat reference's path for its node hash, so the preimage names what it reads.
    private sealed class StatPathSource : IStatHashSource
    {
        public static readonly StatPathSource Instance = new();

        public ReadOnlyMemory<byte> GetStatHash(ResolvedReference reference) =>
            Encoding.UTF8.GetBytes(reference.StatPath ?? reference.Path);
    }
}
