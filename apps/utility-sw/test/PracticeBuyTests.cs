using FiveStack.Utilities;
using Xunit;

namespace FiveStack.Tests;

// Buying in Pracc (DEAFCS): open everywhere, all round, never out of money.
public class PracticeBuyTests
{
    [Fact]
    public void TheBuyWindowOutlastsAPracticeRound()
    {
        // mp_roundtime 60 minutes, and the round is reloaded when it runs out.
        Assert.True(PracticeBuy.BuyTimeSeconds > 60 * 60);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("20")]
    [InlineData("45")]
    [InlineData("3600")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    public void AModeCfgClosingTheBuyWindowIsCaught(string? value)
    {
        Assert.True(PracticeBuy.BuyTimeTooShort(value));
    }

    [Theory]
    [InlineData("60000")]
    [InlineData("60000.000000")]
    [InlineData("99999")]
    public void ThePracticeBuyWindowIsLeftAlone(string value)
    {
        Assert.False(PracticeBuy.BuyTimeTooShort(value));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("false", true)]
    [InlineData(null, true)]
    [InlineData("1", false)]
    [InlineData(" 1 ", false)]
    public void BuyAnywhereIsPutBackOnWhenTurnedOff(string? value, bool off)
    {
        Assert.Equal(off, PracticeBuy.BuyAnywhereOff(value));
    }

    [Fact]
    public void TheAccountIsToppedUpBeforeAnyLoadoutCanRunOut()
    {
        // An AWP (4750), kevlar and helmet (1000) and a full set of utility
        // is about 7000; the top-up line sits well above that.
        Assert.True(PracticeBuy.ShouldTopUp(0));
        Assert.True(PracticeBuy.ShouldTopUp(PracticeBuy.TopUpBelow - 1));
        Assert.False(PracticeBuy.ShouldTopUp(PracticeBuy.TopUpBelow));
        Assert.False(PracticeBuy.ShouldTopUp(PracticeBuy.Money));
        Assert.True(PracticeBuy.TopUpBelow >= 7000 * 2);
    }

    [Fact]
    public void TheTopUpMatchesTheConfiguredMaximum()
    {
        Assert.Equal(65535, PracticeBuy.Money);
    }
}
