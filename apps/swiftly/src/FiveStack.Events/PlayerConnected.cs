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
    private readonly HashSet<ulong> _overCapacityKicks = new();

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

        _overCapacityKicks.Remove(player.SteamID);

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
        int capacity = match.GetExpectedPlayerCount() / 2;

        // TeamUtility.GetTeamCount only checked whether the SIDE had anyone on
        // it at all, not whether THIS player's own lineup already had a full
        // team playing -- wrong through a halftime swap (the other lineup
        // still stands on this side until the round resets) and wrong in
        // general (it can't actually count past 1). Count this lineup's own
        // connected, playing members instead.
        if (
            LineupCapacityUtility.IsOverCapacity(
                MatchUtility
                    .Players()
                    .Select(connected =>
                        (
                            connected.SteamID.ToString(),
                            MatchUtility.GetPlayerLineup(matchData, connected),
                            (int)connected.Controller.Team
                        )
                    ),
                player.SteamID.ToString(),
                lineup_id,
                (int)expectedTeam,
                capacity
            )
        )
        {
            // A kick counts as a disconnect too (see OnPlayerDisconnect) --
            // decided before that fires so the kicked player is neither
            // treated as having left the match nor counted towards a whole
            // roster.
            _overCapacityKicks.Add(player.SteamID);
            _core.Engine.ExecuteCommand($"kickid {player.UserID}");
            return HookResult.Continue;
        }

        // Deferred one tick so this doesn't race CS2's own native
        // halftime/OT side-swap (CCSGameRules::Think
        // bHalftime SwitchTeamsAtRoundReset(), logged as "OnPreResetRound"),
        // which blindly flips whichever team every currently-connected
        // player is on to the opposite side once bHalftime is set -- not
        // FiveStack code, so we can't hook into or order against it
        // directly. If a player reconnects in the same tick that native
        // swap fires, assigning their final expected team synchronously
        // here meant the native swap then flipped them AGAIN right after,
        // landing them on the wrong side -- reported live as a 6v4 split
        // right after an OT halftime switch (the reconnecting player was
        // flipped twice while everyone else, already sitting on their
        // pre-switch side, only got flipped once by the native swap).
        // Running this a tick later gives the native swap a chance to
        // fire first, so our assignment is the authoritative last word.
        _core.Scheduler.NextTick(() =>
        {
            if (!player.IsValid)
            {
                return;
            }

            match.EnforceMemberTeam(player, Team.None);
        });

        _matchEvents.PublishGameEvent(
            "player-connected",
            new Dictionary<string, object>
            {
                { "player_name", player.Name },
                { "steam_id", player.SteamID.ToString() },
            }
        );

        match.warmupShortenSystem.Check();

        // Deferred one tick, same reasoning as OnPlayerDisconnect's own
        // Check() call: at the moment player_connect_full fires, the engine
        // hasn't finished adding this player back to its own player list
        // yet, so a synchronous team-count/PlayerCount() read here still
        // sees them as absent. That read a stale "still empty" team, which
        // just re-affirmed TeamEmptyForfeitSystem's existing pause instead
        // of clearing it (its "already tracking" branch doesn't resume),
        // and PlayerCount() undercounted by one so the explicit resume
        // below never fired either -- with no further connect/disconnect
        // event to ever retry it, the match stayed paused for the rest of
        // the match. Reported live: reconnect within ~30s of a disconnect,
        // match stuck paused until auto-cancel.
        _core.Scheduler.NextTick(() =>
        {
            if (!player.IsValid)
            {
                return;
            }

            match.teamEmptyForfeitSystem.Check();

            // Auto-resume the moment the full expected roster is back,
            // instead of requiring someone to type .resume manually. Safe
            // to call unconditionally -- ResumeMatch() is a no-op (beyond
            // an idempotent mp_unpause_match) when the match isn't
            // actually paused.
            if (MatchUtility.PlayerCount() == match.GetExpectedPlayerCount())
            {
                match.ResumeMatch();
            }
        });

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
