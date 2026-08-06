using FiveStack.Entities;
using FiveStack.Utilities;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace FiveStack;

public partial class FiveStackPlugin
{
    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        MatchMap? currentMap = match?.GetCurrentMap();
        MatchData? matchData = match?.GetMatchData();
        if (
            @event.UserIdPlayer == null
            || !@event.UserIdPlayer.IsValid
            || @event.UserIdPlayer.IsFakeClient
            || match == null
            || currentMap == null
            || matchData == null
        )
        {
            return HookResult.Continue;
        }

        IPlayer player = @event.UserIdPlayer;

        MatchMember? member = MatchUtility.GetMemberFromLineup(
            matchData,
            player.SteamID.ToString(),
            player.Name
        );

        if (member == null)
        {
            return HookResult.Continue;
        }

        _matchEvents.PublishGameEvent(
            "player-disconnected",
            new Dictionary<string, object> { { "steam_id", player.SteamID.ToString() } }
        );

        if (match.IsWarmup() || match.IsKnife())
        {
            match.readySystem.UnreadyPlayer(player);
            match.captainSystem.RemoveCaptain(@event.UserIdPlayer);
            match.warmupShortenSystem.Check();
        }

        _surrenderSystem.RemovePlayerVoteOnDisconnect(player.SteamID);
        match.timeoutSystem.RemovePlayerVoteOnDisconnect(player.SteamID);
        _gameBackupRounds.RemovePlayerVoteOnDisconnect(player.SteamID);

        // Deliberately not pausing immediately here -- the match keeps
        // playing shorthanded from the moment someone disconnects.
        // RoundStart's "waiting for players to reconnect" pause (if the
        // team isn't fully empty) is the only automatic pause left; anyone
        // who wants a pause right now has to call .timeout/.tac themselves.

        // Budget enforcement starts at the knife round, not just after it --
        // OnPlayerDisconnected itself is a no-op during Warmup.
        match.disconnectBudgetSystem.OnPlayerDisconnected(
            @event.UserIdPlayer.SteamID,
            player.Name
        );

        // Deferred one tick: at the moment this event fires, the
        // disconnecting player is still present in MatchUtility.Players()
        // -- the engine hasn't finished removing them from its own player
        // list yet -- so a synchronous Check() here always undercounts by
        // one. Confirmed via live log: with two players on the same team
        // disconnecting a couple seconds apart, Check() logged the team
        // still at 1 (not 0) immediately after the *second* one's own
        // disconnect, still counting the very player whose disconnect
        // triggered the call -- the team count never actually reached 0,
        // so the empty-team pause/forfeit never fired. Wait a tick so the
        // player list has actually settled before counting.
        _core.Scheduler.NextTick(() => match.teamEmptyForfeitSystem.Check());

        return HookResult.Continue;
    }
}
