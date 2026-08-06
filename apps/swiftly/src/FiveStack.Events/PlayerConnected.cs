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
    public HookResult OnPlayerConnect(EventPlayerConnectFull @event)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        MatchData? matchData = match?.GetMatchData();

        if (
            @event.UserIdPlayer == null
            || !@event.UserIdPlayer.IsValid
            || @event.UserIdPlayer.IsFakeClient
            || match == null
            || matchData?.current_match_map_id == null
        )
        {
            return HookResult.Continue;
        }

        match.disconnectBudgetSystem.OnPlayerReconnected(@event.UserIdPlayer.SteamID);

        IPlayer player = @event.UserIdPlayer;

        Guid? lineup_id = MatchUtility.GetPlayerLineup(matchData, player);
        List<MatchMember> players = matchData
            .lineup_1.lineup_players.Concat(matchData.lineup_2.lineup_players)
            .ToList();

        bool shouldKick = true;

        if (
            match.IsWarmup()
            && players.Any(player => !string.IsNullOrEmpty(player.placeholder_name))
        )
        {
            shouldKick = false;
        }

        if (players.Find(player => player.steam_id == null) != null)
        {
            shouldKick = false;
        }

        if (lineup_id == null)
        {
            string? role = null;
            if (PendingPlayers.ContainsKey(player.SteamID))
            {
                role = PendingPlayers[player.SteamID];
                player.Controller.Clan = $"[{role}]";
                player.Controller.ClanUpdated();
                PendingPlayers.Remove(player.SteamID);
            }

            if (shouldKick && role == null)
            {
                _core.Engine.ExecuteCommand($"kickid {player.UserID}");
                return HookResult.Continue;
            }
        }

        Team expectedTeam = match.GetExpectedTeam(player);
        int expectedTeamCount = match.GetExpectedPlayerCount() / 2;
        int teamCount = TeamUtility.GetTeamCount(expectedTeam);

        if (player.Controller.Team == expectedTeam)
        {
            teamCount--;
        }

        if (teamCount > expectedTeamCount)
        {
            _core.Engine.ExecuteCommand($"kickid {player.UserID}");
            return HookResult.Continue;
        }

        match.EnforceMemberTeam(player, Team.None);

        _matchEvents.PublishGameEvent(
            "player-connected",
            new Dictionary<string, object>
            {
                { "player_name", player.Name },
                { "steam_id", player.SteamID.ToString() },
            }
        );

        match.teamEmptyForfeitSystem.Check();
        match.warmupShortenSystem.Check();

        // Auto-resume the moment the full expected roster is back, instead
        // of requiring someone to type .resume manually. Safe to call
        // unconditionally -- ResumeMatch() is a no-op (beyond an idempotent
        // mp_unpause_match) when the match isn't actually paused.
        if (MatchUtility.PlayerCount() == match.GetExpectedPlayerCount())
        {
            // Also cancel any queued one-per-lineup auto-pause for this
            // player's roster -- otherwise it still fires at the next round
            // start even though the missing player already reconnected.
            if (lineup_id != null)
            {
                match.timeoutSystem.CancelPendingAutoPauseForLineup(lineup_id.Value);
            }
            match.ResumeMatch();
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerJoinTeam(EventPlayerTeam @event)
    {
        MatchManager? match = _matchService.GetCurrentMatch();

        if (
            @event.UserIdPlayer == null
            || !@event.UserIdPlayer.IsValid
            || @event.UserIdPlayer.IsFakeClient
            || match == null
        )
        {
            return HookResult.Continue;
        }

        if (MatchUtility.PlayerCount() == 1 && match.IsWarmup())
        {
            _gameServer.SendCommands(["mp_warmup_start"]);
        }

        IPlayer player = @event.UserIdPlayer;

        if (match.readySystem.IsWaitingForReady())
        {
            _gameServer.Message(
                MessageType.Chat,
                _localizer[
                    "player.join.ready_hint",
                    "[green]",
                    CommandUtility.PublicChatTrigger,
                    "[default]"
                ],
                player
            );
        }

        _gameServer.Message(
            MessageType.Chat,
            _localizer[
                "player.join.help_hint",
                "[green]",
                CommandUtility.SilentChatTrigger,
                "[default]"
            ],
            player
        );

        // Deliberately not calling teamEmptyForfeitSystem.Check() here: this
        // event also fires when the engine reassigns both players' sides
        // mid stay/switch decision, and CS2 can process that sequentially
        // (briefly zeroing one side before the other is set), which was
        // triggering a false "team is empty" pause during a normal side
        // swap. OnPlayerConnect already covers the real "someone
        // reconnected" case.

        return HookResult.Continue;
    }

    public HookResult HandleJoinTeam(IPlayer? player, string[] args)
    {
        if (player == null)
        {
            return HookResult.Continue;
        }

        if (args.Length < 2 || !int.TryParse(args[1], out int teamNum))
        {
            return HookResult.Continue;
        }

        Team joiningTeam = TeamUtility.TeamNumToTeam(teamNum);

        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null)
        {
            return HookResult.Continue;
        }

        Team expectedTeam = match.GetExpectedTeam(player);

        if (expectedTeam != Team.None && joiningTeam != expectedTeam)
        {
            return HookResult.Stop;
        }

        return HookResult.Continue;
    }
}
