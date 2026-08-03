using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
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
// cancellation always happens server-side (CancelExpiredMatches); this only
// informs players it's coming, and once it hits zero keeps repeating the
// canceled message until the match is torn down (Reset()).
public class AutoCancelCountdownSystem
{
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
        if (_matchService.GetCurrentMatch()?.IsWarmup() != true)
        {
            return;
        }

        int remainingSeconds = (int)
            Math.Floor((_cancelsAt.Value - DateTime.UtcNow).TotalSeconds);

        if (remainingSeconds <= 0)
        {
            _gameServer.Message(HudDestination.Alert, _localizer["auto_cancel.canceled"]);
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
