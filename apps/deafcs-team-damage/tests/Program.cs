using DeafCS;
int cases = 0;
void Check(string name, bool expected, bool self, int attacker, int victim, uint type)
{
    cases++;
    if (DamagePolicy.ShouldBlock(self, attacker, victim, type) != expected)
        throw new Exception(name);
}
foreach (int team in new[] { 2, 3 })
{
    foreach (uint weapon in new uint[] { 2, 4, 2048, 2 | 524288 })
    {
        Check("teammate weapon", true, false, team, team, weapon);
        Check("enemy weapon", false, false, team, team == 2 ? 3 : 2, weapon);
        Check("self weapon", false, true, team, team, weapon);
    }
    foreach (uint other in new uint[] { 0, 8, 64, 4096, 32, 1, 16384, 256 })
    {
        Check("other teammate damage", false, false, team, team, other);
        Check("other self damage", false, true, team, team, other);
    }
}
foreach (int team in new[] { 0, 1 })
    Check("non-playing team", false, false, team, team, 2);
Console.WriteLine($"PASS: {cases} damage-policy cases");
