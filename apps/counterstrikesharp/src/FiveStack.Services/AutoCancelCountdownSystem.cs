using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Enums;
using FiveStack.Utilities;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FiveStack;

// Mirrors the auto-cancel deadline (matches.cancels_at) into a persistent,
// live-ticking alert countdown ("WARMUP" + mm:ss), refreshed every second so
// it never disappears between milestones like a one-shot alert would. Uses
// HudDestination.Alert rather than Center — ReadySystem's own repeating ".r
// to ready up" reminder already occupies the center-text slot, and the two
// would otherwise fight over it and flicker between messages. The actual
// cancel-or-force-start decision always happens server-side
// (CancelExpiredMatches); this only informs players the deadline is coming,
// and goes quiet once it passes rather than guessing which way it went.
public class AutoCancelCountdownSystem
{
    // Matches CancelExpiredMatches' polling interval on the API -- the worst
    // case wait between cancels_at expiring and the API actually resolving
    // it (cancel or force-start). Shown as a "resolving in..." countdown so
    // the wait doesn't look like nothing is happening.
    private const int ResolveWorstCaseSeconds = 15;

    private readonly GameServer _gameServer;
    private readonly MatchService _matchService;
    private readonly ILogger<AutoCancelCountdownSystem> _logger;
    private readonly IStringLocalizer _localizer;

    private Timer? _timer;
    private DateTime? _cancelsAt;

    public AutoCancelCountdownSystem(
        ILogger<AutoCancelCountdownSystem> logger,
        GameServer gameServer,
        MatchService matchService,
        IStringLocalizer localizer
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

        _timer?.Kill();
        _timer = null;

        if (_cancelsAt == null)
        {
            return;
        }

        _timer = TimerUtility.AddTimer(1, Check, TimerFlags.REPEAT);
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

        // Past the deadline, the API decides whether to actually cancel the
        // match or force it into the knife round instead (if someone already
        // connected) -- this client-side countdown can't know which, so it
        // shows a generic "resolving" countdown for the worst-case wait
        // instead of guessing "canceled" or "starting" and being wrong.
        if (remainingSeconds <= 0)
        {
            int secondsSinceDeadline = -remainingSeconds;
            int resolvingIn = ResolveWorstCaseSeconds - secondsSinceDeadline;

            if (resolvingIn > 0)
            {
                _gameServer.Message(
                    HudDestination.Alert,
                    _localizer["auto_cancel.resolving", resolvingIn]
                );
            }

            return;
        }

        _gameServer.Message(
            HudDestination.Alert,
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
        _timer?.Kill();
        _timer = null;
        _cancelsAt = null;
    }
}
