using FiveStack.Entities;
using FiveStack.Enums;
using FiveStack.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Translation;
using static SwiftlyS2.Shared.Helper;

namespace FiveStack;

public class SurrenderSystem
{
    private readonly MatchEvents _matchEvents;
    private readonly MatchService _matchService;
    private readonly GameServer _gameServer;
    private readonly ILogger<ReadySystem> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILocalizer _localizer;
    public VoteSystem? surrenderingVote;

    private Guid? winningLineupId;

    public SurrenderSystem(
        ILogger<ReadySystem> logger,
        MatchEvents matchEvents,
        MatchService matchService,
        GameServer gameServer,
        IServiceProvider serviceProvider,
        ILocalizer localizer
    )
    {
        _logger = logger;
        _matchEvents = matchEvents;
        _matchService = matchService;
        _gameServer = gameServer;
        _serviceProvider = serviceProvider;
        _localizer = localizer;
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
            _gameServer.Message(
                MessageType.Chat,
                _localizer["forfeit.not_live", ChatColors.Red],
                player
            );
            return;
        }

        // MM-only -- tournament matches have an admin/organizer expected to
        // resolve a no-show manually instead of a player-triggered vote.
        // Draft matches too, now that DisconnectBudgetSystem is disabled
        // for them (no cooldown for leaving a draft) -- the
        // "allMissingConfirmedGone" check below would otherwise never be
        // satisfiable, since a draft player's budget can never exhaust;
        // a whole-empty-team draft still resolves via
        // TeamEmptyForfeitSystem's own independent 5-min clock, a partial
        // no-show is the host/organizer's call, same as tournament.
        MatchData? matchData = match.GetMatchData();
        if (matchData?.is_tournament_match == true || matchData?.is_draft_match == true)
        {
            _gameServer.Message(
                MessageType.Chat,
                _localizer["forfeit.disabled_tournament", ChatColors.Red],
                player
            );
            return;
        }

        int expectedTeamCount = match.GetExpectedPlayerCount() / 2;

        // A "short" team in Duel (1v1) would have 0 players on it -- nobody
        // would be left to type .gg. The automatic forfeit timer
        // (DisconnectBudgetSystem/TeamEmptyForfeitSystem) already handles a
        // missing 1v1 opponent, so there's nothing for this vote to do here.
        if (expectedTeamCount <= 1)
        {
            _gameServer.Message(
                MessageType.Chat,
                _localizer["forfeit.disabled_duel", ChatColors.Red],
                player
            );
            return;
        }

        Team team = player.Controller.Team;
        int currentTeamCount = MatchUtility
            .Players()
            .Count(p => p.Controller.Team == team);

        if (currentTeamCount >= expectedTeamCount)
        {
            _gameServer.Message(
                MessageType.Chat,
                _localizer["forfeit.not_short", ChatColors.Red],
                player
            );
            return;
        }

        // .gg is the deliberate "they're confirmed gone" call, not a panic
        // button for a normal disconnect -- the missing player still has up
        // to 5 minutes to reconnect (DisconnectBudgetSystem) before this is
        // even offered. Same "gone for good" signal TeamEmptyForfeitSystem
        // already uses to skip a pointless countdown for an already-banned
        // roster.
        List<MatchMember> roster = match.GetLineupPlayersForTeam(team);
        HashSet<ulong> connectedSteamIds = new HashSet<ulong>(
            MatchUtility.Players().Where(p => p.Controller.Team == team).Select(p => p.SteamID)
        );

        List<ulong> missingSteamIds = new List<ulong>();
        foreach (MatchMember member in roster)
        {
            if (
                member.steam_id != null
                && ulong.TryParse(member.steam_id, out ulong steamId)
                && !connectedSteamIds.Contains(steamId)
            )
            {
                missingSteamIds.Add(steamId);
            }
        }

        bool allMissingConfirmedGone =
            missingSteamIds.Count > 0
            && missingSteamIds.All(steamId => match.disconnectBudgetSystem.IsBudgetExhausted(steamId));

        if (!allMissingConfirmedGone)
        {
            _gameServer.Message(
                MessageType.Chat,
                _localizer["forfeit.waiting_for_reconnect", ChatColors.Red],
                player
            );
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

    // Reported bug: TeamEmptyForfeitSystem announced "match forfeited" in
    // chat, then nothing actually happened -- the match kept playing 2v0
    // for hours with no server-side trace of what went wrong (this used to
    // silently `return` on a single failed lookup, with only a LogWarning
    // that's easy to miss). matchData/currentMap/the lineup-side lookup can
    // all transiently be unavailable for a moment around a status
    // transition, so retry a few times before giving up -- and if it's
    // still stuck after that, log at Error so it's actually visible instead
    // of leaving the match hung indefinitely with no signal anything is
    // wrong.
    public void Surrender(Team team, int retriesLeft = 5)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        MatchData? matchData = match?.GetMatchData();
        MatchMap? currentMap = match?.GetCurrentMap();

        // Side-aware lookup -- lineup.name is the team/clan name (e.g.
        // "Theft's Team"), never literally "CT"/"TERRORIST", so comparing it
        // against TeamToString(team) always fell through to lineup_2
        // regardless of which team actually won. GetLineupSide resolves
        // which lineup is currently playing as `team`, accounting for side
        // swaps, same as GetExpectedTeam/GetLineupPlayersForTeam.
        Guid? lineup_id = null;

        if (match != null && matchData != null && currentMap != null)
        {
            int roundsPlayed = _gameServer.GetTotalRoundsPlayed();

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
        }

        if (lineup_id == null)
        {
            if (retriesLeft > 0)
            {
                _logger.LogWarning(
                    $"Surrender({team}) could not resolve a lineup yet (match={match != null}, matchData={matchData != null}, currentMap={currentMap != null}) -- retrying, {retriesLeft} attempt(s) left"
                );
                TimerUtility.AddTimer(1.0f, () => Surrender(team, retriesLeft - 1));
                return;
            }

            _logger.LogError(
                $"Surrender({team}) failed permanently after retries -- match is likely stuck and needs manual intervention (match={match != null}, matchData={matchData != null}, currentMap={currentMap != null})"
            );
            return;
        }

        _logger.LogInformation($"Surrendering ${team}:{lineup_id.Value}");

        winningLineupId = lineup_id.Value;

        // Nothing up to this point tells the CS2 engine itself that
        // anything happened -- only our own internal status flag and the
        // API's database changed. The live round (and, since the match was
        // never natively "clinched", every round after it) just kept
        // playing out as normal until the server was eventually torn down
        // minutes later (tv_delay after the match already concluded
        // API-side) -- reported bug: ".gg says forfeit completed, but the
        // match keeps playing for a couple more minutes." Force CS2's own
        // native surrender round-end (real "CTs/Terrorists Surrender"
        // banner, not just our chat message) and freeze the server the same
        // way TeamEmptyForfeitSystem already does, so nothing else is
        // playable while everything else winds down.
        Team losingTeam = TeamUtility.OppositeTeam(team);
        RoundEndReason surrenderReason =
            losingTeam == Team.CT ? RoundEndReason.CTsSurrender : RoundEndReason.TerroristsSurrender;
        MatchUtility.Rules()?.TerminateRound(surrenderReason, 3.0f);
        match?.PauseMatch("Match surrendered", true);

        // UpdateMapStatus's winningLineupId parameter publishes a "mapStatus"
        // game event, which the API only uses to update the match_map row --
        // MatchMapStatusEvent only runs its match-finishing logic for the
        // literal status "Finished", never "Surrendered", so this alone never
        // touches the parent match's own status/winner and never triggers
        // the API's stop-server/ELO/cleanup cascade (that's all driven off
        // matches.status, not match_maps.status). The API does have a
        // dedicated "surrender" game event (-> MatchSurrendered) that sets
        // matches.status directly to one of its TERMINAL_STATUSES -- publish
        // that too, since UpdateMapStatus alone was never enough to actually
        // end the match.
        _matchEvents.PublishGameEvent(
            "surrender",
            new Dictionary<string, object>
            {
                { "winning_lineup_id", lineup_id.Value.ToString() },
            }
        );

        match?.UpdateMapStatus(eMapStatus.Surrendered, lineup_id.Value);
    }

    public Guid? GetWinningLineupId()
    {
        return winningLineupId;
    }
}
