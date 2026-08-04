using FiveStack.Utilities;
using Microsoft.Extensions.Logging;

namespace FiveStack;

// Per-match, per-player disconnect budget: 5 cumulative minutes offline
// before a player is flagged as a leaver, non-resetting across multiple
// disconnect/reconnect cycles within the same match (3 min used, reconnect,
// leave again -> only 2 min left, not a fresh 5). Replaces the old fixed
// 3-minute auto-surrender-vote timer (SurrenderSystem.SetupDisconnectTimer) --
// this only reports a "leaver-timeout" event to the API, which owns the
// actual ban/ELO escalation (mirrors the API's own match_player_disconnects
// audit table, kept independently so enforcement stays instant/local rather
// than depending on a network round-trip per disconnect).
public class DisconnectBudgetSystem
{
    private const int BudgetSeconds = 5 * 60;

    private readonly MatchEvents _matchEvents;
    private readonly MatchService _matchService;
    private readonly ILogger<DisconnectBudgetSystem> _logger;

    private readonly Dictionary<ulong, float> _usedSeconds = new Dictionary<ulong, float>();
    private readonly Dictionary<ulong, DateTime> _disconnectedAt = new Dictionary<ulong, DateTime>();
    private readonly Dictionary<ulong, CancellationTokenSource> _timers =
        new Dictionary<ulong, CancellationTokenSource>();

    public DisconnectBudgetSystem(
        ILogger<DisconnectBudgetSystem> logger,
        MatchEvents matchEvents,
        MatchService matchService
    )
    {
        _logger = logger;
        _matchEvents = matchEvents;
        _matchService = matchService;
    }

    public void OnPlayerDisconnected(ulong steamId)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        if (match == null || !match.IsInPlay())
        {
            return;
        }

        float used = _usedSeconds.GetValueOrDefault(steamId, 0f);
        float remaining = BudgetSeconds - used;

        _disconnectedAt[steamId] = DateTime.UtcNow;

        if (remaining <= 0)
        {
            HandleBudgetExhausted(steamId);
            return;
        }

        TimerUtility.Kill(_timers.GetValueOrDefault(steamId));
        _timers[steamId] = TimerUtility.AddTimer(
            remaining,
            () => HandleBudgetExhausted(steamId)
        );
    }

    public void OnPlayerReconnected(ulong steamId)
    {
        bool wasTracked = _timers.ContainsKey(steamId);

        if (_timers.TryGetValue(steamId, out CancellationTokenSource? timer))
        {
            TimerUtility.Kill(timer);
            _timers.Remove(steamId);
        }

        if (_disconnectedAt.TryGetValue(steamId, out DateTime disconnectedAt))
        {
            float elapsed = (float)(DateTime.UtcNow - disconnectedAt).TotalSeconds;
            _usedSeconds[steamId] = _usedSeconds.GetValueOrDefault(steamId, 0f) + elapsed;
            _disconnectedAt.Remove(steamId);
        }

        if (!wasTracked)
        {
            return;
        }

        MatchManager? match = _matchService.GetCurrentMatch();
        int currentPlayers = MatchUtility.PlayerCount();
        int expectedPlayers = match?.GetExpectedPlayerCount() ?? 10;

        if (match?.IsPaused() == true && currentPlayers == expectedPlayers)
        {
            match.ResumeMatch();
        }
    }

    private void HandleBudgetExhausted(ulong steamId)
    {
        _usedSeconds[steamId] = BudgetSeconds;
        _timers.Remove(steamId);
        _disconnectedAt.Remove(steamId);

        _logger.LogInformation($"Disconnect budget exhausted for {steamId}");

        _matchEvents.PublishGameEvent(
            "leaver-timeout",
            new Dictionary<string, object> { { "steam_id", steamId.ToString() } }
        );
    }

    public void Reset()
    {
        foreach (var timer in _timers.Values)
        {
            TimerUtility.Kill(timer);
        }
        _timers.Clear();
        _usedSeconds.Clear();
        _disconnectedAt.Clear();
    }
}
