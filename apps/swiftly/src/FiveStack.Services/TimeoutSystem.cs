using FiveStack.Entities;
using FiveStack.Enums;
using FiveStack.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Translation;
using static SwiftlyS2.Shared.Helper;

namespace FiveStack;

public class TimeoutSystem
{
    private const int AutoTechnicalPauseSeconds = 2 * 60;

    private readonly HashSet<Team> _teamsPendingResume = new();
    private bool _requiresTeamResumeForCurrentPause;

    // One automatic 2-min technical pause per team per match -- triggered
    // when a player who already touched the server disconnects mid-match,
    // applied at the *next round start* rather than mid-round. Separate
    // from the manual/voted technical pause above, which has no duration
    // limit and can be called repeatedly.
    private readonly HashSet<Team> _usedAutoPause = new();
    private Team? _pendingAutoPauseTeam;
    private CancellationTokenSource? _autoPauseResumeTimer;
    private readonly ISwiftlyCore _core;
    private readonly MatchEvents _matchEvents;
    private readonly GameServer _gameServer;
    private readonly MatchService _matchService;
    private readonly GameBackUpRounds _backUpManagement;
    private readonly ILogger<TimeoutSystem> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly CoachSystem _coachSystem;
    private readonly CaptainSystem _captainSystem;
    private readonly ILocalizer _localizer;
    public VoteSystem? pauseVote;
    public VoteSystem? resumeVote;

    public TimeoutSystem(
        ISwiftlyCore core,
        ILogger<TimeoutSystem> logger,
        MatchEvents matchEvents,
        GameServer gameServer,
        MatchService matchService,
        GameBackUpRounds backUpManagement,
        IServiceProvider serviceProvider,
        CoachSystem coachSystem,
        CaptainSystem captainSystem,
        ILocalizer localizer
    )
    {
        _core = core;
        _logger = logger;
        _matchEvents = matchEvents;
        _gameServer = gameServer;
        _matchService = matchService;
        _serviceProvider = serviceProvider;
        _backUpManagement = backUpManagement;
        _coachSystem = coachSystem;
        _captainSystem = captainSystem;
        _localizer = localizer;
    }

    public void RemovePlayerVoteOnDisconnect(ulong steamId)
    {
        pauseVote?.RemovePlayerVote(steamId);
        resumeVote?.RemovePlayerVote(steamId);
    }

    public void RequestPause(IPlayer? player)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        if (match == null || !match.IsInPlay() || _backUpManagement.IsResettingRound())
        {
            _gameServer.Message(
                MessageType.Chat,
                _localizer["timeout.cannot_pause_not_live", ChatColors.Red],
                player
            );
            return;
        }

        if (IsTimeoutActive())
        {
            SendTimeoutAlreadyActiveMessage(player);
            return;
        }

        string pauseMessage = _localizer["timeout.admin_paused"];

        if (player != null)
        {
            if (!CanPause(player))
            {
                if (pauseVote != null && pauseVote.IsVoteActive())
                {
                    pauseVote.CastVote(player, true);
                    return;
                }

                pauseVote = _serviceProvider.GetRequiredService(typeof(VoteSystem)) as VoteSystem;

                if (pauseVote != null)
                {
                    pauseVote.StartVote(
                        _localizer["timeout.vote.technical"],
                        new Team[] { Team.CT, Team.T },
                        (
                            () =>
                            {
                                _logger.LogInformation("technical pause vote passed");
                                PauseTechMatch(_localizer["timeout.vote.technical_passed"]);
                                pauseVote = null;
                            }
                        ),
                        () =>
                        {
                            _logger.LogInformation("technical pause vote failed");
                            pauseVote = null;
                        },
                        true,
                        30
                    );

                    if (player != null && pauseVote != null)
                    {
                        pauseVote.CastVote(player, true);
                    }
                }

                return;
            }

            pauseMessage = _localizer["timeout.player_paused", player.Controller.PlayerName, ChatColors.Red];
        }

        PauseTechMatch(pauseMessage);
    }

    private bool CanPause(IPlayer? player)
    {
        if (player == null)
        {
            return true;
        }

        bool isCoach = _coachSystem.IsCoach(player, player.Controller.Team);
        bool isCaptain = _captainSystem.IsCaptain(player, player.Controller.Team);

        if (player.Controller.Clan == "[admin]" || player.Controller.Clan == "[organizer]" || player.Controller.Clan == "admin" || player.Controller.Clan == "organizer")
        {
            return true;
        }

        switch (GetTechnicalPauseSetting())
        {
            case eTimeoutSettings.Coach:
                if (!isCoach)
                {
                    return false;
                }
                break;
            case eTimeoutSettings.CoachAndCaptains:
                if (!isCoach && !isCaptain)
                {
                    return false;
                }
                break;
            case eTimeoutSettings.Admin:
                MatchData? matchData = _matchService.GetCurrentMatch()?.GetMatchData();

                if (matchData == null)
                {
                    return false;
                }

                MatchMember? lineupPlayer = MatchUtility.GetMemberFromLineup(
                    matchData,
                    player.SteamID.ToString(),
                    player.Controller.PlayerName
                );

                if (lineupPlayer == null)
                {
                    return false;
                }

                var roleEnum = PlayerRoleUtility.PlayerRoleStringToEnum(lineupPlayer.role);
                if (
                    roleEnum == ePlayerRoles.Administrator
                    || roleEnum == ePlayerRoles.MatchOrganizer
                    || roleEnum == ePlayerRoles.TournamentOrganizer
                )
                {
                    return true;
                }

                return false;
        }

        return true;
    }

    private bool CanCallTacticalTimeout(IPlayer? player)
    {
        if (player == null)
        {
            return true;
        }

        bool isCoach = _coachSystem.IsCoach(player, player.Controller.Team);
        bool isCaptain = _captainSystem.IsCaptain(player, player.Controller.Team);

        switch (GetTacticalTimeoutSetting())
        {
            case eTimeoutSettings.Coach:
                if (!isCoach)
                {
                    return false;
                }
                break;
            case eTimeoutSettings.CoachAndCaptains:
                if (!isCoach && !isCaptain)
                {
                    return false;
                }
                break;
            case eTimeoutSettings.Admin:
                return false;
        }

        return true;
    }

    private void CannotPauseMessage(IPlayer? player, string type)
    {
        _gameServer.Message(
            MessageType.Chat,
            _localizer["timeout.not_allowed", ChatColors.Red, type],
            player
        );
    }

    public void RequestResume(IPlayer? player, string? overrideMessage = null)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        MatchData? matchData = match?.GetMatchData();

        if (matchData == null)
        {
            return;
        }

        // A completely empty team is TeamEmptyForfeitSystem's exclusive
        // territory -- it runs its own countdown and forfeits automatically.
        // Manual .resume must not be able to force the match to play out
        // (and let the present side farm free rounds) against an empty team.
        if (
            match!.IsInPlayOrKnife()
            && (TeamUtility.GetTeamCount(Team.CT) == 0 || TeamUtility.GetTeamCount(Team.T) == 0)
        )
        {
            if (player != null)
            {
                _gameServer.Message(
                    MessageType.Chat,
                    _localizer["timeout.resume_blocked_team_empty", ChatColors.Red],
                    player
                );
            }
            return;
        }

        string resumeMessage = overrideMessage ?? _localizer["timeout.admin_resumed"];

        if (player != null)
        {
            if (ShouldRequireTeamResume())
            {
                if (IsAdminOrOrganizer(player, matchData))
                {
                    ClearPendingTeamResumes();
                    _matchService.GetCurrentMatch()?.ResumeMatch(resumeMessage);
                    return;
                }

                if (!CanPause(player))
                {
                    CannotPauseMessage(player, "resume");
                    return;
                }

                if (!_teamsPendingResume.Contains(player.Controller.Team))
                {
                    _gameServer.Message(
                        MessageType.Chat,
                        $" {ChatColors.Red}Your team has already resumed. Waiting for the other team.",
                        player
                    );
                    return;
                }

                _teamsPendingResume.Remove(player.Controller.Team);
                if (ShouldRequireTeamResume())
                {
                    _gameServer.Message(
                        MessageType.Alert,
                        $"{player.Controller.PlayerName} {ChatColors.Red}resumed for {player.Controller.Team}. Waiting for the other team to resume."
                    );
                    return;
                }
            }

            if (!CanPause(player))
            {
                if (resumeVote != null && resumeVote.IsVoteActive())
                {
                    resumeVote.CastVote(player, true);
                    return;
                }

                resumeVote = _serviceProvider.GetRequiredService(typeof(VoteSystem)) as VoteSystem;

                if (resumeVote != null)
                {
                    resumeVote.StartVote(
                        _localizer["timeout.vote.resume"],
                        new Team[] { Team.CT, Team.T },
                        (
                            () =>
                            {
                                _logger.LogInformation("resume vote passed");
                                _matchService
                                    .GetCurrentMatch()
                                    ?.ResumeMatch(_localizer["timeout.vote.resume_passed"]);
                                resumeVote = null;
                            }
                        ),
                        () =>
                        {
                            _logger.LogInformation("resume vote failed");
                            resumeVote = null;
                        },
                        true,
                        30
                    );

                    if (player != null && resumeVote != null)
                    {
                        resumeVote.CastVote(player, true);
                    }
                }

                return;
            }

            resumeMessage = _localizer["timeout.player_resumed", player.Controller.PlayerName, ChatColors.Red];
        }

        ClearPendingTeamResumes();
        _matchService.GetCurrentMatch()?.ResumeMatch(resumeMessage);
    }

    public void ClearPendingTeamResumes()
    {
        _teamsPendingResume.Clear();
        _requiresTeamResumeForCurrentPause = false;

        // Every resume path (manual, voted, or the automatic full-roster
        // resume in PlayerConnected) routes through here, so this is also
        // the right place to cancel a still-pending auto-pause resume timer
        // -- otherwise it fires again ~2 min later on an already-resumed
        // match. Harmless (ResumeMatch no-ops when not paused), but no
        // reason to leave a stale timer ticking.
        TimerUtility.Kill(_autoPauseResumeTimer);
        _autoPauseResumeTimer = null;
    }

    private bool ShouldRequireTeamResume()
    {
        return _requiresTeamResumeForCurrentPause && _teamsPendingResume.Count > 0;
    }

    private void PauseTechMatch(string pauseMessage)
    {
        _teamsPendingResume.Clear();
        _teamsPendingResume.Add(Team.CT);
        _teamsPendingResume.Add(Team.T);
        _requiresTeamResumeForCurrentPause = true;
        _matchService.GetCurrentMatch()?.PauseMatch(pauseMessage);
    }

    // Called from PlayerDisconnected when someone who already touched the
    // server disconnects mid-match. Doesn't pause immediately -- just
    // queues it for the next round start. A no-op if this team already used
    // their one automatic pause this match.
    public void RequestAutoPauseAtNextRound(Team team)
    {
        if (_usedAutoPause.Contains(team) || _pendingAutoPauseTeam == team)
        {
            return;
        }

        _pendingAutoPauseTeam = team;
        _logger.LogInformation($"Queued automatic technical pause for {team} at next round start");
    }

    // Called from OnRoundStart every round -- applies a queued automatic
    // pause, if any, right as the round begins. Returns true if it did.
    public bool TriggerPendingAutoPauseIfAny()
    {
        if (_pendingAutoPauseTeam == null)
        {
            return false;
        }

        Team team = _pendingAutoPauseTeam.Value;
        _pendingAutoPauseTeam = null;

        if (_usedAutoPause.Contains(team))
        {
            return false;
        }

        _usedAutoPause.Add(team);

        PauseTechMatch(_localizer["timeout.auto_technical_pause", team.ToString()]);

        TimerUtility.Kill(_autoPauseResumeTimer);
        _autoPauseResumeTimer = TimerUtility.AddTimer(
            AutoTechnicalPauseSeconds,
            () => RequestResume(null, _localizer["timeout.auto_technical_pause_ended"])
        );

        return true;
    }

    public void ResetAutoPause()
    {
        _usedAutoPause.Clear();
        _pendingAutoPauseTeam = null;
        TimerUtility.Kill(_autoPauseResumeTimer);
        _autoPauseResumeTimer = null;
    }

    public void CallTacTimeout(IPlayer? player)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        if (match == null || !match.IsInPlay() || _backUpManagement.IsResettingRound())
        {
            _gameServer.Message(
                MessageType.Chat,
                _localizer["timeout.cannot_tac_not_live", ChatColors.Red],
                player
            );
            return;
        }

        if (IsTimeoutActive())
        {
            SendTimeoutAlreadyActiveMessage(player);
            return;
        }

        if (player != null)
        {
            if (!CanCallTacticalTimeout(player))
            {
                CannotPauseMessage(player, "tactical timeout");
                return;
            }

            var rules = MatchUtility.Rules();
            int timeoutsAvailable =
                player.Controller.Team == Team.T
                    ? rules?.TerroristTimeOuts ?? 0
                    : rules?.CTTimeOuts ?? 0;

            if (timeoutsAvailable == 0)
            {
                _gameServer.Message(
                    MessageType.Chat,
                    _localizer["timeout.no_timeouts_left"],
                    player
                );
                return;
            }

            _gameServer.SendCommands([
                $"timeout_{(player.Controller.Team == Team.T ? "terrorist" : "ct")}_start",
            ]);

            _core.Scheduler.NextTick(() =>
            {
                int remaining = timeoutsAvailable - 1;

                _gameServer.Message(
                    MessageType.Alert,
                    _localizer[
                        "timeout.called_tactical",
                        player.Controller.PlayerName,
                        ChatColors.Red,
                        remaining
                    ]
                );

                PublishTimeoutState();
            });
        }
        else
        {
            _gameServer.Message(MessageType.Alert, _localizer["timeout.called_admin"]);
        }
    }

    private eTimeoutSettings GetTechnicalPauseSetting()
    {
        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null || !match.IsInPlay() && _backUpManagement.IsResettingRound() == false)
        {
            return eTimeoutSettings.Admin;
        }

        MatchData? matchData = match.GetMatchData();

        if (matchData == null)
        {
            return eTimeoutSettings.Admin;
        }

        eTimeoutSettings timeoutSetting = TimeoutUtility.TimeoutSettingStringToEnum(
            matchData.options.tech_timeout_setting
        );

        return timeoutSetting;
    }

    private eTimeoutSettings GetTacticalTimeoutSetting()
    {
        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null || !match.IsInPlay() && _backUpManagement.IsResettingRound() == false)
        {
            return eTimeoutSettings.Admin;
        }

        MatchData? matchData = match.GetMatchData();

        if (matchData == null)
        {
            return eTimeoutSettings.Admin;
        }

        eTimeoutSettings timeoutSetting = TimeoutUtility.TimeoutSettingStringToEnum(
            matchData.options.timeout_setting
        );

        return timeoutSetting;
    }

    private bool IsAdminOrOrganizer(IPlayer player, MatchData matchData)
    {
        if (player.Controller.Clan == "[admin]" || player.Controller.Clan == "[organizer]" || player.Controller.Clan == "admin" || player.Controller.Clan == "organizer")
        {
            return true;
        }

        MatchMember? lineupPlayer = MatchUtility.GetMemberFromLineup(
            matchData,
            player.SteamID.ToString(),
            player.Controller.PlayerName
        );

        if (lineupPlayer == null)
        {
            return false;
        }

        var roleEnum = PlayerRoleUtility.PlayerRoleStringToEnum(lineupPlayer.role);
        return roleEnum == ePlayerRoles.Administrator
            || roleEnum == ePlayerRoles.MatchOrganizer
            || roleEnum == ePlayerRoles.TournamentOrganizer;
    }

    private void SendTimeoutAlreadyActiveMessage(IPlayer? player)
    {
        if (player == null)
        {
            return;
        }

        _gameServer.Message(
            MessageType.Chat,
            _localizer["timeout.already_active", ChatColors.Red],
            player
        );
    }

    public bool IsTimeoutActive()
    {
        return MatchUtility.Rules()?.TerroristTimeOutActive == true
            || MatchUtility.Rules()?.CTTimeOutActive == true;
    }

    public (int lineup1Timeouts, int lineup2Timeouts) GetLineupTimeouts()
    {
        var rules = MatchUtility.Rules();
        int tTimeouts = rules?.TerroristTimeOuts ?? 0;
        int ctTimeouts = rules?.CTTimeOuts ?? 0;

        MatchManager? match = _matchService.GetCurrentMatch();
        MatchData? matchData = match?.GetMatchData();
        MatchMap? currentMap = match?.GetCurrentMap();

        if (matchData == null || currentMap == null)
        {
            return (0, 0);
        }

        int totalRoundsPlayed = _gameServer.GetTotalRoundsPlayed();

        Team lineup1Side = TeamUtility.GetLineupSide(
            matchData,
            currentMap,
            matchData.lineup_1_id,
            totalRoundsPlayed
        );

        if (lineup1Side == Team.T)
        {
            return (tTimeouts, ctTimeouts);
        }

        return (ctTimeouts, tTimeouts);
    }

    public void PublishTimeoutState()
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        MatchMap? currentMap = match?.GetCurrentMap();

        if (currentMap == null)
        {
            return;
        }

        Guid mapId = match!.GetActiveMapId() ?? currentMap.id;

        (int lineup1Timeouts, int lineup2Timeouts) = GetLineupTimeouts();

        _matchEvents.PublishGameEvent(
            "techTimeout",
            new Dictionary<string, object>
            {
                { "map_id", mapId },
                { "lineup_1_timeouts_available", lineup1Timeouts },
                { "lineup_2_timeouts_available", lineup2Timeouts },
            }
        );
    }
}
