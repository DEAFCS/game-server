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

        // Do NOT pause immediately here -- that used to race the timed,
        // one-per-team automatic technical pause requested below (queued to
        // fire at the next round start via RequestAutoPauseAtNextRound), and
        // an immediate PauseMatch() has no resume timer of its own, so the
        // match could sit paused indefinitely instead of the intended 2 min.

        // Budget enforcement starts at the knife round, not just after it --
        // OnPlayerDisconnected itself is a no-op during Warmup.
        match.disconnectBudgetSystem.OnPlayerDisconnected(
            @event.Userid.SteamID,
            player.PlayerName
        );
        match.teamEmptyForfeitSystem.Check();

        // One automatic 2-min technical pause per lineup per match, applied
        // at the next round start rather than immediately. Keyed by
        // lineup_id (member.match_lineup_id), not player.Team -- a lineup's
        // native CT/T side can change over the match (side swaps, knife
        // round stay/switch), and player.Team is only a snapshot of the
        // side at this exact moment.
        if (match.IsInPlayOrKnife())
        {
            match.timeoutSystem.RequestAutoPauseAtNextRound(member.match_lineup_id);
        }

        return HookResult.Continue;
    }
}
