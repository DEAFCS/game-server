using FiveStack.Entities;
using FiveStack.Enums;
using FiveStack.Utilities;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Translation;
using static SwiftlyS2.Shared.Helper;

namespace FiveStack;

public class KnifeSystem
{
    private readonly GameServer _gameServer;
    private readonly MatchEvents _matchEvents;
    private readonly MatchService _matchService;
    private readonly ILogger<KnifeSystem> _logger;
    private readonly EnvironmentService _environmentService;
    private readonly ILocalizer _localizer;
    private CancellationTokenSource? _knifeRoundTimer;
    private CancellationTokenSource? _knifeTimeoutTimer;

    private Team? _winningTeam;

    // Captain has this long to pick stay/switch before it auto-resolves to
    // "stay" (the standard CS convention for an undecided knife round).
    private const float KNIFE_DECISION_TIMEOUT_SECONDS = 60f;

    public KnifeSystem(
        ILogger<KnifeSystem> logger,
        GameServer gameServer,
        MatchEvents matchEvents,
        MatchService matchService,
        EnvironmentService environmentService,
        ILocalizer localizer
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

        MatchUtility.Core.Scheduler.NextTick(() =>
        {
            TimerUtility.AddTimer(
                5,
                () => _gameServer.Message(MessageType.Alert, _localizer["knife.start"])
            );
        });
    }

    public void SetWinningTeam(Team team)
    {
        MatchManager? match = _matchService.GetCurrentMatch();

        // Warmup only, not paused — captains have up to
        // KNIFE_DECISION_TIMEOUT_SECONDS to pick stay/switch, and freezing the
        // game solid for that whole window felt worse than letting players
        // move around in warmup while they wait.
        //
        // Order matters here: mp_warmup_start latches in whatever
        // mp_warmuptime is set to at that exact moment, so the cfg exec (which
        // resets mp_warmuptime to the normal, much longer pre-knife warmup
        // duration) and our override to the 60s decision window both have to
        // run *before* mp_warmup_start — not after — or CS2's own native
        // WARMUP HUD box shows leftover time from the wrong duration.
        List<string> commands = [];
        if (match != null)
        {
            commands.Add($"exec 5stack.{match.GetMatchData()?.options.type.ToLower()}.cfg");
        }
        commands.Add($"mp_warmuptime {(int)KNIFE_DECISION_TIMEOUT_SECONDS}");
        commands.Add("mp_warmup_start");

        _gameServer.SendCommands([.. commands]);

        var rules = MatchUtility.Rules();
        if (rules != null)
        {
            rules.RoundsPlayedThisPhase = 0;
        }

        _logger.LogInformation($"setting winning team: {team}");

        _winningTeam = team;

        _knifeRoundTimer = TimerUtility.Repeat(3, SetupKnifeMessage);
        _knifeTimeoutTimer = TimerUtility.AddTimer(
            KNIFE_DECISION_TIMEOUT_SECONDS,
            HandleDecisionTimeout
        );

        SetupKnifeMessage();

        string teamName = team == Team.T ? "Terrorist" : "CT";
        string shortTeamName = team == Team.T ? "T" : "CT";

        // Everyone should see who won the knife round.
        _gameServer.Message(MessageType.Chat, _localizer["knife.round_won", shortTeamName]);

        // "Captain is picking" only matters to the winning team — chat text
        // plus a center-text box, both sent to just their players (the API
        // has no per-team Alert, only per-player Chat/Center).
        foreach (IPlayer player in TeamPlayers(team))
        {
            player.SendChat(_localizer["knife.captain_picking", teamName].Colored());
            player.SendCenter(_localizer["knife.captain_picking", teamName].Colored());
        }
    }

    private static IEnumerable<IPlayer> TeamPlayers(Team team)
    {
        foreach (IPlayer player in MatchUtility.Players())
        {
            if (TeamUtility.TeamNumToTeam(player.Controller.TeamNum) == team)
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
        IPlayer? captain = match?.captainSystem?.GetTeamCaptain(_winningTeam.Value);

        if (captain == null)
        {
            _logger.LogCritical("missing team captain, auto selecting captains failed");
            return;
        }

        captain.SendCenter(
            _localizer[
                "knife.captain_prompt",
                ChatColors.Green,
                CommandUtility.PublicChatTrigger,
                ChatColors.Default,
                ChatColors.Green,
                CommandUtility.PublicChatTrigger
            ].Colored()
        );
    }

    public void Stay(IPlayer player)
    {
        _logger.LogInformation("Knife round staying");

        Team winningTeam = GetWinningTeam() ?? Team.None;
        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null || winningTeam == Team.None || !match.IsKnife())
        {
            return;
        }

        if (match.captainSystem.IsCaptain(player, winningTeam) == false)
        {
            _gameServer.Message(
                MessageType.Chat,
                _localizer["knife.not_captain", ChatColors.Red],
                player
            );
            return;
        }

        Reset();

        _gameServer.Message(
            MessageType.Alert,
            _localizer["knife.captain_picked_stay", ChatColors.Red, ChatColors.Default]
        );

        match.UpdateMapStatus(eMapStatus.Live);
    }

    public void Switch(IPlayer player)
    {
        Team winningTeam = GetWinningTeam() ?? Team.None;
        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null || winningTeam == Team.None || !match.IsKnife())
        {
            return;
        }

        if (match.captainSystem.IsCaptain(player, winningTeam) == false)
        {
            _gameServer.Message(
                MessageType.Chat,
                _localizer["knife.not_captain", ChatColors.Red],
                player
            );
            return;
        }

        Reset();

        _gameServer.Message(
            MessageType.Alert,
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

        _gameServer.Message(MessageType.Center, _localizer["knife.skipping"]);

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

        MatchUtility.Core.Scheduler.NextTick(() =>
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

    public Team? GetWinningTeam()
    {
        return _winningTeam;
    }

    // Only meaningful when the knife round timed out with nobody dead — CS2's
    // own round-end logic always credits CT in that case, which isn't a
    // meaningful outcome for a knife round. Decided by, in order: (1) whoever
    // has more players alive — a 2v1 shouldn't lose to a weaker but
    // undamaged 1v2, (2) whoever has more total health remaining, (3) a coin
    // flip if even that's tied, since neither side has any actual edge.
    public Team GetTimeoutWinner()
    {
        int aliveT = 0;
        int aliveCt = 0;
        int healthT = 0;
        int healthCt = 0;

        foreach (IPlayer player in MatchUtility.Players())
        {
            int health = player.PlayerPawn?.Health ?? 0;
            if (health <= 0)
            {
                continue;
            }

            Team team = TeamUtility.TeamNumToTeam(player.Controller.TeamNum);
            if (team == Team.T)
            {
                aliveT++;
                healthT += health;
            }
            else if (team == Team.CT)
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
            return aliveT > aliveCt ? Team.T : Team.CT;
        }

        if (healthT != healthCt)
        {
            return healthT > healthCt ? Team.T : Team.CT;
        }

        return Random.Shared.Next(2) == 0 ? Team.T : Team.CT;
    }

    private void HandleDecisionTimeout()
    {
        Team winningTeam = GetWinningTeam() ?? Team.None;
        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null || winningTeam == Team.None || !match.IsKnife())
        {
            return;
        }

        _logger.LogInformation("knife round decision timed out, auto-staying");

        Reset();

        _gameServer.Message(
            MessageType.Alert,
            _localizer["knife.timeout_stay", ChatColors.Red, ChatColors.Default]
        );

        match.UpdateMapStatus(eMapStatus.Live);
    }

    public void Reset()
    {
        TimerUtility.Kill(_knifeRoundTimer);
        TimerUtility.Kill(_knifeTimeoutTimer);
        _knifeRoundTimer = null;
        _knifeTimeoutTimer = null;
        _winningTeam = null;
    }
}
