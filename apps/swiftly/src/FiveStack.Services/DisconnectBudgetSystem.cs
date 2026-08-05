using FiveStack.Utilities;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Translation;
using static SwiftlyS2.Shared.Helper;

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

    // Minutes-remaining milestones announced in global chat while someone is
    // disconnected, e.g. "5 min left" then "3 min left" then "1 min left" --
    // not a per-second ticker, just enough visibility to know it's running.
    private static readonly int[] MilestoneSeconds = { 5 * 60, 3 * 60, 60 };

    private readonly MatchEvents _matchEvents;
    private readonly MatchService _matchService;
    private readonly GameServer _gameServer;
    private readonly ILocalizer _localizer;
    private readonly ILogger<DisconnectBudgetSystem> _logger;

    private readonly Dictionary<ulong, float> _usedSeconds = new Dictionary<ulong, float>();
    private readonly Dictionary<ulong, DateTime> _disconnectedAt = new Dictionary<ulong, DateTime>();
    private readonly Dictionary<ulong, string> _playerNames = new Dictionary<ulong, string>();
    private readonly Dictionary<ulong, CancellationTokenSource> _timers =
        new Dictionary<ulong, CancellationTokenSource>();
    private readonly Dictionary<ulong, List<CancellationTokenSource>> _milestoneTimers =
        new Dictionary<ulong, List<CancellationTokenSource>>();

    // Milestone announcements that tried to fire mid-round (not freeze
    // period) get queued here instead of dropped, and flushed the moment
    // freeze period starts (see FlushPendingAnnouncements, called from
    // OnRoundStart) -- players asked not to see this mid-round, but it must
    // still reach them, just delayed to the next round instead of lost.
    private readonly List<string> _pendingAnnouncements = new List<string>();

    public DisconnectBudgetSystem(
        ILogger<DisconnectBudgetSystem> logger,
        MatchEvents matchEvents,
        MatchService matchService,
        GameServer gameServer,
        ILocalizer localizer
    )
    {
        _logger = logger;
        _matchEvents = matchEvents;
        _matchService = matchService;
        _gameServer = gameServer;
        _localizer = localizer;
    }

    public void OnPlayerDisconnected(ulong steamId, string playerName)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        if (match == null || !match.IsInPlayOrKnife())
        {
            return;
        }

        float used = _usedSeconds.GetValueOrDefault(steamId, 0f);
        float remaining = BudgetSeconds - used;

        _disconnectedAt[steamId] = DateTime.UtcNow;
        _playerNames[steamId] = playerName;

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

        ScheduleMilestones(steamId, playerName, remaining);
    }

    // Used by TeamEmptyForfeitSystem to tell "team is empty but someone could
    // still reconnect" apart from "team is empty and everyone on it is
    // already permanently banned" -- no reason to run a fresh forfeit
    // countdown in the latter case.
    public bool IsBudgetExhausted(ulong steamId)
    {
        return _usedSeconds.GetValueOrDefault(steamId, 0f) >= BudgetSeconds;
    }

    public void OnPlayerReconnected(ulong steamId)
    {
        bool wasTracked = _timers.ContainsKey(steamId);

        if (_timers.TryGetValue(steamId, out CancellationTokenSource? timer))
        {
            TimerUtility.Kill(timer);
            _timers.Remove(steamId);
        }

        KillMilestoneTimers(steamId);

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

    private void ScheduleMilestones(ulong steamId, string playerName, float remaining)
    {
        List<CancellationTokenSource> timers = new List<CancellationTokenSource>();

        foreach (int milestone in MilestoneSeconds)
        {
            if (milestone > remaining)
            {
                continue;
            }

            float delay = remaining - milestone;

            if (delay <= 0)
            {
                AnnounceMilestone(playerName, milestone);
                continue;
            }

            timers.Add(
                TimerUtility.AddTimer(delay, () => AnnounceMilestone(playerName, milestone))
            );
        }

        _milestoneTimers[steamId] = timers;
    }

    private void AnnounceMilestone(string playerName, int secondsRemaining)
    {
        int minutesRemaining = secondsRemaining / 60;
        SendOrQueue(
            $"{ChatColors.Orange}[DEAFCS] {ChatColors.Red}"
                + _localizer["leaver.reconnect_warning", playerName, minutesRemaining]
        );
    }

    // Players asked not to see these mid-round, only right before a round
    // starts -- but a message that's due mid-round must still reach them, so
    // it's queued instead of dropped, and sent as soon as freeze period
    // starts (see FlushPendingAnnouncements).
    private void SendOrQueue(string message)
    {
        if (MatchUtility.Rules()?.FreezePeriod ?? false)
        {
            _gameServer.Message(MessageType.Chat, message);
            return;
        }

        _pendingAnnouncements.Add(message);
    }

    // Called from OnRoundStart, right as freeze period begins -- sends any
    // announcement that tried to fire mid-round and got queued instead.
    public void FlushPendingAnnouncements()
    {
        if (_pendingAnnouncements.Count == 0)
        {
            return;
        }

        foreach (string message in _pendingAnnouncements)
        {
            _gameServer.Message(MessageType.Chat, message);
        }

        _pendingAnnouncements.Clear();
    }

    private void KillMilestoneTimers(ulong steamId)
    {
        if (_milestoneTimers.TryGetValue(steamId, out List<CancellationTokenSource>? timers))
        {
            foreach (var timer in timers)
            {
                TimerUtility.Kill(timer);
            }
            _milestoneTimers.Remove(steamId);
        }
    }

    private void HandleBudgetExhausted(ulong steamId)
    {
        _usedSeconds[steamId] = BudgetSeconds;
        _timers.Remove(steamId);
        _disconnectedAt.Remove(steamId);
        KillMilestoneTimers(steamId);

        string playerName = _playerNames.GetValueOrDefault(steamId, steamId.ToString());

        _logger.LogInformation($"Disconnect budget exhausted for {steamId}");

        SendOrQueue(
            $"{ChatColors.Orange}[DEAFCS] {ChatColors.Red}" + _localizer["leaver.banned", playerName]
        );

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
        foreach (var steamId in _milestoneTimers.Keys.ToList())
        {
            KillMilestoneTimers(steamId);
        }
        _timers.Clear();
        _usedSeconds.Clear();
        _disconnectedAt.Clear();
        _playerNames.Clear();
        _pendingAnnouncements.Clear();
    }
}
