namespace DeafCS;

internal static class DamagePolicy
{
    // SwiftlyS2 v1.4.14 DamageTypes_t: BULLET=2, SLASH=4, BUCKSHOT=2048.
    // Keep the decision independent of native entities so the rule can be tested.
    private const uint WeaponDamage = 2u | 4u | 2048u;

    internal static bool ShouldBlock(bool sameEntity, int attackerTeam,
        int victimTeam, uint damageType) =>
        !sameEntity && attackerTeam is 2 or 3 && attackerTeam == victimTeam &&
        (damageType & WeaponDamage) != 0;
}
