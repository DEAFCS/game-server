using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Entities;
using FiveStack.Enums;
using FiveStack.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FiveStack;

public class TimeoutSystem
{
    private const int AutoTechnicalPauseSeconds = 2 * 60;

    private readonly HashSet<CsTeam> _teamsPendingResume = new();
    private bool _requiresTeamResumeForCurrentPause;

    // One automatic 2-min technical pause per team per match -- triggered
    // when a player who already touched the server disconnects mid-match,
    // applied at the *next round start* rather than mid-round. Separate
    // from the manual/voted technical pause above, which has no duration
    // limit and can be called repeatedly.
    private readonly HashSet<CsTeam> _usedAutoPause = new();
    private CsTeam? _pendingAutoPauseTeam;
    private Timer? _autoPauseResumeTimer;

    // Live-ticking "TECHNICAL TIMEOUT: mm:ss" alert, refreshed every second
    // for the duration of the auto pause. mp_pause_match (used above) is a
    // generic, indefinite admin pause -- unlike a native tactical timeout
    // (.tac), CS2 has no idea it's supposed to end in 2 min and shows no
    // countdown of its own, so without this players just see a static
    // "MATCH PAUSED" with no indication anything is timed. Mirrors
    // AutoCancelCountdownSystem's live countdown for the same reason.
    private Timer? _autoPauseCountdownTimer;
    private DateTime? _autoPauseEndsAt;
    private readonly MatchEvents _matchEvents;
    private readonly GameServer _gameServer;
    private readonly MatchService _matchService;
    private readonly GameBackUpRounds _backUpManagement;
    private readonly ILogger<TimeoutSystem> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly CoachSystem _coachSystem;
    private readonly CaptainSystem _captainSystem;
    private readonly IStringLocalizer _localizer;
    public VoteSystem? pauseVote;
    public VoteSystem? resumeVote;

    public TimeoutSystem(
        ILogger<TimeoutSystem> logger,
        MatchEvents matchEvents,
        GameServer gameServer,
        MatchService matchService,
        GameBackUpRounds backUpManagement,
        IServiceProvider serviceProvider,
        CoachSystem coachSystem,
        CaptainSystem captainSystem,
        IStringLocalizer localizer
    )
    {
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

    public void RequestPause(CCSPlayerController? player)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        if (match == null || !match.IsInPlay() || _backUpManagement.IsResettingRound())
        {
            _gameServer.Message(
                HudDestination.Chat,
                _localizer["timeout.cannot_pause_not_live", ChatColors.Red],
                player
            );
            return;
        }

        // Technical timeout (.tech/.pause) is tournament-only -- matchmaking
        // and other custom matches can still call a tactical timeout
        // (.tac), just not this one. Console/RCON (player == null) is left
        // alone; this only gates the player-typed chat command.
        if (player != null && match.GetMatchData()?.is_tournament_match != true)
        {
            _gameServer.Message(
                HudDestination.Chat,
                _localizer["timeout.tech_tournament_only", ChatColors.Red],
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
                        new CsTeam[] { CsTeam.CounterTerrorist, CsTeam.Terrorist },
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

            pauseMessage = _localizer["timeout.player_paused", player.PlayerName, ChatColors.Red];
        }

        PauseTechMatch(pauseMessage);
    }

    private bool CanPause(CCSPlayerController? player)
    {
        if (player == null)
        {
            return true;
        }

        bool isCoach = _coachSystem.IsCoach(player, player.Team);
        bool isCaptain = _captainSystem.IsCaptain(player, player.Team);

        if (player.Clan == "[admin]" || player.Clan == "[organizer]" || player.Clan == "admin" || player.Clan == "organizer")
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
                // "Admin" doesn't mean literally nobody else -- the team
                // captain (and anyone tagged Administrator/Organizer on the
                // roster) can still call it. An admin who's also playing
                // shouldn't have to leave the match to hit console every
                // time a pause is needed.
                if (isCaptain)
                {
                    return true;
                }

                MatchData? matchData = _matchService.GetCurrentMatch()?.GetMatchData();

                if (matchData == null)
                {
                    return false;
                }

                return IsAdminOrOrganizer(player, matchData);
        }

        return true;
    }

    private bool CanCallTacticalTimeout(CCSPlayerController? player)
    {
        if (player == null)
        {
            return true;
        }

        bool isCoach = _coachSystem.IsCoach(player, player.Team);
        bool isCaptain = _captainSystem.IsCaptain(player, player.Team);

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
                // Same carve-out as CanPause -- captain or an
                // Administrator/Organizer-tagged roster member can still
                // call it even under "Admin".
                if (isCaptain)
                {
                    return true;
                }

                MatchData? matchData = _matchService.GetCurrentMatch()?.GetMatchData();

                if (matchData == null)
                {
                    return false;
                }

                return IsAdminOrOrganizer(player, matchData);
        }

        return true;
    }

    private void CannotPauseMessage(CCSPlayerController? player, string type)
    {
        _gameServer.Message(
            HudDestination.Chat,
            _localizer["timeout.not_allowed", ChatColors.Red, type],
            player
        );
    }

    public void RequestResume(CCSPlayerController? player, string? overrideMessage = null)
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
            && (
                TeamUtility.GetTeamCount(CsTeam.CounterTerrorist) == 0
                || TeamUtility.GetTeamCount(CsTeam.Terrorist) == 0
            )
        )
        {
            if (player != null)
            {
                _gameServer.Message(
                    HudDestination.Chat,
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

                if (!_teamsPendingResume.Contains(player.Team))
                {
                    _gameServer.Message(
                        HudDestination.Chat,
                        $" {ChatColors.Red}Your team has already resumed. Waiting for the other team.",
                        player
                    );
                    return;
                }

                _teamsPendingResume.Remove(player.Team);
                if (ShouldRequireTeamResume())
                {
                    _gameServer.Message(
                        HudDestination.Alert,
                        $"{player.PlayerName} {ChatColors.Red}resumed for {player.Team}. Waiting for the other team to resume."
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
                        new CsTeam[] { CsTeam.CounterTerrorist, CsTeam.Terrorist },
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

            resumeMessage = _localizer["timeout.player_resumed", player.PlayerName, ChatColors.Red];
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
        _autoPauseResumeTimer?.Kill();
        _autoPauseResumeTimer = null;
        StopAutoPauseCountdown();
    }

    private bool ShouldRequireTeamResume()
    {
        return _requiresTeamResumeForCurrentPause && _teamsPendingResume.Count > 0;
    }

    private void PauseTechMatch(string pauseMessage)
    {
        _teamsPendingResume.Clear();
        _teamsPendingResume.Add(CsTeam.CounterTerrorist);
        _teamsPendingResume.Add(CsTeam.Terrorist);
        _requiresTeamResumeForCurrentPause = true;
        _matchService.GetCurrentMatch()?.PauseMatch(pauseMessage);
    }

    // Called from PlayerDisconnected when someone who already touched the
    // server disconnects mid-match. Doesn't pause immediately -- just
    // queues it for the next round start. A no-op if this team already used
    // their one automatic pause this match.
    public void RequestAutoPauseAtNextRound(CsTeam team)
    {
        if (_usedAutoPause.Contains(team) || _pendingAutoPauseTeam == team)
        {
            return;
        }

        _pendingAutoPauseTeam = team;
        _logger.LogInformation($"Queued automatic technical pause for {team} at next round start");
    }

    // Called when a player reconnects. If the reconnecting player's team is
    // the one with a queued auto-pause and everyone is back, cancels the
    // queued pause -- otherwise it fires at the next round start even though
    // the player who disconnected is already back (the pause would then
    // needlessly hold up a round that no longer has anyone missing).
    public void CancelPendingAutoPauseForTeam(CsTeam team)
    {
        if (_pendingAutoPauseTeam == team)
        {
            _pendingAutoPauseTeam = null;
            _logger.LogInformation($"Cancelled queued automatic technical pause for {team} -- player reconnected");
        }
    }

    // Called from OnRoundStart every round -- applies a queued automatic
    // pause, if any, right as the round begins. Returns true if it did.
    public bool TriggerPendingAutoPauseIfAny()
    {
        if (_pendingAutoPauseTeam == null)
        {
            return false;
        }

        CsTeam team = _pendingAutoPauseTeam.Value;
        _pendingAutoPauseTeam = null;

        if (_usedAutoPause.Contains(team))
        {
            return false;
        }

        _usedAutoPause.Add(team);

        PauseTechMatch(_localizer["timeout.auto_technical_pause", team.ToString()]);

        _autoPauseResumeTimer?.Kill();
        _autoPauseResumeTimer = TimerUtility.AddTimer(
            AutoTechnicalPauseSeconds,
            () => RequestResume(null, _localizer["timeout.auto_technical_pause_ended"])
        );

        StartAutoPauseCountdown();

        return true;
    }

    private void StartAutoPauseCountdown()
    {
        _autoPauseEndsAt = DateTime.UtcNow.AddSeconds(AutoTechnicalPauseSeconds);

        _autoPauseCountdownTimer?.Kill();
        _autoPauseCountdownTimer = TimerUtility.AddTimer(1, TickAutoPauseCountdown, TimerFlags.REPEAT);
        TickAutoPauseCountdown();
    }

    private void TickAutoPauseCountdown()
    {
        if (_autoPauseEndsAt == null)
        {
            StopAutoPauseCountdown();
            return;
        }

        int remainingSeconds = (int)Math.Ceiling((_autoPauseEndsAt.Value - DateTime.UtcNow).TotalSeconds);

        if (remainingSeconds <= 0)
        {
            StopAutoPauseCountdown();
            return;
        }

        int minutes = remainingSeconds / 60;
        int seconds = remainingSeconds % 60;

        _gameServer.Message(
            HudDestination.Alert,
            _localizer["timeout.auto_technical_pause_countdown", $"{minutes}:{seconds:D2}"]
        );
    }

    private void StopAutoPauseCountdown()
    {
        _autoPauseEndsAt = null;
        _autoPauseCountdownTimer?.Kill();
        _autoPauseCountdownTimer = null;
    }

    public void ResetAutoPause()
    {
        _usedAutoPause.Clear();
        _pendingAutoPauseTeam = null;
        _autoPauseResumeTimer?.Kill();
        _autoPauseResumeTimer = null;
        StopAutoPauseCountdown();
    }

    public void CallTacTimeout(CCSPlayerController? player)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        if (match == null || !match.IsInPlay() || _backUpManagement.IsResettingRound())
        {
            _gameServer.Message(
                HudDestination.Chat,
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

            // Read timeout count from CS2's native game rules
            var rules = MatchUtility.Rules();
            int timeoutsAvailable =
                player.Team == CsTeam.Terrorist
                    ? rules?.TerroristTimeOuts ?? 0
                    : rules?.CTTimeOuts ?? 0;

            if (timeoutsAvailable == 0)
            {
                _gameServer.Message(
                    HudDestination.Chat,
                    _localizer["timeout.no_timeouts_left"],
                    player
                );
                return;
            }

            // Let CS2 handle the timeout natively
            _gameServer.SendCommands([
                $"timeout_{(player.Team == CsTeam.Terrorist ? "terrorist" : "ct")}_start",
            ]);

            // After CS2 processes the timeout, sync state to DB
            Server.NextFrame(() =>
            {
                int remaining = timeoutsAvailable - 1;

                _gameServer.Message(
                    HudDestination.Alert,
                    _localizer[
                        "timeout.called_tactical",
                        player.PlayerName,
                        ChatColors.Red,
                        remaining
                    ]
                );

                PublishTimeoutState();
            });
        }
        else
        {
            _gameServer.Message(HudDestination.Alert, _localizer["timeout.called_admin"]);
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

    private bool IsAdminOrOrganizer(CCSPlayerController player, MatchData matchData)
    {
        if (player.Clan == "[admin]" || player.Clan == "[organizer]" || player.Clan == "admin" || player.Clan == "organizer")
        {
            return true;
        }

        MatchMember? lineupPlayer = MatchUtility.GetMemberFromLineup(
            matchData,
            player.SteamID.ToString(),
            player.PlayerName
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

    private void SendTimeoutAlreadyActiveMessage(CCSPlayerController? player)
    {
        if (player == null)
        {
            return;
        }

        _gameServer.Message(
            HudDestination.Chat,
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

        CsTeam lineup1Side = TeamUtility.GetLineupSide(
            matchData,
            currentMap,
            matchData.lineup_1_id,
            totalRoundsPlayed
        );

        if (lineup1Side == CsTeam.Terrorist)
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
