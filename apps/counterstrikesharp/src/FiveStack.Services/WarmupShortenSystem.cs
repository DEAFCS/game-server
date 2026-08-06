using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Entities;
using FiveStack.Enums;
using FiveStack.Utilities;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FiveStack;

// Matchmaking-only: .ready/.unready are disabled there (tournament-only, see
// Ready.cs), so there's no manual "everyone confirmed" step anymore. Instead,
// once every expected roster player has connected during warmup, wait a
// short grace window (not CS2's native mp_warmuptime_all_players_connected --
// that counts every connected server slot, including the GOTV/coach buffer
// added on top of the roster in EXTRA_GAME_PARAMS, so it would never fire at
// the right time) and then auto-advance to the knife round. Tournament
// matches are untouched -- ReadySystem still governs those.
public class WarmupShortenSystem
{
    private const int ShortenedWarmupSeconds = 60;

    private readonly GameServer _gameServer;
    private readonly MatchService _matchService;
    private readonly IStringLocalizer _localizer;
    private readonly ILogger<WarmupShortenSystem> _logger;

    private Timer? _timer;

    public WarmupShortenSystem(
        ILogger<WarmupShortenSystem> logger,
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

    // Call after any connect/disconnect while the match may be in warmup.
    public void Check()
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        MatchData? matchData = match?.GetMatchData();

        if (
            match == null
            || matchData == null
            || !match.IsWarmup()
            || matchData.is_tournament_match
        )
        {
            CancelTracking();
            return;
        }

        int currentPlayers = MatchUtility.Players().Count;
        int expectedPlayers = match.GetExpectedPlayerCount();

        if (currentPlayers < expectedPlayers)
        {
            CancelTracking();
            return;
        }

        if (_timer != null)
        {
            return;
        }

        _logger.LogInformation(
            $"All {expectedPlayers} players connected during warmup -- starting {ShortenedWarmupSeconds}s countdown to knife round"
        );

        _gameServer.Message(
            HudDestination.Chat,
            $"{ChatColors.Orange}[DEAFCS] {ChatColors.Default}"
                + _localizer["warmup.all_connected", ShortenedWarmupSeconds]
        );

        _timer = TimerUtility.AddTimer(
            ShortenedWarmupSeconds,
            () =>
            {
                _timer = null;

                MatchManager? currentMatch = _matchService.GetCurrentMatch();
                if (currentMatch == null || !currentMatch.IsWarmup())
                {
                    return;
                }

                currentMatch.UpdateMapStatus(eMapStatus.Knife);
            }
        );
    }

    private void CancelTracking()
    {
        _timer?.Kill();
        _timer = null;
    }

    public void Reset()
    {
        CancelTracking();
    }
}
