using System.Reflection;
using FiveStack;
using FiveStack.Entities;
using FiveStack.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using Xunit;

// Env vars and plugin statics are process-wide, so these run serially with the other hibernation tests.
[Collection("SwiftlyStatics")]
public class StayAwakeTests : IDisposable
{
    private readonly string? _oldMatchId = Environment.GetEnvironmentVariable("MATCH_ID");
    private readonly string? _oldHibernate = Environment.GetEnvironmentVariable("HIBERNATE_WHEN_EMPTY");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MATCH_ID", _oldMatchId);
        Environment.SetEnvironmentVariable("HIBERNATE_WHEN_EMPTY", _oldHibernate);
    }

    // engineDefault: what sv_hibernate_when_empty reads before the plugin touches it
    private static (MatchService service, HibernationTests.FakeCore cvar) Make(bool engineDefault)
    {
        var root = DispatchProxy.Create<ISwiftlyCore, HibernationTests.FakeCore>();
        var fake = (HibernationTests.FakeCore)(object)root;
        _ = root.ConVar.Find<bool>("sv_hibernate_when_empty"); // creates the child proxies
        var cvar = fake.Child("get_ConVar").Child("Find");
        cvar.Returns["get_Value"] = engineDefault;

        var env = new EnvironmentService(
            root,
            NullLogger<EnvironmentService>.Instance,
            Options.Create(new FiveStackConfig())
        );
        var service = new MatchService(root, NullLogger<MatchService>.Instance, null!, env, null!);
        return (service, cvar);
    }

    private static int SetInternalCalls(HibernationTests.FakeCore cvar) =>
        cvar.Calls.Count(c => c == "SetInternal");

    private static void Env(string? matchId, string? hibernate)
    {
        Environment.SetEnvironmentVariable("MATCH_ID", matchId);
        Environment.SetEnvironmentVariable("HIBERNATE_WHEN_EMPTY", hibernate);
    }

    [Fact]
    public void Default_DeafcsEnv_NeverHandsHibernationBack()
    {
        // The deployed API sends no MATCH_ID and HIBERNATE_WHEN_EMPTY is unset.
        Env(null, null);
        var (service, cvar) = Make(engineDefault: true); // match arrived before server.cfg ran

        service.StayAwake(true);
        Assert.Equal(false, cvar.Returns["get_Value"]);

        service.StayAwake(false); // match cleared
        Assert.Equal(false, cvar.Returns["get_Value"]);
        Assert.Equal(1, SetInternalCalls(cvar));
    }

    [Fact]
    public void OptIn_NonMatchServer_HibernatesAgainAfterMatchClears()
    {
        Env(null, "true");
        var (service, cvar) = Make(engineDefault: true);

        service.StayAwake(true);
        Assert.Equal(false, cvar.Returns["get_Value"]);

        service.StayAwake(false);
        Assert.Equal(true, cvar.Returns["get_Value"]);
    }

    [Fact]
    public void MatchServer_NeverHibernatesAgain_EvenWhenOptedIn()
    {
        Env("11111111-1111-1111-1111-111111111111", "true");
        var (service, cvar) = Make(engineDefault: true);

        service.StayAwake(true);
        service.StayAwake(false);

        Assert.Equal(false, cvar.Returns["get_Value"]);
    }

    [Fact]
    public void AlreadyAwake_StayAwakeTouchesNothing()
    {
        Env(null, "true");
        var (service, cvar) = Make(engineDefault: false); // server.cfg already set 0

        service.StayAwake(true);
        service.StayAwake(false);

        Assert.Equal(0, SetInternalCalls(cvar));
    }

    [Fact]
    public void ReleaseWithoutHold_DoesNothing()
    {
        Env(null, "true");
        var (service, cvar) = Make(engineDefault: true);

        service.StayAwake(false);

        Assert.Equal(0, SetInternalCalls(cvar));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 14_999, false)]
    [InlineData(0, 15_000, true)]
    [InlineData(0, 90_000, true)]
    [InlineData(1_000, 1_000, false)]
    public void HeartbeatDue_FollowsFifteenSecondCadence(long last, long now, bool due)
    {
        Assert.Equal(due, HibernationUtility.HeartbeatDue(now, last, 15_000));
    }
}
