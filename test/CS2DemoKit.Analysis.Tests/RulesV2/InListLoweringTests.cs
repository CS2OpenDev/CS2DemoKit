#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Parsing;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     <c>x in [a, b]</c> has no v1 form, so the writer lowers it to a disjunction of equalities the
///     v1 compiler evaluates, and an empty list to <c>false</c>. The baked filter of
///     <c>grenade_released</c> is the first shipped use.
/// </summary>
[Category("Unit")]
public class InListLoweringTests
{
    [Test]
    public async Task InList_WritesADisjunction_AndAnEmptyListWritesFalse()
    {
        ExpressionNode members = ExpressionParser.Parse("event.Weapon in [\"a\", \"b\"]").Require();
        await Assert.That(V1ExpressionWriter.Write(members))
            .IsEqualTo("((event.Weapon == \"a\") || (event.Weapon == \"b\"))");

        ExpressionNode numbers = ExpressionParser.Parse("event.Attacker in [1, 2] and event.Attacker != 3").Require();
        await Assert.That(V1ExpressionWriter.Write(numbers))
            .IsEqualTo("(((event.Attacker == 1) || (event.Attacker == 2)) && (event.Attacker != 3))");

        ExpressionNode empty = ExpressionParser.Parse("event.Weapon in []").Require();
        await Assert.That(V1ExpressionWriter.Write(empty)).IsEqualTo("false");
    }

    [Test]
    public async Task TheLoweredDisjunction_CompilesAndTestsMembership()
    {
        EventRegistration reg = EventRegistry.Build().GetEvent("player_death")!;
        string v1 = V1ExpressionWriter.Write(ExpressionParser.Parse("event.Weapon in [\"awp\", \"ak47\"]").Require());
        Func<PlayerDeathEvent, bool> predicate =
            (Func<PlayerDeathEvent, bool>)ExpressionCompiler.CompileEventCondition(v1, reg.EventType, reg.Fields);

        await Assert.That(predicate((PlayerDeathEvent)TestGameEvents.PlayerDeath(weapon: "awp").Payload!)).IsTrue();
        await Assert.That(predicate((PlayerDeathEvent)TestGameEvents.PlayerDeath(weapon: "glock").Payload!)).IsFalse();
    }
}
