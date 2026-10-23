using FiveStack.Entities.Practice;
using FiveStack.Utilities;
using Xunit;

namespace FiveStack.Tests;

// E onto a spawn point (DEAFCS): the lineup snap's rules, for spawns.
public class PracticeSpawnSnapTests
{
    private static ThrowSnapshot Spawn(float x, float y, float yaw = 90f) =>
        new() { feet_position = new Vec3(x, y, 0f), yaw = yaw };

    // Mirage-like: five T spawns on a row, five CT spawns far away.
    private static readonly List<ThrowSnapshot> Spawns = new()
    {
        Spawn(1000, 0), Spawn(1064, 0), Spawn(1128, 0), Spawn(1192, 0), Spawn(1256, 0),
        Spawn(-2000, 500, 270f), Spawn(-1936, 500, 270f), Spawn(-1872, 500, 270f),
        Spawn(-1808, 500, 270f), Spawn(-1744, 500, 270f),
    };

    private static float YawTo(Vec3 from, Vec3 to) =>
        (float)(Math.Atan2(to.y - from.y, to.x - from.x) * 180.0 / Math.PI);

    private static float PitchTo(Vec3 eye, Vec3 to)
    {
        float flat = new Vec3(to.x - eye.x, to.y - eye.y, 0f).LengthXY();
        return (float)(-Math.Atan2(to.z - eye.z, flat) * 180.0 / Math.PI);
    }

    [Fact]
    public void ThePointedAtSpawnWinsOverTheNearestOne()
    {
        var standing = new Vec3(1064, -150, 0);
        var eye = new Vec3(standing.x, standing.y, PracticeSpawnSnap.EyeHeight);
        Vec3 target = Spawns[3].feet_position;

        int chosen = PracticeSpawnSnap.Choose(standing, YawTo(eye, target), PitchTo(eye, target), Spawns);

        Assert.Equal(3, chosen);
    }

    [Fact]
    public void WithNothingPointedAtTheNearestSpawnInReachIsTaken()
    {
        var standing = new Vec3(1128, -120, 0);

        // Looking straight up: points at nothing.
        Assert.Equal(2, PracticeSpawnSnap.Choose(standing, 0f, -89f, Spawns));
    }

    [Fact]
    public void ASpawnOutOfReachIsNeverTaken()
    {
        var standing = new Vec3(1000, -400, 0);
        var eye = new Vec3(standing.x, standing.y, PracticeSpawnSnap.EyeHeight);
        Vec3 target = Spawns[0].feet_position;

        Assert.Equal(-1, PracticeSpawnSnap.Choose(standing, YawTo(eye, target), PitchTo(eye, target), Spawns));
        Assert.Equal(-1, PracticeSpawnSnap.Choose(new Vec3(0, 0, 0), 0f, 0f, Spawns));
    }

    [Fact]
    public void StandingOnTheSpawnAlreadyDoesNothing()
    {
        var standing = new Vec3(1064 + 3, 2, 0);

        Assert.Equal(-1, PracticeSpawnSnap.Choose(standing, 0f, -89f, Spawns));
    }

    [Fact]
    public void ASpawnBehindAWallCannotBePointedAt()
    {
        var standing = new Vec3(1064, -150, 0);
        var eye = new Vec3(standing.x, standing.y, PracticeSpawnSnap.EyeHeight);
        Vec3 target = Spawns[3].feet_position;

        int chosen = PracticeSpawnSnap.Choose(
            standing,
            YawTo(eye, target),
            PitchTo(eye, target),
            Spawns,
            visible: (_, point) => point.x != target.x
        );

        // Falls back to the nearest one in reach instead.
        Assert.Equal(1, chosen);
    }

    [Fact]
    public void BothSidesSpawnsAreReachableFromTheirOwnArea()
    {
        Assert.Equal(7, PracticeSpawnSnap.Choose(new Vec3(-1872, 380, 0), 0f, -89f, Spawns));
        Assert.Equal(0, PracticeSpawnSnap.Choose(new Vec3(990, -100, 0), 0f, -89f, Spawns));
    }

    [Fact]
    public void TwoPlayersChooseIndependently()
    {
        int first = PracticeSpawnSnap.Choose(new Vec3(1000, -100, 0), 0f, -89f, Spawns);
        int second = PracticeSpawnSnap.Choose(new Vec3(-1744, 400, 0), 0f, -89f, Spawns);

        Assert.Equal(0, first);
        Assert.Equal(9, second);
    }

    [Fact]
    public void NoSpawnsMeansNoSnap()
    {
        Assert.Equal(-1, PracticeSpawnSnap.Choose(new Vec3(0, 0, 0), 0f, 0f, new List<ThrowSnapshot>()));
    }

    [Fact]
    public void ViewMatchesCs2Angles()
    {
        Vec3 east = PracticeSpawnSnap.View(0f, 0f);
        Vec3 down = PracticeSpawnSnap.View(0f, 89.9f);

        Assert.True(east.x > 0.99f && Math.Abs(east.z) < 0.01f);
        Assert.True(down.z < -0.99f);
    }

    [Fact]
    public void ItUsesTheLineupSnapsReachAndArrival()
    {
        Assert.Equal(220f, PracticeSpawnSnap.ReachUnits);
        Assert.Equal(8f, PracticeLineupUtility.StanceToleranceUnits);
    }
}
