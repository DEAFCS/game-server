using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Entities;
using FiveStack.Utilities;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

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

    private readonly MatchService _matchService;
    private readonly GameServer _gameServer;
    private readonly IStringLocalizer _localizer;
    private readonly SurrenderSystem _surrenderSystem;
    private readonly ILogger<TeamEmptyForfeitSystem> _logger;

    private CsTeam? _trackedEmptyTeam;
    private Timer? _forfeitTimer;

    // Exposed so TimeoutSystem can refuse .tac/.timeout while this is
    // actively holding the server paused for an empty team -- calling CS2's
    // native timeout_*_start on top of our own mp_pause_match drops our
    // pause (CS2 won't hold mp_pause_match while its own native tactical
    // timeout is running), and once the native timeout naturally ends CS2
    // just resumes play -- but this system's own forfeit countdown was
    // never cancelled, so the match keeps running against the still-empty
    // team until the (now pointless) countdown finally fires.
    public bool IsActivelyPausing => _trackedEmptyTeam != null;

    public TeamEmptyForfeitSystem(
        ILogger<TeamEmptyForfeitSystem> logger,
        MatchService matchService,
        GameServer gameServer,
        IStringLocalizer localizer,
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
            _logger.LogInformation(
                $"Check: skipping, match={match != null} isInPlayOrKnife={match?.IsInPlayOrKnife()}"
            );
            CancelTracking();
            return;
        }

        int ctCount = TeamUtility.GetTeamCount(CsTeam.CounterTerrorist);
        int tCount = TeamUtility.GetTeamCount(CsTeam.Terrorist);
        _logger.LogInformation($"Check: ctCount={ctCount} tCount={tCount}");

        foreach (CsTeam team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
        {
            if (TeamUtility.GetTeamCount(team) == 0)
            {
                HandleEmptyTeam(match, team);
                return;
            }
        }

        CancelTracking(match);
    }

    private void HandleEmptyTeam(MatchManager match, CsTeam emptyTeam)
    {
        if (_trackedEmptyTeam == emptyTeam)
        {
            // Already tracking this team as empty -- the forfeit clock and
            // milestones are already running, don't restart them. Still
            // re-send the pause itself, though (harmless if already
            // paused): CS2's own live-match-start processing (cfg exec,
            // mp_restartgame, run when a knife decision finalizes) can
            // silently drop an earlier mp_pause_match, and this is what
            // actually keeps the match held once that happens.
            _logger.LogInformation($"HandleEmptyTeam({emptyTeam}): already tracking, re-affirming pause");
            match.PauseMatch($"{emptyTeam} has no players connected", true);
            return;
        }

        _logger.LogInformation($"HandleEmptyTeam({emptyTeam}): now tracking, will pause + start forfeit clock");

        CancelTracking();
        _trackedEmptyTeam = emptyTeam;

        CsTeam winningTeam =
            emptyTeam == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;

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
    }

    private bool IsRosterFullyBanned(MatchManager match, CsTeam emptyTeam)
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

    private void Forfeit(CsTeam winningTeam)
    {
        _gameServer.Message(
            HudDestination.Chat,
            $"{ChatColors.Orange}[DEAFCS] {ChatColors.Red}" + _localizer["team_empty.forfeited"]
        );
        _surrenderSystem.Surrender(winningTeam);
        CancelTracking();
    }

    private void CancelTracking(MatchManager? resumeMatch = null)
    {
        bool wasTracking = _trackedEmptyTeam != null;
        _trackedEmptyTeam = null;

        _forfeitTimer?.Kill();
        _forfeitTimer = null;

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
