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
    private readonly HashSet<CsTeam> _teamsPendingResume = new();
    private bool _requiresTeamResumeForCurrentPause;

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

        // Technical timeout (.tech/.pause) is tournament/draft-only --
        // matchmaking can still call a tactical timeout (.tac), just not
        // this one. Console/RCON (player == null) is left alone; this only
        // gates the player-typed chat command.
        if (player != null && !IsTechAllowed(match.GetMatchData()))
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

        // A completely empty team is TeamEmptyForfeitSystem's exclusive
        // territory -- calling a technical pause on top of it would drop
        // its mp_pause_match freeze (CS2 won't hold that while a native
        // timeout is running) without cancelling its forfeit countdown,
        // leaving the match playable again against the still-empty team
        // once the technical pause ends.
        if (match.teamEmptyForfeitSystem.IsActivelyPausing)
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

    // .tech/.pause and .resume are available in tournament and draft
    // matches, not MM.
    private static bool IsTechAllowed(MatchData? matchData)
    {
        return matchData != null
            && (matchData.is_tournament_match || matchData.is_draft_match);
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

        // .resume is tournament/draft-only, same as .tech/.pause --
        // matchmaking pauses (auto technical pause, waiting-for-players,
        // team-empty) all resolve on their own (full roster reconnects,
        // budget/timer elapses); there's no legitimate manual-resume path
        // outside a tournament/draft's admin-controlled technical pause.
        if (player != null && !IsTechAllowed(matchData))
        {
            _gameServer.Message(
                HudDestination.Chat,
                _localizer["timeout.resume_tournament_only", ChatColors.Red],
                player
            );
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

        // Same reasoning as RequestPause -- a native tactical timeout on
        // top of TeamEmptyForfeitSystem's own freeze would drop it without
        // cancelling the forfeit countdown, letting the match keep playing
        // against the still-empty team once the timeout ends.
        if (match.teamEmptyForfeitSystem.IsActivelyPausing)
        {
            SendTimeoutAlreadyActiveMessage(player);
            return;
        }

        // IsTimeoutActive() only sees CS2's own native tactical-timeout
        // flags, not our own mp_pause_match-based technical pause (.tech,
        // tournament-only) -- match.IsPaused() reflects that instead.
        // Without this, .tac/.timeout slipped straight through while a
        // .tech pause was already active in a tournament match (MM never
        // hit this, since .tech doesn't exist there -- the only way MM
        // gets paused is TeamEmptyForfeitSystem, already checked above).
        if (match.IsPaused())
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
