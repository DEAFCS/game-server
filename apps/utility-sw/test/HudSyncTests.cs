using FiveStack.Utilities;
using Xunit;

namespace FiveStack.Tests;

// The 2026-10-17 two-player incident: bots were placed and removed, both
// players' practice HUD went blank, the wheel came up empty, and reconnecting
// did not bring either back. The engine keeps each player's HUD overrides on
// the layout entity by slot; the plugin keeps a cache of what it sent and
// suppresses anything unchanged. These model the engine side closely enough to
// show the cache and the heal doing the right thing when the two part.
public class HudSyncTests
{
    private const string Root = "root";
    private const string Shown = "shown";
    private const string Layout = "nade3_bar";

    // Per-slot overrides, as the layout entity holds them.
    private sealed class FakeEngine
    {
        public readonly Dictionary<int, Dictionary<string, string>> Variables = new();
        public readonly Dictionary<int, Dictionary<(string, string), bool>> Classes = new();
        public int Writes;

        public void SetVariable(int slot, string name, string value)
        {
            Writes++;
            Slot(Variables, slot)[name] = value;
        }

        public void SetClass(int slot, string element, string name, bool on)
        {
            Writes++;
            Slot(Classes, slot)[(element, name)] = on;
        }

        public bool? HasClass(int slot, string element, string name)
        {
            return Classes.TryGetValue(slot, out var classes) && classes.TryGetValue((element, name), out bool on)
                ? on
                : null;
        }

        // A slot changing hands: its overrides go with whoever had it.
        public void DropSlot(int slot)
        {
            Variables.Remove(slot);
            Classes.Remove(slot);
        }

        public void DropAll()
        {
            Variables.Clear();
            Classes.Clear();
        }

        private static Dictionary<TKey, TValue> Slot<TKey, TValue>(
            Dictionary<int, Dictionary<TKey, TValue>> all,
            int slot
        ) where TKey : notnull
        {
            if (!all.TryGetValue(slot, out var mine))
            {
                mine = new Dictionary<TKey, TValue>();
                all[slot] = mine;
            }

            return mine;
        }
    }

    // HudSurface without the entity: the same cache in front of the same writes.
    private sealed class Surface
    {
        private readonly FakeEngine _engine;
        public readonly int Slot;
        public HudDiffCache Sent = new();

        public Surface(FakeEngine engine, int slot)
        {
            _engine = engine;
            Slot = slot;
        }

        public void Set(string name, string value)
        {
            if (Sent.Variable(name, value))
            {
                _engine.SetVariable(Slot, name, value);
            }
        }

        public void Class(string element, string name, bool on)
        {
            if (Sent.Class(element, name, on))
            {
                _engine.SetClass(Slot, element, name, on);
            }
        }

        // What a template's Apply plus Show amounts to.
        public void ShowBar(string title)
        {
            Set("title", title);
            Class("compass", "p3", true);
            Class(Root, Shown, true);
        }

        public bool Lost()
        {
            return HudHeal.Lost(Sent.Holds(Root, Shown), _engine.HasClass(Slot, Root, Shown));
        }

        public bool EngineShowsBar()
        {
            return _engine.HasClass(Slot, Root, Shown) == true
                && _engine.Variables.TryGetValue(Slot, out var vars)
                && vars.ContainsKey("title");
        }
    }

    // What HudKit.Heal does for one surface.
    private static bool Heal(HudHeal heal, Surface surface, int now)
    {
        if (!heal.ShouldResend(surface.Slot, Layout, surface.Lost(), now))
        {
            return false;
        }

        surface.Sent.Invalidate();
        surface.ShowBar("Mirage");

        return true;
    }

    [Fact]
    public void AStaleCacheSendsNothingAfterTheEngineDropsTheSlot()
    {
        var engine = new FakeEngine();
        var surface = new Surface(engine, 4);

        surface.ShowBar("Mirage");
        engine.DropSlot(4);

        int before = engine.Writes;
        surface.ShowBar("Mirage");

        // The incident: every value is "unchanged", so nothing goes out.
        Assert.Equal(before, engine.Writes);
        Assert.False(surface.EngineShowsBar());
        Assert.True(surface.Lost());
    }

    [Fact]
    public void AReconnectStartsTheSlotOverWhetherOrNotThePlayerResolved()
    {
        var engine = new FakeEngine();
        var surface = new Surface(engine, 4);
        surface.ShowBar("Mirage");

        // Disconnect: the engine lets the slot go; HudKit.Forget now runs
        // unconditionally, which is a fresh surface for whoever comes next.
        engine.DropSlot(4);
        var reconnected = new Surface(engine, 4);
        reconnected.ShowBar("Mirage");

        Assert.True(reconnected.EngineShowsBar());
        Assert.False(reconnected.Lost());
    }

    [Fact]
    public void TheHealPutsBackASurfaceWhoseEngineStateWentMissing()
    {
        var engine = new FakeEngine();
        var heal = new HudHeal();
        var surface = new Surface(engine, 4);
        surface.ShowBar("Mirage");

        Assert.False(Heal(heal, surface, 100));

        engine.DropSlot(4);

        Assert.True(Heal(heal, surface, 101));
        Assert.True(surface.EngineShowsBar());
        Assert.False(Heal(heal, surface, 102));
    }

    [Fact]
    public void BotsComingAndGoingLeaveBothPlayersAlone()
    {
        var engine = new FakeEngine();
        var heal = new HudHeal();
        var host = new Surface(engine, 3);
        var friend = new Surface(engine, 4);
        host.ShowBar("Mirage");
        friend.ShowBar("Mirage");

        // Six bots take slots 5..10 and are kicked.
        for (int bot = 5; bot <= 10; bot++)
        {
            engine.SetVariable(bot, "title", "");
            engine.DropSlot(bot);
        }

        Assert.False(Heal(heal, host, 200));
        Assert.False(Heal(heal, friend, 200));
        Assert.True(host.EngineShowsBar());
        Assert.True(friend.EngineShowsBar());
    }

    [Fact]
    public void BothPlayersComeBackWhenTheEngineLetsEverySlotGo()
    {
        var engine = new FakeEngine();
        var heal = new HudHeal();
        var host = new Surface(engine, 3);
        var friend = new Surface(engine, 4);
        host.ShowBar("Mirage");
        friend.ShowBar("Mirage");

        // What both players saw after the bots went: nothing held for anyone.
        engine.DropAll();

        Assert.True(Heal(heal, host, 300));
        Assert.True(Heal(heal, friend, 300));
        Assert.True(host.EngineShowsBar());
        Assert.True(friend.EngineShowsBar());
    }

    [Fact]
    public void TwoPlayersOnOneSessionAreHealedSeparately()
    {
        var engine = new FakeEngine();
        var heal = new HudHeal();
        var host = new Surface(engine, 3);
        var friend = new Surface(engine, 4);
        host.ShowBar("Mirage");
        friend.ShowBar("Inferno");

        engine.DropSlot(4);

        Assert.False(Heal(heal, host, 400));
        Assert.True(Heal(heal, friend, 400));
        Assert.Equal("Mirage", engine.Variables[3]["title"]);
        Assert.True(friend.EngineShowsBar());
    }

    [Fact]
    public void AHiddenSurfaceIsNeverLost()
    {
        Assert.False(HudHeal.Lost(cachedShown: false, engineShown: null));
        Assert.False(HudHeal.Lost(cachedShown: false, engineShown: false));
        Assert.False(HudHeal.Lost(cachedShown: true, engineShown: true));
        Assert.True(HudHeal.Lost(cachedShown: true, engineShown: null));
        Assert.True(HudHeal.Lost(cachedShown: true, engineShown: false));
    }

    [Fact]
    public void AHealThatKeepsBeingNeededIsSpacedOutAndThenGivenUp()
    {
        var heal = new HudHeal();
        int now = 1000;

        Assert.True(heal.ShouldResend(4, Layout, lost: true, now));
        Assert.False(heal.ShouldResend(4, Layout, lost: true, now + 1));

        int resends = 1;

        for (int step = 1; step <= 20; step++)
        {
            if (heal.ShouldResend(4, Layout, lost: true, now + step * HudHeal.MinSecondsBetween))
            {
                resends++;
            }
        }

        Assert.Equal(HudHeal.MaxInARow, resends);
        Assert.True(heal.GaveUp(4, Layout));

        // Somebody new in the slot is a fresh start.
        heal.Forget(4);
        Assert.True(heal.ShouldResend(4, Layout, lost: true, now + 500));
    }

    [Fact]
    public void AHealthyCheckClearsTheCount()
    {
        var heal = new HudHeal();

        Assert.True(heal.ShouldResend(3, Layout, lost: true, 10));
        Assert.False(heal.ShouldResend(3, Layout, lost: false, 11));
        Assert.True(heal.ShouldResend(3, Layout, lost: true, 12));
    }

    [Fact]
    public void ForgettingOneSlotKeepsTheOther()
    {
        var heal = new HudHeal();

        for (int i = 0; i < HudHeal.MaxInARow; i++)
        {
            heal.ShouldResend(3, Layout, lost: true, i * HudHeal.MinSecondsBetween);
            heal.ShouldResend(4, Layout, lost: true, i * HudHeal.MinSecondsBetween);
        }

        heal.Forget(4);

        Assert.True(heal.GaveUp(3, Layout));
        Assert.False(heal.GaveUp(4, Layout));
    }

    [Fact]
    public void TheCacheSuppressesOnlyWhatIsUnchanged()
    {
        var cache = new HudDiffCache();

        Assert.True(cache.Variable("title", "Mirage"));
        Assert.False(cache.Variable("title", "Mirage"));
        Assert.True(cache.Variable("title", "Inferno"));

        Assert.True(cache.Class(Root, Shown, true));
        Assert.False(cache.Class(Root, Shown, true));
        Assert.True(cache.Holds(Root, Shown));
        Assert.True(cache.Class(Root, Shown, false));
        Assert.False(cache.Holds(Root, Shown));

        cache.Invalidate();
        Assert.True(cache.IsEmpty);
        Assert.True(cache.Variable("title", "Inferno"));
    }

    [Fact]
    public void AMoveReportsTheCellItLeft()
    {
        var cache = new HudDiffCache();

        Assert.True(cache.Move("dot", "x", 5, out int? left));
        Assert.Null(left);
        Assert.False(cache.Move("dot", "x", 5, out left));
        Assert.True(cache.Move("dot", "x", 9, out left));
        Assert.Equal(5, left);
    }

    [Fact]
    public void OnePressThatBroughtThreeBotsKeepsOne()
    {
        // The incident: TricoN slot 3, Theft slot 4, bots 5..7 for one press.
        Assert.Equal(new[] { 7, 6 }, PracticeBotRoster.Surplus(new[] { 5, 6, 7 }, placed: 1));
    }

    [Fact]
    public void TheSecondPressKeepsTwoOfSix()
    {
        Assert.Equal(new[] { 10, 9, 8, 7 }, PracticeBotRoster.Surplus(new[] { 5, 6, 7, 8, 9, 10 }, placed: 2));
    }

    [Fact]
    public void AsManyBotsAsPlacedKicksNobody()
    {
        Assert.Empty(PracticeBotRoster.Surplus(new[] { 5, 6 }, placed: 2));
        Assert.Empty(PracticeBotRoster.Surplus(new[] { 5 }, placed: 3));
        Assert.Empty(PracticeBotRoster.Surplus(Array.Empty<int>(), placed: 0));
    }

    [Fact]
    public void NonePlacedKicksEveryBotOnATeam()
    {
        Assert.Equal(new[] { 9, 6 }, PracticeBotRoster.Surplus(new[] { 6, 9, 6 }, placed: 0));
        Assert.Equal(new[] { 6 }, PracticeBotRoster.Surplus(new[] { 6 }, placed: -1));
    }
}
