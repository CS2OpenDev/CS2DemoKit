#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The truth table of <see cref="PawnLookup.IsAlive" /> (issue #58): alive means
///     <c>m_lifeState</c> is 0 and <c>m_iHealth</c> is above zero, and a field that has not been
///     networked reads as not alive rather than guessing.
/// </summary>
[Category("Unit")]
public class PawnLookupIsAliveTests
{
    [Test]
    [Arguments(0, 100, true)]
    [Arguments(0, 1, true)]
    [Arguments(0, 0, false)]
    [Arguments(1, 100, false)]
    [Arguments(2, 100, false)]
    [Arguments(2, 0, false)]
    public async Task IsAlive_ReadsLifeStateAndHealth(int lifeState, int health, bool alive)
    {
        EntityState pawn = Pawn();
        pawn.SetInt("m_lifeState", lifeState);
        pawn.SetInt("m_iHealth", health);

        await Assert.That(PawnLookup.IsAlive(pawn)).IsEqualTo(alive);
    }

    [Test]
    public async Task IsAlive_IsFalseWhenNothingIsNetworked() =>
        await Assert.That(PawnLookup.IsAlive(Pawn())).IsFalse();

    [Test]
    public async Task IsAlive_IsFalseWhenHealthIsUnseen()
    {
        EntityState pawn = Pawn();
        pawn.SetInt("m_lifeState", 0);

        await Assert.That(PawnLookup.IsAlive(pawn)).IsFalse();
    }

    [Test]
    public async Task IsAlive_IsFalseWhenLifeStateIsUnseen()
    {
        EntityState pawn = Pawn();
        pawn.SetInt("m_iHealth", 100);

        await Assert.That(PawnLookup.IsAlive(pawn)).IsFalse();
    }

    [Test]
    public void IsAlive_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => PawnLookup.IsAlive(null!));

    private static EntityState Pawn() => new("CCSPlayerPawn", 0);
}
