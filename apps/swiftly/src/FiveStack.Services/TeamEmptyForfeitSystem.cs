using FiveStack.Entities;
using FiveStack.Utilities;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Translation;
using static SwiftlyS2.Shared.Helper;

namespace FiveStack;

// Fresh, independent 5-minute forfeit clock for when an entire team has zero
// connected players -- separate from each individual's own disconnect budget
// (DisconnectBudgetSystem). If everyone on the empty team is already
// permanently banned (budget exhausted for all of them), there's no chance
// any of them come back, so the match forfeits immediately instead of
// running a pointless countdown.
public class TeamEmptyForfeitSystem
{
    private const int ForfeitSeconds = 5 * 60;
    private static readonly int[] MilestoneSeconds = { 5 * 60, 4 * 60, 3 * 60, 2 * 60, 60 };

    private readonly MatchService _matchService;
    private readonly GameServer _gameServer;
    private readonly ILocalizer _localizer;
    private readonly SurrenderSystem _surrenderSystem;
    private readonly ILogger<TeamEmptyForfeitSystem> _logger;

    private Team? _trackedEmptyTeam;
    private CancellationTokenSource? _forfeitTimer;
    private List<CancellationTokenSource> _milestoneTimers = new List<CancellationTokenSource>();

    public TeamEmptyForfeitSystem(
        ILogger<TeamEmptyForfeitSystem> logger,
        MatchService matchService,
        GameServer gameServer,
        ILocalizer localizer,
        SurrenderSystem surrenderSystem
    )
    {
        _logger = logger;
        _matchService = matchService;
        _gameServer = gameServer;
        _localizer = localizer;
        _surrenderSystem = surrenderSystem;
    }

    // Call after any connect/disconnect while a match is live/knife.
    public void Check()
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        if (match == null || !match.IsInPlayOrKnife())
        {
            CancelTracking();
            return;
        }

        foreach (Team team in new[] { Team.CT, Team.T })
        {
            if (TeamUtility.GetTeamCount(team) == 0)
            {
                HandleEmptyTeam(match, team);
                return;
            }
        }

        CancelTracking(match);
    }

    private void HandleEmptyTeam(MatchManager match, Team emptyTeam)
    {
        if (_trackedEmptyTeam == emptyTeam)
        {
            return;
        }

        CancelTracking();
        _trackedEmptyTeam = emptyTeam;

        Team winningTeam = emptyTeam == Team.CT ? Team.T : Team.CT;

        if (IsRosterFullyBanned(match, emptyTeam))
        {
            _logger.LogInformation(
                $"Team {emptyTeam} is empty and every roster player is already banned -- forfeiting immediately"
            );
            Forfeit(winningTeam);
            return;
        }

        // Freeze the game while the team is empty -- otherwise rounds (knife
        // included) can play out and resolve against nobody while the
        // forfeit countdown is still running.
        match.PauseMatch($"{emptyTeam} has no players connected", true);

        _forfeitTimer = TimerUtility.AddTimer(ForfeitSeconds, () => Forfeit(winningTeam));

        foreach (int milestone in MilestoneSeconds)
        {
            float delay = ForfeitSeconds - milestone;
            if (delay <= 0)
            {
                AnnounceMilestone(milestone);
                continue;
            }

            _milestoneTimers.Add(
                TimerUtility.AddTimer(delay, () => AnnounceMilestone(milestone))
            );
        }
    }

    private bool IsRosterFullyBanned(MatchManager match, Team emptyTeam)
    {
        List<MatchMember> roster = match.GetLineupPlayersForTeam(emptyTeam);

        if (roster.Count == 0)
        {
            return false;
        }

        return roster.All(member =>
            member.steam_id != null
            && ulong.TryParse(member.steam_id, out ulong steamId)
            && match.disconnectBudgetSystem.IsBudgetExhausted(steamId)
        );
    }

    private void AnnounceMilestone(int secondsRemaining)
    {
        int minutesRemaining = secondsRemaining / 60;
        _gameServer.Message(
            MessageType.Chat,
            _localizer["team_empty.forfeit_warning", minutesRemaining]
        );
    }

    private void Forfeit(Team winningTeam)
    {
        _gameServer.Message(MessageType.Chat, _localizer["team_empty.forfeited"]);
        _surrenderSystem.Surrender(winningTeam);
        CancelTracking();
    }

    private void CancelTracking(MatchManager? resumeMatch = null)
    {
        bool wasTracking = _trackedEmptyTeam != null;
        _trackedEmptyTeam = null;

        TimerUtility.Kill(_forfeitTimer);
        _forfeitTimer = null;

        foreach (var timer in _milestoneTimers)
        {
            TimerUtility.Kill(timer);
        }
        _milestoneTimers.Clear();

        if (wasTracking && resumeMatch != null)
        {
            resumeMatch.ResumeMatch();
        }
    }

    public void Reset()
    {
        CancelTracking();
    }
}
