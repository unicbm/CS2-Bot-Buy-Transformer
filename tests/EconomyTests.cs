using BotBuyPatch;
using Xunit;

public sealed class EconomyTests
{
    [Theory]
    [InlineData(16000, 3000, 2700, true)]
    [InlineData(16000, 3000, 3100, true)]
    [InlineData(5700, 3000, 2700, true)]
    [InlineData(5699, 3000, 2700, false)]
    [InlineData(3000, 3000, 2700, false)]
    [InlineData(2900, 3100, 2700, false)]
    [InlineData(2800, 0, 3100, false)]
    [InlineData(16000, 3000, -1, false)]
    public void AssistanceClosesTheCashGapWithoutImpoverishingDonor(int donor, int recipient, int price, bool expected)
        => Assert.Equal(expected, BotBuyEconomy.CanSponsor(donor, recipient, price));

    [Fact]
    public void RepeatedDropsUseRemainingFundsAndActualRiflePrice()
    {
        int cash = 12000;
        foreach (int recipient in new[] { 1000, 2000 })
        {
            Assert.True(BotBuyEconomy.CanSponsor(cash, recipient, 3100));
            cash -= 3100;
        }
        Assert.False(BotBuyEconomy.CanSponsor(cash, 3000, 3100));
        Assert.True(BotBuyEconomy.CanSponsor(cash, 3000, 2700));
    }

    [Theory]
    [InlineData(false, 1399, 1)]
    [InlineData(false, 1400, 2)]
    [InlineData(false, 3599, 2)]
    [InlineData(false, 3600, 3)]
    [InlineData(false, 16200, 3)]
    [InlineData(true, 16200, 0)]
    public void EconomyUsesPrePurchaseResources(bool pistol, double resources, int expected)
        => Assert.Equal((TeamEconomyClass)expected, BotBuyEconomy.Classify(pistol, resources, new()));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(4, true)]
    public void ReplaySkipIsStillOwnership(int planCount, bool owned)
        => Assert.Equal(owned, BotBuyEconomy.OwnsReplayBuy(planCount));

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, false, true)]
    public void ReplayDonorsCanHelpHumansWithoutChangingBotReplayLoadouts(
        bool donorReplay, bool recipientReplay, bool human, bool expected)
        => Assert.Equal(expected, BotBuyEconomy.AllowsAssistance(donorReplay, recipientReplay, human));

    [Theory]
    [InlineData(2700)]
    [InlineData(2900)]
    [InlineData(3100)]
    public void EightThousandDonorCanHelpHumanWithTwentyEightHundred(int price)
        => Assert.True(BotBuyEconomy.AllowsAssistance(true, false, true)
            && BotBuyEconomy.CanSponsor(8000, 2800, price));
}
