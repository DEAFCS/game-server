namespace FiveStack.Utilities;

// AFK protection for practice servers (DEAFCS).
//
// The reaper only ends a session once nobody is connected, so a player who
// stays connected and walks away keeps a server -- one of the few the reserve
// leaves for practice -- for as long as they like. Each player gets their own
// idle clock, reset by anything they actually do: a warning at 25 minutes, a
// last one at 29, and a kick at 30, after which the empty-server reaper ends
// the session as before. Upstream 5Stack has no AFK handling.
public sealed class PracticeAfk
{
    public const int WarnSeconds = 25 * 60;
    public const int FinalWarnSeconds = 29 * 60;
    public const int KickSeconds = 30 * 60;

    // A look this small between two one-second samples is aim jitter, not a
    // player: below it the view does not count as moving.
    public const float LookDegrees = 1f;

    // Likewise for standing: a pawn settling on a slope drifts this much.
    public const float MoveUnits = 4f;

    public enum Step
    {
        None,
        Warn,
        FinalWarn,
        Kick,
    }

    private sealed class Clock
    {
        public int LastActive;
        public Step Shown;
    }

    private readonly Dictionary<ulong, Clock> _clocks = new();

    /// <summary>The player did something: their clock starts over.</summary>
    public void Active(ulong steamId, int nowSeconds)
    {
        if (_clocks.TryGetValue(steamId, out Clock? clock))
        {
            clock.LastActive = nowSeconds;
            clock.Shown = Step.None;

            return;
        }

        _clocks[steamId] = new Clock { LastActive = nowSeconds, Shown = Step.None };
    }

    /// <summary>
    /// What is due for this player now, each step answered once: Warn, then
    /// FinalWarn, then Kick. A player seen for the first time starts a clock
    /// rather than being judged on time they were never watched for.
    /// </summary>
    public Step Check(ulong steamId, int nowSeconds)
    {
        if (!_clocks.TryGetValue(steamId, out Clock? clock))
        {
            Active(steamId, nowSeconds);

            return Step.None;
        }

        int idle = nowSeconds - clock.LastActive;

        Step due =
            idle >= KickSeconds ? Step.Kick
            : idle >= FinalWarnSeconds ? Step.FinalWarn
            : idle >= WarnSeconds ? Step.Warn
            : Step.None;

        if (due <= clock.Shown)
        {
            return Step.None;
        }

        clock.Shown = due;

        return due;
    }

    public int IdleSeconds(ulong steamId, int nowSeconds)
    {
        return _clocks.TryGetValue(steamId, out Clock? clock) ? nowSeconds - clock.LastActive : 0;
    }

    public void Forget(ulong steamId)
    {
        _clocks.Remove(steamId);
    }

    public void Reset()
    {
        _clocks.Clear();
    }

    /// <summary>Whether two one-second view samples differ enough to be a person looking around.</summary>
    public static bool Looked(float yawBefore, float pitchBefore, float yawNow, float pitchNow)
    {
        float yaw = MathF.Abs(((yawNow - yawBefore) % 360f + 540f) % 360f - 180f);
        float pitch = MathF.Abs(pitchNow - pitchBefore);

        return yaw >= LookDegrees || pitch >= LookDegrees;
    }

    /// <summary>Whether two one-second position samples differ enough to be a person moving.</summary>
    public static bool Moved(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        float dx = x1 - x0;
        float dy = y1 - y0;
        float dz = z1 - z0;

        return dx * dx + dy * dy + dz * dz >= MoveUnits * MoveUnits;
    }
}
