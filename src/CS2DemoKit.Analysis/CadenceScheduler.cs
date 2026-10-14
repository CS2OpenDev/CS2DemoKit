#region

using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>One cadence of a build with the dispatch types of its anchor and its stop.</summary>
/// <param name="Def">The resolved cadence; <see cref="CadenceDef.Ticks" /> is set.</param>
/// <param name="Anchors">The dispatch types that start it at step 0.</param>
/// <param name="Stops">The dispatch types that stop it; empty for <see cref="CadenceStop.None" />.</param>
internal sealed record CadencePlan(CadenceDef Def, IReadOnlySet<Type> Anchors, IReadOnlySet<Type> Stops);

/// <summary>
///     The per-evaluation state of a build's cadences. The evaluator reports every dispatch key with
///     the frame clock, then drains the steps due after the frame's post-frame events.
/// </summary>
internal sealed class CadenceScheduler
{
    private readonly bool[] _armed;
    private readonly int[] _due;
    private readonly string[] _keys;
    private readonly int[] _periods;
    private readonly CadencePlan[] _plans;
    private readonly int[] _steps;

    internal CadenceScheduler(IReadOnlyList<CadencePlan> plans)
    {
        _plans = [.. plans];
        _armed = new bool[_plans.Length];
        _due = new int[_plans.Length];
        _steps = new int[_plans.Length];
        _keys = _plans.Select(p => p.Def.Key).ToArray();
        _periods = _plans.Select(p => p.Def.Ticks ?? throw new InvalidOperationException(
            $"cadence '{p.Def.Id}' reached the evaluator without a period in ticks")).ToArray();
    }

    internal void Observe(Type key, int tick)
    {
        bool restart = key == typeof(BeginNewMatchEvent);
        for (int i = 0; i < _plans.Length; i++)
        {
            if (restart || _plans[i].Stops.Contains(key))
            {
                _armed[i] = false;
            }

            // After the stop, so a stop that is also an anchor (next_freeze_end) restarts.
            if (_plans[i].Anchors.Contains(key))
            {
                _armed[i] = true;
                _due[i] = tick;
                _steps[i] = 0;
            }
        }
    }

    /// <summary>Appends every step due at or before <paramref name="tick" />, missed steps included.</summary>
    internal void Drain(int tick, List<CadenceTickEvent> into)
    {
        for (int i = 0; i < _plans.Length; i++)
        {
            while (_armed[i] && tick >= _due[i])
            {
                into.Add(new CadenceTickEvent(tick, tick, tick, _keys[i], _steps[i]));
                _due[i] += _periods[i];
                _steps[i]++;
            }
        }
    }
}
