using FiveStack.Utilities;
using Xunit;

namespace FiveStack.Tests;

// Pracc AFK protection (DEAFCS policy): warn at 5, warn again at 9, kick at 10.
public class PracticeAfkTests
{
    private const ulong Anders = 1;
    private const ulong Theft = 2;

    [Fact]
    public void WarnsAt5WarnsAgainAt9AndKicksAt10()
    {
        var afk = new PracticeAfk();
        afk.Active(Anders, 0);

        Assert.Equal(PracticeAfk.Step.None, afk.Check(Anders, 5 * 60 - 1));
        Assert.Equal(PracticeAfk.Step.Warn, afk.Check(Anders, 5 * 60));
        Assert.Equal(PracticeAfk.Step.None, afk.Check(Anders, 8 * 60));
        Assert.Equal(PracticeAfk.Step.FinalWarn, afk.Check(Anders, 9 * 60));
        Assert.Equal(PracticeAfk.Step.None, afk.Check(Anders, 9 * 60 + 30));
        Assert.Equal(PracticeAfk.Step.Kick, afk.Check(Anders, 10 * 60));
    }

    [Fact]
    public void EachStepIsAnsweredOnce()
    {
        var afk = new PracticeAfk();
        afk.Active(Anders, 0);

        Assert.Equal(PracticeAfk.Step.Warn, afk.Check(Anders, 5 * 60));
        Assert.Equal(PracticeAfk.Step.None, afk.Check(Anders, 5 * 60 + 1));
        Assert.Equal(PracticeAfk.Step.None, afk.Check(Anders, 5 * 60 + 2));
    }

    [Fact]
    public void ALateCheckJumpsStraightToTheStepDue()
    {
        var afk = new PracticeAfk();
        afk.Active(Anders, 0);

        // A server that did not tick for a while: no stale warning, the kick.
        Assert.Equal(PracticeAfk.Step.Kick, afk.Check(Anders, 11 * 60));
    }

    [Fact]
    public void AnyActivityStartsTheClockOverAndClearsTheWarnings()
    {
        var afk = new PracticeAfk();
        afk.Active(Anders, 0);
        Assert.Equal(PracticeAfk.Step.Warn, afk.Check(Anders, 5 * 60));

        afk.Active(Anders, 6 * 60);

        Assert.Equal(PracticeAfk.Step.None, afk.Check(Anders, 10 * 60));
        Assert.Equal(PracticeAfk.Step.Warn, afk.Check(Anders, 6 * 60 + 5 * 60));
    }

    [Fact]
    public void EveryPlayerHasTheirOwnClock()
    {
        var afk = new PracticeAfk();
        afk.Active(Anders, 0);
        afk.Active(Theft, 0);

        // Theft keeps practising; Anders walks away.
        for (int minute = 1; minute <= 10; minute++)
        {
            afk.Active(Theft, minute * 60);
        }

        Assert.Equal(PracticeAfk.Step.Kick, afk.Check(Anders, 10 * 60 + 1));
        Assert.Equal(PracticeAfk.Step.None, afk.Check(Theft, 10 * 60 + 1));
    }

    [Fact]
    public void APlayerSeenForTheFirstTimeIsNotJudgedOnUnwatchedTime()
    {
        var afk = new PracticeAfk();

        Assert.Equal(PracticeAfk.Step.None, afk.Check(Anders, 99_999));
        Assert.Equal(0, afk.IdleSeconds(Anders, 99_999));
        Assert.Equal(PracticeAfk.Step.Warn, afk.Check(Anders, 99_999 + 5 * 60));
    }

    [Fact]
    public void ForgettingAPlayerDropsTheirClock()
    {
        var afk = new PracticeAfk();
        afk.Active(Anders, 0);
        afk.Forget(Anders);

        Assert.Equal(PracticeAfk.Step.None, afk.Check(Anders, 20 * 60));
    }

    [Fact]
    public void LiningUpAThrowWhileStandingStillCountsAsActivity()
    {
        // Small aim corrections on a lineup are what practising looks like.
        Assert.True(PracticeAfk.Looked(130.2f, -38.5f, 131.4f, -38.5f));
        Assert.True(PracticeAfk.Looked(130.2f, -38.5f, 130.2f, -40f));
    }

    [Fact]
    public void AimJitterAndASettlingPawnDoNot()
    {
        Assert.False(PracticeAfk.Looked(130.2f, -38.5f, 130.6f, -38.3f));
        Assert.False(PracticeAfk.Moved(0, 0, 0, 1, 1, 0.5f));
    }

    [Fact]
    public void LookingAcrossTheYawWrapCountsTheShortWayRound()
    {
        Assert.False(PracticeAfk.Looked(359.8f, 0, 0.2f, 0));
        Assert.True(PracticeAfk.Looked(359f, 0, 1f, 0));
        Assert.False(PracticeAfk.Looked(-179.8f, 0, 179.9f, 0));
    }

    [Fact]
    public void WalkingCountsAsActivity()
    {
        Assert.True(PracticeAfk.Moved(0, 0, 0, 10, 0, 0));
        Assert.True(PracticeAfk.Moved(0, 0, 0, 0, 0, 5));
    }

    [Fact]
    public void TheThresholdsAreTheAgreedOnes()
    {
        Assert.Equal(5 * 60, PracticeAfk.WarnSeconds);
        Assert.Equal(9 * 60, PracticeAfk.FinalWarnSeconds);
        Assert.Equal(10 * 60, PracticeAfk.KickSeconds);
    }
}
