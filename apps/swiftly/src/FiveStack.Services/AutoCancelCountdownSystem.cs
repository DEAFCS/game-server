using FiveStack.Enums;
using FiveStack.Utilities;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Translation;
using static SwiftlyS2.Shared.Helper;

namespace FiveStack;

// Mirrors the auto-cancel deadline (matches.cancels_at) into a persistent,
// live-ticking center-text countdown ("WARMUP" + mm:ss), refreshed every
// second so it never disappears between milestones like a one-shot alert
// would. The actual cancellation always happens server-side
// (CancelExpiredMatches); this only informs players it's coming, and once
// it hits zero keeps repeating the canceled message until the match is torn
// down (Reset()).
public class AutoCancelCountdownSystem
{
    private readonly GameServer _gameServer;
    private readonly ILogger<AutoCancelCountdownSystem> _logger;
    private readonly ILocalizer _localizer;

    private CancellationTokenSource? _timer;
    private DateTime? _cancelsAt;

    public AutoCancelCountdownSystem(
        ILogger<AutoCancelCountdownSystem> logger,
        GameServer gameServer,
        ILocalizer localizer
    )
    {
        _logger = logger;
        _gameServer = gameServer;
        _localizer = localizer;
    }

    public void SetCancelsAt(DateTime? cancelsAt)
    {
        if (cancelsAt == _cancelsAt)
        {
            return;
        }

        _cancelsAt = cancelsAt;

        TimerUtility.Kill(_timer);
        _timer = null;

        if (_cancelsAt == null)
        {
            return;
        }

        _timer = TimerUtility.Repeat(1, Check);
        Check();
    }

    private void Check()
    {
        if (_cancelsAt == null)
        {
            return;
        }

        int remainingSeconds = (int)
            Math.Floor((_cancelsAt.Value - DateTime.UtcNow).TotalSeconds);

        if (remainingSeconds <= 0)
        {
            _gameServer.Message(MessageType.Center, _localizer["auto_cancel.canceled"]);
            return;
        }

        _gameServer.Message(
            MessageType.Center,
            _localizer["auto_cancel.warmup_countdown", FormatTime(remainingSeconds)]
        );
    }

    private static string FormatTime(int totalSeconds)
    {
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:D2}";
    }

    public void Reset()
    {
        TimerUtility.Kill(_timer);
        _timer = null;
        _cancelsAt = null;
    }
}
