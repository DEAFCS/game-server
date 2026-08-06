using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using FiveStack.Entities;
using FiveStack.Utilities;

namespace FiveStack;

public partial class FiveStackPlugin
{
    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        MatchMap? currentMap = match?.GetCurrentMap();
        MatchData? matchData = match?.GetMatchData();
        if (
            @event.Userid == null
            || !@event.Userid.IsValid
            || @event.Userid.IsBot
            || match == null
            || currentMap == null
            || matchData == null
        )
        {
            return HookResult.Continue;
        }

        CCSPlayerController player = @event.Userid;

        MatchMember? member = MatchUtility.GetMemberFromLineup(
            matchData,
            player.SteamID.ToString(),
            player.PlayerName
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
            match.captainSystem.RemoveCaptain(@event.Userid);
            match.warmupShortenSystem.Check();
        }

        _surrenderSystem.RemovePlayerVoteOnDisconnect(player.SteamID);
        _timeoutSystem.RemovePlayerVoteOnDisconnect(player.SteamID);
        _gameBackupRounds.RemovePlayerVoteOnDisconnect(player.SteamID);

        // Deliberately not pausing immediately here -- the match keeps
        // playing shorthanded from the moment someone disconnects.
        // RoundStart's "waiting for players to reconnect" pause (if the
        // team isn't fully empty) is the only automatic pause left; anyone
        // who wants a pause right now has to call .timeout/.tac themselves.

        // Budget enforcement starts at the knife round, not just after it --
        // OnPlayerDisconnected itself is a no-op during Warmup.
        match.disconnectBudgetSystem.OnPlayerDisconnected(
            @event.Userid.SteamID,
            player.PlayerName
        );
        match.teamEmptyForfeitSystem.Check();

        return HookResult.Continue;
    }
}
