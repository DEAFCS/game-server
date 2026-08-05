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

    // Chat warnings (separate from the live tick above) naming whoever
    // hasn't joined yet, fired once each as the deadline crosses these
    // thresholds -- mirrors DisconnectBudgetSystem's milestone warnings.
    private static readonly int[] MilestoneSeconds = { 5 * 60, 3 * 60, 60 };

    private readonly GameServer _gameServer;
    private readonly MatchService _matchService;
    private readonly ILogger<AutoCancelCountdownSystem> _logger;
    private readonly ILocalizer _localizer;

    private CancellationTokenSource? _timer;
    private DateTime? _cancelsAt;
    private readonly HashSet<int> _announcedMilestones = new HashSet<int>();

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
        _announcedMilestones.Clear();

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
                    MessageType.Alert,
                    _localizer["auto_cancel.resolving", resolvingIn]
                );
            }

            return;
        }

        _gameServer.Message(
            MessageType.Alert,
            _localizer["auto_cancel.warmup_countdown", FormatTime(remainingSeconds)]
        );

        AnnounceMissingPlayersMilestone(remainingSeconds);
    }

    // Chat warning naming whoever hasn't joined yet, fired once as the
    // deadline crosses each 5/3/1-min threshold. Skipped once nobody's
    // actually missing anymore (everyone connected -> CancelExpiredMatches
    // will force-start instead of canceling, nothing to warn about).
    private void AnnounceMissingPlayersMilestone(int remainingSeconds)
    {
        foreach (int milestone in MilestoneSeconds)
        {
            if (remainingSeconds > milestone || _announcedMilestones.Contains(milestone))
            {
                continue;
            }

            _announcedMilestones.Add(milestone);

            List<string> missingPlayers =
                _matchService.GetCurrentMatch()?.GetMissingPlayerNames() ?? new List<string>();

            if (missingPlayers.Count == 0)
            {
                continue;
            }

            int minutesRemaining = milestone / 60;
            _gameServer.Message(
                MessageType.Chat,
                $"{ChatColors.Orange}[DEAFCS] {ChatColors.Default}"
                    + _localizer[
                        "auto_cancel.missing_players_warning",
                        minutesRemaining,
                        string.Join(", ", missingPlayers)
                    ]
            );
        }
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
        _announcedMilestones.Clear();
    }
}
