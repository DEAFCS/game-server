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
// once every expected roster player has connected during warmup, retarget
// the existing live "WARMUP mm:ss" clock (AutoCancelCountdownSystem) down to
// a short window and auto-advance to the knife round when it elapses -- no
// manual ready-up needed, and players see one consistent, live-ticking
// countdown instead of a separate silent timer with just a one-off chat
// ping (which is all this used to do, and looked broken since the visible
// clock kept showing the old, longer no-show deadline).
//
// Deliberately NOT built on CS2's native mp_warmuptime_all_players_connected
// -- that cvar counts every connected server slot, including the GOTV/coach
// buffer (EXTRA_GAME_PARAMS pads maxplayers with +3 beyond the roster), so
// it would never actually reach "all connected" at the right threshold.
//
// Note: this system only ever decides *when* warmup ends (its own timer is
// the authority for that, same as ReadySystem.Skip() driving it directly
// client-side) -- retargeting AutoCancelCountdownSystem's displayed
// countdown is purely visual, mirroring whatever the actual deadline is at
// any given moment. It never touches the DB-authoritative cancels_at that
// CancelExpiredMatches uses for the no-show cancel-or-force-start decision.
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
            CancelTracking(match, matchData);
            return;
        }

        int currentPlayers = MatchUtility.PlayerCount();
        int expectedPlayers = match.GetExpectedPlayerCount();

        if (currentPlayers < expectedPlayers)
        {
            CancelTracking(match, matchData);
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

        // Retarget the live "WARMUP mm:ss" clock down to the shortened
        // window so what players see actually matches when the match is
        // about to start, instead of continuing to count down the old
        // (longer) no-show deadline.
        match.autoCancelCountdownSystem.SetCancelsAt(
            DateTime.UtcNow.AddSeconds(ShortenedWarmupSeconds)
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

    private void CancelTracking(MatchManager? match, MatchData? matchData)
    {
        bool wasTracking = _timer != null;

        TimerUtility.Kill(_timer);
        _timer = null;

        if (wasTracking)
        {
            // The roster's no longer fully connected (or the match left
            // warmup) -- the shortened countdown no longer applies, restore
            // the real no-show deadline on the live clock.
            match?.autoCancelCountdownSystem.SetCancelsAt(matchData?.cancels_at);
        }
    }

    public void Reset()
    {
        CancelTracking(null, null);
    }
}
