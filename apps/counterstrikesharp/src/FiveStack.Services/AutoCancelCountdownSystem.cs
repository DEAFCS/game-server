using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Utilities;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FiveStack;

// Mirrors the auto-cancel deadline (matches.cancels_at) into the in-game
// chat as coarse milestone announcements — not a live per-second countdown,
// same as the website's warning. The actual cancellation always happens
// server-side (CancelExpiredMatches); this only informs players it's coming.
public class AutoCancelCountdownSystem
{
    // Largest first — Check() announces the first (largest) one the
    // remaining time has dropped to or below, so starting mid-countdown
    // (e.g. after a reconnect) never announces a milestone already passed.
    private static readonly int[] Milestones = { 300, 240, 180, 120, 60, 30, 15 };

    private readonly GameServer _gameServer;
    private readonly ILogger<AutoCancelCountdownSystem> _logger;
    private readonly IStringLocalizer _localizer;

    private Timer? _timer;
    private DateTime? _cancelsAt;
    private int? _lastAnnouncedMilestone;

    public AutoCancelCountdownSystem(
        ILogger<AutoCancelCountdownSystem> logger,
        GameServer gameServer,
        IStringLocalizer localizer
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
        _lastAnnouncedMilestone = null;

        _timer?.Kill();
        _timer = null;

        if (_cancelsAt == null)
        {
            return;
        }

        _timer = TimerUtility.AddTimer(5, Check, TimerFlags.REPEAT);
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
            if (_lastAnnouncedMilestone != 0)
            {
                _lastAnnouncedMilestone = 0;
                _gameServer.Message(
                    HudDestination.Alert,
                    _localizer["auto_cancel.canceled", ChatColors.Red]
                );
            }

            _timer?.Kill();
            _timer = null;
            return;
        }

        foreach (int milestone in Milestones)
        {
            if (
                remainingSeconds <= milestone
                && (_lastAnnouncedMilestone == null || milestone < _lastAnnouncedMilestone)
            )
            {
                _lastAnnouncedMilestone = milestone;
                _gameServer.Message(
                    HudDestination.Alert,
                    _localizer["auto_cancel.time_left", ChatColors.Red, FormatMilestone(milestone)]
                );
                break;
            }
        }
    }

    private static string FormatMilestone(int seconds)
    {
        return seconds >= 60 ? $"{seconds / 60} min" : $"{seconds} sec";
    }

    public void Reset()
    {
        _timer?.Kill();
        _timer = null;
        _cancelsAt = null;
        _lastAnnouncedMilestone = null;
    }
}
