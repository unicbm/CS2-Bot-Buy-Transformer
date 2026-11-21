namespace BotBuyPatch;

internal static class BotBuyEconomy
{
    internal static TeamEconomyClass Classify(bool pistol, double availablePerPlayer, EconomyThresholds thresholds)
        => pistol ? TeamEconomyClass.Pistol
            : availablePerPlayer < thresholds.EcoMaxExclusive ? TeamEconomyClass.Eco
            : availablePerPlayer < thresholds.ForceMaxExclusive ? TeamEconomyClass.Force
            : TeamEconomyClass.Full;

    // Own equipment is bought first. Assistance may close the remaining cash gap,
    // but must not make the donor poorer than the teammate receiving the rifle.
    internal static bool CanSponsor(int donorMoney, int recipientMoney, int price)
        => price > 0 && recipientMoney >= 0 && donorMoney - (long)price >= recipientMoney;

    internal static bool OwnsReplayBuy(int itemCount) => itemCount >= 0;

    internal static bool AllowsAssistance(bool donorReplayOwned, bool recipientReplayOwned, bool humanControlled)
        => humanControlled || (!donorReplayOwned && !recipientReplayOwned);
}
