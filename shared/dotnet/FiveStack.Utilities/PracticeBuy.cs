using System.Globalization;

namespace FiveStack.Utilities;

// Buying on a practice server (DEAFCS).
//
// Upstream 5Stack switches buying off in practice (mp_buy_anywhere 0,
// mp_buytime 0, buy zones despawned): grenades are handed back as they are
// thrown, so there was nothing to buy. DEAFCS players also want a weapon in
// hand while they practise, so the normal buy menu stays open everywhere and
// for the whole round, and the account is kept topped up, because practice
// rounds never end and mp_afterroundmoney would never refill it.
public static class PracticeBuy
{
    // Longer than any practice round (mp_roundtime 60 = 3600 s), so the buy
    // window never closes before the round itself is reloaded.
    public const int BuyTimeSeconds = 60000;

    // mp_maxmoney / mp_startmoney in the practice cfg.
    public const int Money = 65535;

    // Below this the account is filled back to Money: more than any one
    // loadout costs, so a player never meets "not enough money" mid-practice.
    public const int TopUpBelow = 16000;

    public static bool ShouldTopUp(int account)
    {
        return account < TopUpBelow;
    }

    /// <summary>
    /// Whether a value read back from mp_buytime has been put back under the
    /// practice window (a map's mode cfg landing after ours does that).
    /// Anything unreadable counts as too short.
    /// </summary>
    public static bool BuyTimeTooShort(string? value)
    {
        return !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds)
            || seconds < BuyTimeSeconds;
    }

    /// <summary>Whether mp_buy_anywhere has been turned back off.</summary>
    public static bool BuyAnywhereOff(string? value)
    {
        return value?.Trim() != "1";
    }
}
