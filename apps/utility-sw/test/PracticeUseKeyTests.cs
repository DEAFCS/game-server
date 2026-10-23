using FiveStack.Utilities;
using Xunit;

namespace FiveStack.Tests;

// The legend's E row, in E's own order. 2026-10-18: it stayed dimmed beside a
// spawn point E snapped to, because it only knew about saved lineups.
public class PracticeUseKeyTests
{
    [Fact]
    public void BesideASpawnWithNoLineupInReachTheRowIsLit()
    {
        PracticeUseKey.Row row = PracticeUseKey.Describe(false, null, 0, lineupAway: null, spawnChoice: 3);

        Assert.Equal(PracticeUseKey.SpawnLabel, row.Label);
        Assert.False(row.Dim);
    }

    [Fact]
    public void ALineupFarAwayDoesNotHideANearbySpawn()
    {
        PracticeUseKey.Row row = PracticeUseKey.Describe(false, null, 0, lineupAway: 900f, spawnChoice: 0);

        Assert.Equal(PracticeUseKey.SpawnLabel, row.Label);
        Assert.False(row.Dim);
    }

    [Fact]
    public void ASavedLineupInReachStillWinsOverASpawn()
    {
        PracticeUseKey.Row row = PracticeUseKey.Describe(false, null, 0, lineupAway: 120f, spawnChoice: 2);

        Assert.Equal(PracticeUseKey.SpotLabel, row.Label);
        Assert.False(row.Dim);
    }

    [Fact]
    public void StandingOnALineupBesideASpawnIsTheLineupsAnswerNotTheSpawns()
    {
        // E does nothing there (StandOnNearest says the lineup answered), so
        // the row must not promise a spawn snap.
        PracticeUseKey.Row row = PracticeUseKey.Describe(false, null, 0, lineupAway: 3f, spawnChoice: 1);

        Assert.Equal(PracticeUseKey.NearbyLabel, row.Label);
        Assert.True(row.Dim);
    }

    [Fact]
    public void TheNextThrowHereComesFirst()
    {
        PracticeUseKey.Row row = PracticeUseKey.Describe(false, cycleIndex: 1, cycleCount: 3, lineupAway: 0f, spawnChoice: 4);

        Assert.Equal("Next throw from here · 2 of 3", row.Label);
        Assert.False(row.Dim);
    }

    [Fact]
    public void HiddenMarkersDimTheRowWhateverIsNear()
    {
        PracticeUseKey.Row row = PracticeUseKey.Describe(true, 0, 2, 50f, 1);

        Assert.Equal(PracticeUseKey.NearbyLabel, row.Label);
        Assert.True(row.Dim);
    }

    [Fact]
    public void NothingInReachDimsTheRow()
    {
        PracticeUseKey.Row row = PracticeUseKey.Describe(false, null, 0, lineupAway: 500f, spawnChoice: -1);

        Assert.Equal(PracticeUseKey.NearbyLabel, row.Label);
        Assert.True(row.Dim);
    }

    [Fact]
    public void ALineupWithinReachButNotStoodOnIsLitAsBefore()
    {
        // Unchanged lineup behaviour: the old row lit in exactly this band.
        Assert.False(PracticeUseKey.Describe(false, null, 0, 219f, -1).Dim);
        Assert.True(PracticeUseKey.Describe(false, null, 0, 221f, -1).Dim);
        Assert.True(PracticeUseKey.Describe(false, null, 0, 8f, -1).Dim);
        Assert.False(PracticeUseKey.Describe(false, null, 0, 8.5f, -1).Dim);
    }
}
