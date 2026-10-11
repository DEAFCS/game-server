using System.Reflection;
using FiveStack.Utilities;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using Xunit;

// HibernationUtility and TimerUtility keep static state, so these must not run in parallel.
[Collection("SwiftlyStatics")]
public class HibernationTests
{
    // Minimal stand-in for ISwiftlyCore: interface-typed members return child proxies,
    // delegates added with add_* are recorded, and Scheduler calls are logged by name.
    public class FakeCore : DispatchProxy
    {
        public Dictionary<string, Delegate?> Handlers = new();
        public List<string> Calls = new();
        public Dictionary<string, object?> Returns = new();
        public List<Action> Repeating = new();
        private readonly Dictionary<string, object> _children = new();

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            string name = m!.Name;

            if (name.StartsWith("add_"))
            {
                string key = name[4..];
                Handlers[key] = Delegate.Combine(Handlers.GetValueOrDefault(key), (Delegate)args![0]!);
                return null;
            }

            if (name.StartsWith("remove_"))
            {
                string key = name[7..];
                Handlers[key] = Delegate.Remove(Handlers.GetValueOrDefault(key), (Delegate)args![0]!);
                return null;
            }

            Calls.Add(name);

            if (name == "SetInternal")
            {
                Returns["get_Value"] = args![0];
            }

            if (Returns.TryGetValue(name, out object? fixedValue))
            {
                return fixedValue;
            }

            if (name == "RepeatBySeconds")
            {
                Repeating.Add((Action)args![1]!);
                return new CancellationTokenSource();
            }

            if (name == "NextTick" || name == "NextWorldUpdate")
            {
                return null;
            }

            Type rt = m.ReturnType;
            if (rt == typeof(CancellationTokenSource))
            {
                return new CancellationTokenSource();
            }
            if (rt.IsInterface && rt != typeof(void))
            {
                if (!_children.TryGetValue(name, out object? child))
                {
                    child = typeof(DispatchProxy)
                        .GetMethods().First(x => x.Name == nameof(DispatchProxy.Create) && x.IsGenericMethodDefinition && x.GetGenericArguments().Length == 2)
                        .MakeGenericMethod(rt, typeof(FakeCore))
                        .Invoke(null, null)!;
                    ((FakeCore)child).Parent = this;
                    _children[name] = child;
                }
                return child;
            }
            return rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
        }

        public FakeCore? Parent;
        public FakeCore Root => Parent?.Root ?? this;
        public FakeCore Child(string member) => (FakeCore)_children[member];
    }

    private static (ISwiftlyCore core, FakeCore scheduler, FakeCore events) Make()
    {
        var root = DispatchProxy.Create<ISwiftlyCore, FakeCore>();
        var fake = (FakeCore)(object)root;
        // Touch the members so child proxies exist.
        _ = root.Scheduler;
        _ = root.Event;
        _ = root.Logger;
        return (root, fake.Child("get_Scheduler"), fake.Child("get_Event"));
    }

    [Fact]
    public void NextTick_UsesTickWhenServerIsTicking()
    {
        var (core, scheduler, events) = Make();
        HibernationUtility.Initialize(core);
        try
        {
            ((EventDelegates.OnTick)events.Handlers["OnTick"]!)();
            HibernationUtility.NextTick(() => { });

            Assert.Contains("NextTick", scheduler.Calls);
            Assert.DoesNotContain("NextWorldUpdate", scheduler.Calls);
        }
        finally
        {
            HibernationUtility.Shutdown();
        }
    }

    [Fact]
    public async Task NextTick_FallsBackToWorldUpdateWhenTicksStop()
    {
        var (core, scheduler, events) = Make();
        HibernationUtility.Initialize(core);
        try
        {
            ((EventDelegates.OnTick)events.Handlers["OnTick"]!)();
            await Task.Delay(400); // past the 250 ms ticking window: hibernating, no ticks

            HibernationUtility.NextTick(() => { });

            Assert.Contains("NextWorldUpdate", scheduler.Calls);
            Assert.DoesNotContain("NextTick", scheduler.Calls);
        }
        finally
        {
            HibernationUtility.Shutdown();
        }
    }

    [Fact]
    public async Task NextTick_BeforeAnyTickUsesWorldUpdate()
    {
        // Plugin loaded into a server that is already hibernating: nothing has ticked yet.
        await Task.Delay(300); // let the static tick stamp from earlier tests age out
        var (core, scheduler, _) = Make();
        HibernationUtility.Initialize(core);
        try
        {
            HibernationUtility.NextTick(() => { });

            Assert.Contains("NextWorldUpdate", scheduler.Calls);
        }
        finally
        {
            HibernationUtility.Shutdown();
        }
    }

    [Fact]
    public void Shutdown_UnsubscribesHandlers()
    {
        var (core, _, events) = Make();
        HibernationUtility.Initialize(core);
        HibernationUtility.Shutdown();

        Assert.Null(events.Handlers["OnTick"]);
        Assert.Null(events.Handlers["OnWorldUpdate"]);
    }

    [Fact]
    public void IsHibernating_FalseWhenTicking()
    {
        var (core, _, events) = Make();
        HibernationUtility.Initialize(core);
        try
        {
            ((EventDelegates.OnTick)events.Handlers["OnTick"]!)();
            ((EventDelegates.OnWorldUpdate)events.Handlers["OnWorldUpdate"]!)();

            Assert.False(HibernationUtility.IsHibernating);
        }
        finally
        {
            HibernationUtility.Shutdown();
        }
    }

    [Fact]
    public void Repeat_KeepsRunningAfterCallbackThrows()
    {
        var (core, scheduler, _) = Make();
        TimerUtility.Initialize(core);
        int runs = 0;

        TimerUtility.Repeat(15, () =>
        {
            runs++;
            throw new InvalidOperationException("boom");
        });

        Action wrapped = scheduler.Repeating[0];
        wrapped(); // must not propagate, otherwise SwiftlyS2 stops rescheduling
        wrapped();

        Assert.Equal(2, runs);
        TimerUtility.ClearAll();
    }

    [Fact]
    public void Repeat_SkipsCallbackOnceCancelled()
    {
        var (core, scheduler, _) = Make();
        TimerUtility.Initialize(core);
        int runs = 0;

        var timer = TimerUtility.Repeat(15, () => runs++);
        TimerUtility.Kill(timer);
        scheduler.Repeating[0]();

        Assert.Equal(0, runs);
    }
}
