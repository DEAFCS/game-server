using FiveStack.Entities;
using FiveStack.Enums;
using FiveStack.Utilities;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Translation;
using static SwiftlyS2.Shared.Helper;

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
    private readonly ILocalizer _localizer;
    private readonly ILogger<WarmupShortenSystem> _logger;

    private CancellationTokenSource? _timer;

    public WarmupShortenSystem(
        ILogger<WarmupShortenSystem> logger,
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

        int currentPlayers = MatchUtility.PlayerCount();
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
            MessageType.Chat,
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
        TimerUtility.Kill(_timer);
        _timer = null;
    }

    public void Reset()
    {
        CancelTracking();
    }
}
