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

        PublishPendingRound(SendBackupRound: true);

        int currentPlayers = MatchUtility.PlayerCount();

        int expectedPlayers = _matchService.GetCurrentMatch()?.GetExpectedPlayerCount() ?? 10;

        // The one-per-team automatic technical pause takes priority over
        // the generic "waiting for players" pause below -- don't fire both
        // for the same round start.
        bool triggeredAutoPause = matchManager.timeoutSystem.TriggerPendingAutoPauseIfAny();

        // Don't keep re-pausing every round for someone who's already
        // permanently banned -- they can never come back, so the match
        // should just keep playing shorthanded (e.g. 1v2) instead.
        if (
            !triggeredAutoPause
            && currentPlayers < expectedPlayers
            && !matchManager.AllMissingPlayersAreBanned()
        )
        {
            matchManager.PauseMatch("Waiting for players to reconnect");
        }

        return HookResult.Continue;
    }
}
