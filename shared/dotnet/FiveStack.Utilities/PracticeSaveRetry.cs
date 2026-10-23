namespace FiveStack.Utilities;

// When a practice server tries its held-back saves again.
//
// A save that could not reach the panel is queued and the player is told "it
// will retry". The queue used to be drained only by the next save that got
// through, a map load or a hot reload -- so one dropped connection (seen live
// on 2026-10-18: "Connection reset by peer" on POST /utility/ingest) left the
// save sitting there for the rest of the session, the lineup never reached the
// library, and E had nothing to snap to.
public static class PracticeSaveRetry
{
    public const int EverySeconds = 10;

    public static bool ShouldDrain(int queued, int secondsSinceLastAttempt)
    {
        return queued > 0 && secondsSinceLastAttempt >= EverySeconds;
    }
}
