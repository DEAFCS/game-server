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
// live-ticking alert countdown ("WARMUP" + mm:ss), refreshed every second so
// it never disappears between milestones like a one-shot alert would. Uses
// MessageType.Alert rather than Center — ReadySystem's own repeating ".r to
// ready up" reminder already occupies the center-text slot, and the two would
// otherwise fight over it and flicker between messages. The actual
// cancellation always happens server-side (CancelExpiredMatches); this only
// informs players it's coming, and once it hits zero keeps repeating the
// canceled message until the match is torn down (Reset()).
public class AutoCancelCountdownSystem
{
    private readonly GameServer _gameServer;
    private readonly MatchService _matchService;
    private readonly ILogger<AutoCancelCountdownSystem> _logger;
    private readonly ILocalizer _localizer;

    private CancellationTokenSource? _timer;
    private DateTime? _cancelsAt;

    public AutoCancelCountdownSystem(
        ILogger<AutoCancelCountdownSystem> logger,
        GameServer gameServer,
        MatchService matchService,
        ILocalizer localizer
    )
    {
        _logger = logger;
        _gameServer = gameServer;
        _matchService = matchService;
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

        // cancels_at also carries the (much longer) "hung live match" safety
        // timeout once the knife round starts (Knife/Live/Overtime — "LIVE"
        // is considered to begin at the knife round) — only the pre-game
        // warmup deadline should show as a countdown to players.
        //
        // Deliberately checking GetCurrentMapStatus() directly here instead
        // of match.IsWarmup(): that helper also returns true whenever CS2's
        // own engine WarmupPeriod flag is set, which KnifeSystem turns back
        // on (mp_warmup_start) during the post-knife stay/switch decision
        // window to avoid freezing the game. That made this countdown
        // wrongly reappear there too, showing the unrelated (much larger)
        // live-match-timeout value under the "WARMUP" label.
        eMapStatus? currentStatus = _matchService.GetCurrentMatch()?.GetCurrentMapStatus();
        if (currentStatus != eMapStatus.Warmup && currentStatus != eMapStatus.Scheduled)
        {
            return;
        }

        int remainingSeconds = (int)
            Math.Floor((_cancelsAt.Value - DateTime.UtcNow).TotalSeconds);

        if (remainingSeconds <= 0)
        {
            _gameServer.Message(MessageType.Alert, _localizer["auto_cancel.canceled"]);
            return;
        }

        _gameServer.Message(
            MessageType.Alert,
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
