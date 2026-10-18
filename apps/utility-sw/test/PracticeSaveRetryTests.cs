using FiveStack.Utilities;
using Xunit;

namespace FiveStack.Tests;

// 2026-10-18: a save hit one "Connection reset by peer", was queued with "it
// will retry", and was never sent again -- nothing drained the queue until the
// next successful save or a map change.
public class PracticeSaveRetryTests
{
    [Fact]
    public void NothingQueuedIsNeverDrained()
    {
        Assert.False(PracticeSaveRetry.ShouldDrain(queued: 0, secondsSinceLastAttempt: 3600));
    }

    [Fact]
    public void AQueuedSaveIsTriedAgainWithinTheInterval()
    {
        Assert.False(PracticeSaveRetry.ShouldDrain(1, PracticeSaveRetry.EverySeconds - 1));
        Assert.True(PracticeSaveRetry.ShouldDrain(1, PracticeSaveRetry.EverySeconds));
        Assert.True(PracticeSaveRetry.ShouldDrain(64, PracticeSaveRetry.EverySeconds + 5));
    }

    [Fact]
    public void TheIntervalIsShortEnoughToReachTheLibraryBeforeTheNextThrow()
    {
        Assert.InRange(PracticeSaveRetry.EverySeconds, 5, 15);
    }
}
