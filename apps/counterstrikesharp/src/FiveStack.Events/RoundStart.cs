using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using FiveStack.Utilities;
using Microsoft.Extensions.Logging;

namespace FiveStack;

public partial class FiveStackPlugin
{
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
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

        int currentPlayers = MatchUtility.Players().Count;

        int expectedPlayers = _matchService.GetCurrentMatch()?.GetExpectedPlayerCount() ?? 10;

        // Don't keep re-pausing every round for someone who's already
        // permanently banned -- they can never come back, so the match
        // should just keep playing shorthanded (e.g. 1v2) instead.
        if (
            currentPlayers < expectedPlayers
            && !matchManager.AllMissingPlayersAreBanned()
        )
        {
            matchManager.PauseMatch("Waiting for players to reconnect");
        }

        return HookResult.Continue;
    }
}
