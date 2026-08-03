using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Entities;
using FiveStack.Enums;
using FiveStack.Utilities;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FiveStack;

public class KnifeSystem
{
    private readonly GameServer _gameServer;
    private readonly MatchEvents _matchEvents;
    private readonly MatchService _matchService;
    private readonly ILogger<KnifeSystem> _logger;
    private readonly EnvironmentService _environmentService;
    private readonly IStringLocalizer _localizer;
    private Timer? _knifeRoundTimer;
    private Timer? _knifeTimeoutTimer;
    private Timer? _knifeCountdownTimer;

    private CsTeam? _winningTeam;
    private DateTime? _decisionDeadline;

    // Captain has this long to pick stay/switch before it auto-resolves to
    // "stay" (the standard CS convention for an undecided knife round).
    private const float KNIFE_DECISION_TIMEOUT_SECONDS = 60f;

    public KnifeSystem(
        ILogger<KnifeSystem> logger,
        GameServer gameServer,
        MatchEvents matchEvents,
        MatchService matchService,
        EnvironmentService environmentService,
        IStringLocalizer localizer
    )
    {
        _logger = logger;
        _matchService = matchService;
        _matchEvents = matchEvents;
        _gameServer = gameServer;
        _environmentService = environmentService;
        _localizer = localizer;
    }

    public void Start()
    {
        _gameServer.SendCommands(["exec 5stack.knife.cfg", "mp_warmup_end", "mp_restartgame 1"]);

        Server.NextFrame(() =>
        {
            TimerUtility.AddTimer(
                5,
                () => _gameServer.Message(HudDestination.Alert, _localizer["knife.start"])
            );
        });
    }

    public void SetWinningTeam(CsTeam team)
    {
        MatchManager? match = _matchService.GetCurrentMatch();

        // Warmup only, not frozen — captains have up to
        // KNIFE_DECISION_TIMEOUT_SECONDS to pick stay/switch, and freezing the
        // game solid for that whole window felt worse than letting players
        // move around in warmup while they wait.
        //
        // Order matters here: mp_warmup_start latches in whatever
        // mp_warmuptime is set to at that exact moment, so the cfg exec (which
        // resets mp_warmuptime to the normal, much longer pre-knife warmup
        // duration) and our overrides both have to run *before*
        // mp_warmup_start — not after — or CS2's own native WARMUP HUD box
        // shows the wrong duration. The match cfg also sets
        // mp_warmup_pausetimer 1 (so the real pre-match warmup never runs out
        // on its own) — that has to be turned back off here too, or the
        // countdown just sits frozen instead of ticking down.
        List<string> commands = [];
        if (match != null)
        {
            commands.Add($"exec 5stack.{match.GetMatchData()?.options.type.ToLower()}.cfg");
        }
        commands.Add($"mp_warmuptime {(int)KNIFE_DECISION_TIMEOUT_SECONDS}");
        commands.Add("mp_warmup_pausetimer 0");
        commands.Add("mp_warmup_start");

        _gameServer.SendCommands([.. commands]);

        var rules = MatchUtility.Rules();
        if (rules != null)
        {
            rules.RoundsPlayedThisPhase = 0;
        }

        _logger.LogInformation($"setting winning team: {team}");

        _winningTeam = team;

        _knifeRoundTimer = TimerUtility.AddTimer(3, SetupKnifeMessage, TimerFlags.REPEAT);
        _knifeTimeoutTimer = TimerUtility.AddTimer(
            KNIFE_DECISION_TIMEOUT_SECONDS,
            HandleDecisionTimeout
        );

        // CS2's own native WARMUP HUD box can't be trusted to show the right
        // time here (it keeps leftover time from the original, much longer
        // pre-knife warmup no matter what we set mp_warmuptime to) — so this
        // is our own, fully-controlled live countdown instead.
        _decisionDeadline = DateTime.UtcNow.AddSeconds(KNIFE_DECISION_TIMEOUT_SECONDS);
        _knifeCountdownTimer = TimerUtility.AddTimer(1, UpdateDecisionCountdown, TimerFlags.REPEAT);
        UpdateDecisionCountdown();

        SetupKnifeMessage();

        string teamName = team == CsTeam.Terrorist ? "Terrorist" : "CT";
        string shortTeamName = team == CsTeam.Terrorist ? "T" : "CT";

        // Everyone should see who won the knife round.
        _gameServer.Message(HudDestination.Chat, _localizer["knife.round_won", shortTeamName]);

        // "Captain is picking" only matters to the winning team — chat text
        // plus a center-text box, both sent to just their players (the API
        // has no per-team Alert, only per-player Chat/Center).
        foreach (CCSPlayerController player in TeamPlayers(team))
        {
            player.PrintToChat(_localizer["knife.captain_picking", teamName]);
            player.PrintToCenter(_localizer["knife.captain_picking", teamName]);
        }
    }

    private static IEnumerable<CCSPlayerController> TeamPlayers(CsTeam team)
    {
        foreach (CCSPlayerController player in MatchUtility.Players())
        {
            if (player.Team == team)
            {
                yield return player;
            }
        }
    }

    public void SetupKnifeMessage()
    {
        if (_winningTeam == null)
        {
            _logger.LogCritical("missing winning team");
            return;
        }

        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null)
        {
            _logger.LogCritical("missing match");
            return;
        }

        match?.captainSystem?.AutoSelectCaptains();
        CCSPlayerController? captain = match?.captainSystem?.GetTeamCaptain(_winningTeam.Value);

        if (captain == null)
        {
            _logger.LogCritical("missing team captain, auto selecting captains failed");
            return;
        }

        captain.PrintToCenter(
            _localizer[
                "knife.captain_prompt",
                ChatColors.Green,
                CommandUtility.PublicChatTrigger,
                ChatColors.Default,
                ChatColors.Green,
                CommandUtility.PublicChatTrigger
            ]
        );
    }

    public void Stay(CCSPlayerController player)
    {
        _logger.LogInformation("Knife round staying");

        CsTeam winningTeam = GetWinningTeam() ?? CsTeam.None;
        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null || winningTeam == CsTeam.None || !match.IsKnife())
        {
            return;
        }

        if (match.captainSystem.IsCaptain(player, winningTeam) == false)
        {
            _gameServer.Message(
                HudDestination.Chat,
                _localizer["knife.not_captain", ChatColors.Red],
                player
            );
            return;
        }

        Reset();

        _gameServer.Message(
            HudDestination.Alert,
            _localizer["knife.captain_picked_stay", ChatColors.Red, ChatColors.Default]
        );

        match.UpdateMapStatus(eMapStatus.Live);
    }

    public void Switch(CCSPlayerController player)
    {
        CsTeam winningTeam = GetWinningTeam() ?? CsTeam.None;
        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null || winningTeam == CsTeam.None || !match.IsKnife())
        {
            return;
        }

        if (match.captainSystem.IsCaptain(player, winningTeam) == false)
        {
            _gameServer.Message(
                HudDestination.Chat,
                _localizer["knife.not_captain", ChatColors.Red],
                player
            );
            return;
        }

        Reset();

        _gameServer.Message(
            HudDestination.Alert,
            _localizer["knife.captain_picked_swap", ChatColors.Red, ChatColors.Default]
        );

        if (_environmentService.IsOfflineMode())
        {
            match.UpdateMapStatus(eMapStatus.Live);
            _gameServer.SendCommands(["mp_swapteams; mp_restartgame 1"]);
            return;
        }

        _matchEvents.PublishGameEvent("switch", new Dictionary<string, object>());
    }

    public void Skip()
    {
        _gameServer.SendCommands(["mp_warmup_start;"]);

        MatchManager? match = _matchService.GetCurrentMatch();

        if (match != null)
        {
            _gameServer.SendCommands([
                $"exec 5stack.{match.GetMatchData()?.options.type.ToLower()}.cfg",
            ]);
        }

        var rules = MatchUtility.Rules();
        if (rules != null)
        {
            rules.RoundsPlayedThisPhase = 0;
        }

        Reset();

        if (match == null || !match.IsKnife())
        {
            return;
        }

        _gameServer.Message(HudDestination.Center, _localizer["knife.skipping"]);

        _gameServer.SendCommands(["mp_restartgame 1;mp_warmup_end"]);

        match.UpdateMapStatus(eMapStatus.Live);
    }

    public void ConfirmSwitch()
    {
        _logger.LogInformation("Knife round confirming switch");

        MatchManager? match = _matchService.GetCurrentMatch();
        MatchData? matchData = match?.GetMatchData();
        MatchMap? currentMap = match?.GetCurrentMap();

        if (match == null || matchData == null || currentMap == null)
        {
            return;
        }

        currentMap.lineup_1_side = currentMap.lineup_1_side == "CT" ? "TERRORIST" : "CT";
        currentMap.lineup_2_side = currentMap.lineup_2_side == "CT" ? "TERRORIST" : "CT";

        _gameServer.SendCommands(["mp_swapteams"]);

        Server.NextFrame(() =>
        {
            match.UpdateMapStatus(eMapStatus.Live);
        });
        TimerUtility.AddTimer(
            1.0f,
            () =>
            {
                _gameServer.SendCommands(["mp_restartgame 1"]);
            }
        );
    }

    public CsTeam? GetWinningTeam()
    {
        return _winningTeam;
    }

    // Only meaningful when the knife round timed out with nobody dead — CS2's
    // own round-end logic always credits CT in that case, which isn't a
    // meaningful outcome for a knife round. Decided by, in order: (1) whoever
    // has more players alive — a 2v1 shouldn't lose to a weaker but
    // undamaged 1v2, (2) whoever has more total health remaining, (3) a coin
    // flip if even that's tied, since neither side has any actual edge.
    public CsTeam GetTimeoutWinner()
    {
        int aliveT = 0;
        int aliveCt = 0;
        int healthT = 0;
        int healthCt = 0;

        foreach (CCSPlayerController player in MatchUtility.Players())
        {
            int health = player.PlayerPawn.Value?.Health ?? 0;
            if (health <= 0)
            {
                continue;
            }

            if (player.Team == CsTeam.Terrorist)
            {
                aliveT++;
                healthT += health;
            }
            else if (player.Team == CsTeam.CounterTerrorist)
            {
                aliveCt++;
                healthCt += health;
            }
        }

        _logger.LogInformation(
            $"knife round timed out — alive T={aliveT} CT={aliveCt}, health T={healthT} CT={healthCt}"
        );

        if (aliveT != aliveCt)
        {
            return aliveT > aliveCt ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        }

        if (healthT != healthCt)
        {
            return healthT > healthCt ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        }

        return Random.Shared.Next(2) == 0 ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
    }

    private void HandleDecisionTimeout()
    {
        CsTeam winningTeam = GetWinningTeam() ?? CsTeam.None;
        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null || winningTeam == CsTeam.None || !match.IsKnife())
        {
            return;
        }

        _logger.LogInformation("knife round decision timed out, auto-staying");

        Reset();

        _gameServer.Message(
            HudDestination.Alert,
            _localizer["knife.timeout_stay", ChatColors.Red, ChatColors.Default]
        );

        match.UpdateMapStatus(eMapStatus.Live);
    }

    private void UpdateDecisionCountdown()
    {
        if (_decisionDeadline == null)
        {
            return;
        }

        int remainingSeconds = (int)
            Math.Max(0, Math.Ceiling((_decisionDeadline.Value - DateTime.UtcNow).TotalSeconds));

        int minutes = remainingSeconds / 60;
        int seconds = remainingSeconds % 60;

        _gameServer.Message(
            HudDestination.Alert,
            _localizer["knife.decision_countdown", $"{minutes}:{seconds:D2}"]
        );
    }

    public void Reset()
    {
        _knifeRoundTimer?.Kill();
        _knifeTimeoutTimer?.Kill();
        _knifeCountdownTimer?.Kill();
        _knifeRoundTimer = null;
        _knifeTimeoutTimer = null;
        _knifeCountdownTimer = null;
        _winningTeam = null;
        _decisionDeadline = null;
    }
}
