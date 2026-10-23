using FiveStack.Entities.Practice;
using FiveStack.Utilities;
using Xunit;

namespace FiveStack.Tests;

// 2026-10-18: Mirage -> Cache in one practice session sent Mirage's places as
// de_cache, because the first walk after the change still saw Mirage's
// env_cs_place entities.
public class MapCalloutsGuardTests
{
    private static MapCalloutPayload Place(string name, float x, float y) =>
        new()
        {
            name = name,
            boxes = new List<MapCalloutBox>
            {
                new() { min = new[] { x, y, 0f }, max = new[] { x + 100f, y + 100f, 128f } },
            },
        };

    private static readonly List<MapCalloutPayload> Mirage = new()
    {
        Place("Jungle", -1200, -600), Place("PalaceAlley", 300, -2000), Place("TRamp", 900, -1500),
    };

    private static readonly List<MapCalloutPayload> Cache = new()
    {
        Place("AMain", 1500, 300), Place("Highway", 200, 1200), Place("Squeaky", 900, 900),
    };

    [Fact]
    public void MiragesPlacesReadAfterSwitchingToCacheAreLeftovers()
    {
        string mirage = MapCalloutsGuard.Signature(Mirage);

        Assert.True(MapCalloutsGuard.IsLeftover("de_cache", Mirage, "de_mirage", mirage));
    }

    [Fact]
    public void CachesOwnPlacesAreReported()
    {
        string mirage = MapCalloutsGuard.Signature(Mirage);

        Assert.False(MapCalloutsGuard.IsLeftover("de_cache", Cache, "de_mirage", mirage));
    }

    [Fact]
    public void MirageToCacheToMirageReportsEachMapsOwnPlaces()
    {
        // Mirage first: nothing before it.
        Assert.False(MapCalloutsGuard.IsLeftover("de_mirage", Mirage, null, null));
        string previous = MapCalloutsGuard.Signature(Mirage);

        // Cache: the stale walk is refused, the real one goes.
        Assert.True(MapCalloutsGuard.IsLeftover("de_cache", Mirage, "de_mirage", previous));
        Assert.False(MapCalloutsGuard.IsLeftover("de_cache", Cache, "de_mirage", previous));
        previous = MapCalloutsGuard.Signature(Cache);

        // And back: Cache's leftovers refused, Mirage's own places go.
        Assert.True(MapCalloutsGuard.IsLeftover("de_mirage", Cache, "de_cache", previous));
        Assert.False(MapCalloutsGuard.IsLeftover("de_mirage", Mirage, "de_cache", previous));
    }

    [Fact]
    public void ReloadingTheSameMapIsNeverLeftovers()
    {
        string mirage = MapCalloutsGuard.Signature(Mirage);

        Assert.False(MapCalloutsGuard.IsLeftover("de_mirage", Mirage, "de_mirage", mirage));
        Assert.False(MapCalloutsGuard.IsLeftover("DE_MIRAGE", Mirage, "de_mirage", mirage));
    }

    [Fact]
    public void TheSignatureIgnoresOrderCaseAndFloatNoise()
    {
        var shuffled = new List<MapCalloutPayload>
        {
            Place("tramp", 900.2f, -1500.3f), Place("JUNGLE", -1200, -600), Place("PalaceAlley", 299.8f, -2000),
        };

        Assert.Equal(MapCalloutsGuard.Signature(Mirage), MapCalloutsGuard.Signature(shuffled));
    }

    [Fact]
    public void OneMovedBoxMakesItADifferentMap()
    {
        var moved = new List<MapCalloutPayload>(Mirage) { Place("Connector", 0, 0) };

        Assert.False(MapCalloutsGuard.IsLeftover("de_cache", moved, "de_mirage", MapCalloutsGuard.Signature(Mirage)));
    }

    [Fact]
    public void AnEmptyWalkIsNotLeftoversItIsNotYet()
    {
        Assert.False(MapCalloutsGuard.IsLeftover("de_cache", new List<MapCalloutPayload>(), "de_mirage", "x"));
    }
}
