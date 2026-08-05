using FiveStack.Entities;
using FiveStack.Enums;
using FiveStack.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace FiveStack;

public class SurrenderSystem
{
    private readonly MatchEvents _matchEvents;
    private readonly MatchService _matchService;
    private readonly GameServer _gameServer;
    private readonly ILogger<ReadySystem> _logger;
    private readonly IServiceProvider _serviceProvider;
    public VoteSystem? surrenderingVote;

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

    // ".gg" -- the only forfeit path now (the old always-available
    // .surrender majority vote was removed to avoid confusing players with
    // two different commands). Only usable when the caller's own team is
    // short a player, and requires 100% consensus among whoever's currently
    // present on that team (not the full expected roster, since some of
    // them are the ones missing).
    public void SetupForfeitVote(IPlayer player)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        if (match == null || !match.IsInPlayOrKnife())
        {
            player.SendConsole(" Cannot call .gg while the match is not live");
            return;
        }

        Team team = player.Controller.Team;
        int currentTeamCount = MatchUtility
            .Players()
            .Count(p => p.Controller.Team == team);
        int expectedTeamCount = match.GetExpectedPlayerCount() / 2;

        if (currentTeamCount >= expectedTeamCount)
        {
            player.SendConsole(" .gg is only available when your team is short a player");
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

        Team winningTeam = TeamUtility.OppositeTeam(team);

        surrenderingVote.StartVote(
            "Forfeit",
            new Team[] { team },
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

    // Called from OnRoundStart -- a .gg vote only stays valid for the round
    // it was started in. If it's still unresolved once a new round begins,
    // cancel it instead of letting it linger; someone has to type .gg again.
    public void CancelPendingForfeitVote()
    {
        if (surrenderingVote != null && surrenderingVote.IsVoteActive())
        {
            surrenderingVote.CancelVote();
        }
    }

    public void Reset()
    {
        surrenderingVote = null;
    }

    public bool IsSurrendering()
    {
        return surrenderingVote != null && surrenderingVote.IsVoteActive();
    }

    public void RemovePlayerVoteOnDisconnect(ulong steamId)
    {
        surrenderingVote?.RemovePlayerVote(steamId);
    }

    public void Surrender(Team team)
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
        // against TeamToString(team) always fell through to lineup_2
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
}
