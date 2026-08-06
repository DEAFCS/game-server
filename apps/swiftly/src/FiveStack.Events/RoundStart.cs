using FiveStack.Utilities;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;

namespace FiveStack;

public partial class FiveStackPlugin
{
    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart @event)
    {
        _victimHealth.Clear();

        MatchManager? matchManager = _matchService.GetCurrentMatch();
        if (matchManager == null)
        {
            _logger.LogInformation("OnRoundStart: no current match - skipping");
            return HookResult.Continue;
        }

        _rankSystem.Refresh();

        int totalRoundsPlayed = _gameServer.GetTotalRoundsPlayed();
        bool isInPlay = matchManager.IsInPlay();
        bool isKnife = matchManager.IsKnife();
        bool isWarmup = matchManager.IsWarmup();

        _logger.LogInformation(
            $"OnRoundStart totalRoundsPlayed={totalRoundsPlayed} isInPlay={isInPlay} isWarmup={isWarmup} isKnife={isKnife}"
        );

        if (!isInPlay)
        {
            return HookResult.Continue;
        }

        if (_gameBackupRounds.IsResettingRound())
        {
            _logger.LogInformation("OnRoundStart skipping publish: restoring round");
            return HookResult.Continue;
        }

        // Safety net: re-assert the match-type cfg's custom cvars
        // (mp_team_timeout_time/_max etc.) once the first live round has
        // actually started -- see ReapplyMatchTypeCfg for why this is
        // needed on top of the exec already done at Live start.
        if (totalRoundsPlayed == 0)
        {
            matchManager.ReapplyMatchTypeCfg();
        }

        PublishPendingRound(SendBackupRound: true);

        // A .gg vote only stays valid for the round it was started in.
        _surrenderSystem.CancelPendingForfeitVote();

        // Freeze period just started -- send any "player must reconnect"
        // warning that tried to fire mid-round and got queued instead of
        // dropped.
        matchManager.disconnectBudgetSystem.FlushPendingAnnouncements();

        int currentPlayers = MatchUtility.PlayerCount();

        int expectedPlayers = _matchService.GetCurrentMatch()?.GetExpectedPlayerCount() ?? 10;

        // Matchmaking never pauses for a missing player unless their whole
        // team is empty -- that's TeamEmptyForfeitSystem's own territory,
        // untouched here. Otherwise the match just keeps playing
        // shorthanded (e.g. 1v2) from the moment someone leaves; no
        // automatic pause of any kind in MM. Tournament matches keep the
        // "wait every round" pause, since an admin/organizer is expected to
        // actually manage a no-show there.
        bool isTournamentMatch = matchManager.GetMatchData()?.is_tournament_match == true;

        if (
            isTournamentMatch
            && currentPlayers < expectedPlayers
            && !matchManager.AllMissingPlayersAreBanned()
        )
        {
            matchManager.PauseMatch("Waiting for players to reconnect");
        }

        return HookResult.Continue;
    }
}
