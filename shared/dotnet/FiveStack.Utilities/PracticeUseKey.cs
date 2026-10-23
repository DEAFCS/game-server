namespace FiveStack.Utilities;

// What the legend's E row says, in the order E itself decides what to do
// (PracticeKeys.Use): markers hidden, next throw from this spot, snap onto a
// saved lineup, snap onto a shown spawn point (DEAFCS), otherwise nothing --
// the row stays and dims, because the key is still worth knowing about.
//
// The spawn step is the DEAFCS addition: E snapped onto spawn points from
// ed42867, but the row only knew about lineups and stayed dimmed beside a
// spawn it would snap to (seen in the 2026-10-18 controlled test).
public static class PracticeUseKey
{
    public const string NearbyLabel = "Snap onto a spot nearby";
    public const string SpotLabel = "Snap onto the spot";
    public const string SpawnLabel = "Snap onto the spawn";

    public readonly record struct Row(string Label, bool Dim);

    /// <param name="cleared">The player hid the markers: E does nothing.</param>
    /// <param name="cycleIndex">Zero-based position of the loaded throw when E would step to the next one here, else null.</param>
    /// <param name="cycleCount">How many throws share this spot.</param>
    /// <param name="lineupAway">Flat distance to the nearest saved lineup, or null for none.</param>
    /// <param name="spawnChoice">PracticeSpawnSnap.Choose's answer, or -1 (also when spawns are not shown).</param>
    public static Row Describe(
        bool cleared,
        int? cycleIndex,
        int cycleCount,
        float? lineupAway,
        int spawnChoice
    )
    {
        if (cleared)
        {
            return new Row(NearbyLabel, true);
        }

        if (cycleIndex is int index)
        {
            return new Row($"Next throw from here · {index + 1} of {cycleCount}", false);
        }

        if (
            lineupAway is float away
            && away <= PracticeSpawnSnap.ReachUnits
            && away > PracticeLineupUtility.StanceToleranceUnits
        )
        {
            return new Row(SpotLabel, false);
        }

        // A saved lineup in reach that the player already stands on answers E
        // (it does nothing), so the spawn is only offered when no lineup is.
        bool lineupClaims = lineupAway is float near && near <= PracticeSpawnSnap.ReachUnits;

        if (!lineupClaims && spawnChoice >= 0)
        {
            return new Row(SpawnLabel, false);
        }

        return new Row(NearbyLabel, true);
    }
}
