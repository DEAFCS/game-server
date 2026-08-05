using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Entities;
using FiveStack.Enums;
using FiveStack.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FiveStack;

public class SurrenderSystem
{
    private readonly MatchEvents _matchEvents;
    private readonly MatchService _matchService;
    private readonly GameServer _gameServer;
    private readonly ILogger<ReadySystem> _logger;
    private readonly IServiceProvider _serviceProvider;
    public VoteSystem? surrenderingVote;

    private Dictionary<CsTeam, Dictionary<ulong, Timer>> _disconnectTimers =
        new Dictionary<CsTeam, Dictionary<ulong, Timer>>();

    private Guid? winningLineupId;

    public SurrenderSystem(
        ILogger<ReadySystem> logger,
        MatchEvents matchEvents,
        MatchService matchService,
        GameServer gameServer,
        IServiceProvider serviceProvider
    )
    {
        _logger = logger;
        _matchEvents = matchEvents;
        _matchService = matchService;
        _gameServer = gameServer;
        _serviceProvider = serviceProvider;
        Reset();
    }

    public void SetupDisconnectTimer(CsTeam team, ulong steamId)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        if (match == null || !match.IsInPlay())
        {
            return;
        }

        MatchData? matchData = match.GetMatchData();
        if (matchData == null)
        {
            return;
        }

        MatchMember? member = MatchUtility.GetMemberFromLineup(matchData, steamId.ToString(), "");
        if (member == null)
        {
            return;
        }

        if (!_disconnectTimers.ContainsKey(team))
        {
            _disconnectTimers[team] = new Dictionary<ulong, Timer>();
        }

        _disconnectTimers[team][steamId] = TimerUtility.AddTimer(
            60 * 3,
            () =>
            {
                SetupSurrender(team);
                PlayerAbandonedMatch(steamId);
            }
        );
    }

    // we dont pass the team in because they may not be on the team immediately after reconnecting
    public void CancelDisconnectTimer(ulong steamId)
    {
        bool canceledTimer = false;
        foreach (var _team in MatchUtility.Teams())
        {
            CsTeam team = TeamUtility.TeamNumToCSTeam(_team.TeamNum);

            if (_disconnectTimers.ContainsKey(team))
            {
                if (_disconnectTimers[team].ContainsKey(steamId))
                {
                    _disconnectTimers[team][steamId].Kill();
                    _disconnectTimers[team].Remove(steamId);
                    canceledTimer = true;
                }
            }
        }

        if (!canceledTimer)
        {
            return;
        }

        int currentPlayers = MatchUtility.Players().Count;

        int expectedPlayers = _matchService.GetCurrentMatch()?.GetExpectedPlayerCount() ?? 10;

        if (
            _matchService.GetCurrentMatch()?.IsPaused() == true
            && currentPlayers == expectedPlayers
        )
        {
            Reset();
            _matchService.GetCurrentMatch()?.ResumeMatch();
        }
    }

    public void SetupSurrender(CsTeam team, CCSPlayerController? player = null)
    {
        _logger.LogInformation($"Setting up surrender vote for {team}");
        if (surrenderingVote != null && surrenderingVote.IsVoteActive())
        {
            player?.PrintToConsole(" A surrender vote is already in progress");
            return;
        }

        surrenderingVote = _serviceProvider.GetRequiredService(typeof(VoteSystem)) as VoteSystem;

        if (surrenderingVote == null)
        {
            return;
        }

        _logger.LogInformation($"Starting Surrender Vote for {team}");
        surrenderingVote.StartVote(
            "Surrender",
            new CsTeam[] { team },
            () =>
            {
                _logger.LogInformation("surrender vote passed");
                // The surrendering team loses -- Surrender(x) credits x as
                // the winner, so the *other* team gets it.
                Surrender(TeamUtility.OppositeTeam(team));
                Reset();
            },
            () =>
            {
                _logger.LogInformation("surrender vote failed");
                Reset();
            },
            false,
            30
        );
    }

    // ".gg" -- separate from the always-available .surrender majority vote
    // above: only usable when the caller's own team is short a player, and
    // requires 100% consensus among whoever's currently present on that
    // team (not the full expected roster, since some of them are the ones
    // missing).
    public void SetupForfeitVote(CCSPlayerController player)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        if (match == null || !match.IsInPlayOrKnife())
        {
            player.PrintToConsole(" Cannot call .gg while the match is not live");
            return;
        }

        CsTeam team = player.Team;
        int currentTeamCount = MatchUtility.Players().Count(p => p.Team == team);
        int expectedTeamCount = match.GetExpectedPlayerCount() / 2;

        if (currentTeamCount >= expectedTeamCount)
        {
            player.PrintToConsole(" .gg is only available when your team is short a player");
            return;
        }

        if (surrenderingVote != null && surrenderingVote.IsVoteActive())
        {
            surrenderingVote.CastVote(player, true);
            return;
        }

        _logger.LogInformation($"Setting up forfeit (.gg) vote for {team}");

        surrenderingVote = _serviceProvider.GetRequiredService(typeof(VoteSystem)) as VoteSystem;

        if (surrenderingVote == null)
        {
            return;
        }

        CsTeam winningTeam = TeamUtility.OppositeTeam(team);

        surrenderingVote.StartVote(
            "Forfeit",
            new CsTeam[] { team },
            () =>
            {
                _logger.LogInformation("forfeit (.gg) vote passed");
                Surrender(winningTeam);
                Reset();
            },
            () =>
            {
                _logger.LogInformation("forfeit (.gg) vote failed");
                Reset();
            },
            false,
            30,
            true
        );

        surrenderingVote.CastVote(player, true);
    }

    public void Reset()
    {
        surrenderingVote = null;

        foreach (var team in _disconnectTimers.Keys)
        {
            foreach (var timer in _disconnectTimers[team].Values)
            {
                timer?.Kill();
            }
        }
        _disconnectTimers.Clear();
    }

    public bool IsSurrendering()
    {
        return surrenderingVote != null && surrenderingVote.IsVoteActive();
    }

    public void RemovePlayerVoteOnDisconnect(ulong steamId)
    {
        surrenderingVote?.RemovePlayerVote(steamId);
    }

    public void Surrender(CsTeam team)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        if (match == null)
        {
            return;
        }

        MatchData? matchData = match.GetMatchData();
        MatchMap? currentMap = match.GetCurrentMap();
        if (matchData == null || currentMap == null)
        {
            return;
        }

        // Side-aware lookup -- lineup.name is the team/clan name (e.g.
        // "Theft's Team"), never literally "CT"/"TERRORIST", so comparing it
        // against CSTeamToString(team) always fell through to lineup_2
        // regardless of which team actually won. GetLineupSide resolves
        // which lineup is currently playing as `team`, accounting for side
        // swaps, same as GetExpectedTeam/GetLineupPlayersForTeam.
        int roundsPlayed = _gameServer.GetTotalRoundsPlayed();
        Guid? lineup_id = null;

        if (
            TeamUtility.GetLineupSide(matchData, currentMap, matchData.lineup_1_id, roundsPlayed)
            == team
        )
        {
            lineup_id = matchData.lineup_1_id;
        }
        else if (
            TeamUtility.GetLineupSide(matchData, currentMap, matchData.lineup_2_id, roundsPlayed)
            == team
        )
        {
            lineup_id = matchData.lineup_2_id;
        }

        if (lineup_id == null)
        {
            _logger.LogWarning($"No lineup id found for {team}");
            return;
        }

        _logger.LogInformation($"Surrendering ${team}:{lineup_id.Value}");

        winningLineupId = lineup_id.Value;

        _matchService.GetCurrentMatch()?.UpdateMapStatus(eMapStatus.Surrendered);
    }

    public Guid? GetWinningLineupId()
    {
        return winningLineupId;
    }

    public void PlayerAbandonedMatch(ulong steamId)
    {
        _matchEvents.PublishGameEvent(
            "abandoned",
            new Dictionary<string, object>
            {
                { "time", DateTime.Now },
                { "steam_id", steamId.ToString() },
            }
        );
    }
}
