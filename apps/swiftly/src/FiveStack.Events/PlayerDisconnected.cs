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

        // Do NOT pause immediately here -- that used to race the timed,
        // one-per-team automatic technical pause requested below (queued to
        // fire at the next round start via RequestAutoPauseAtNextRound), and
        // an immediate PauseMatch() has no resume timer of its own, so the
        // match could sit paused indefinitely instead of the intended 2 min.

        // Budget enforcement starts at the knife round, not just after it --
        // OnPlayerDisconnected itself is a no-op during Warmup.
        match.disconnectBudgetSystem.OnPlayerDisconnected(
            @event.UserIdPlayer.SteamID,
            player.Name
        );
        match.teamEmptyForfeitSystem.Check();

        // One automatic 2-min technical pause per lineup per match, applied
        // at the next round start rather than immediately. Keyed by
        // lineup_id (member.match_lineup_id), not player.Controller.Team --
        // a lineup's native CT/T side can change over the match (side
        // swaps, knife round stay/switch), and player.Controller.Team is
        // only a snapshot of the side at this exact moment.
        if (match.IsInPlayOrKnife())
        {
            match.timeoutSystem.RequestAutoPauseAtNextRound(member.match_lineup_id);
        }

        return HookResult.Continue;
    }
}
