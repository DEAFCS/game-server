using FiveStack.Entities;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using static SwiftlyS2.Shared.Helper;

namespace FiveStack;

public partial class FiveStackPlugin
{
    [Command("r", registerRaw: false, permission: "")]
    [CommandAlias("ready", registerRaw: false)]
    [CommandAlias("unready", registerRaw: false)]
    [CommandAlias("ur", registerRaw: false)]
    public void OnReady(ICommandContext context)
    {
        MatchManager? match = _matchService.GetCurrentMatch();

        IPlayer? player = context.Sender;

        if (player == null || match == null || !match.IsWarmup())
        {
            return;
        }

        // .ready/.unready are tournament-only -- matchmaking auto-advances
        // once everyone's connected (WarmupShortenSystem), no manual
        // ready-up step needed there.
        MatchData? matchData = match.GetMatchData();
        if (matchData?.is_tournament_match != true)
        {
            _gameServer.Message(
                MessageType.Chat,
                _localizer["ready.tournament_only", ChatColors.Red],
                player
            );
            return;
        }

        match.readySystem.ToggleReady(player);
    }

    [Command("force_ready", registerRaw: true, permission: "")]
    public void OnForceStart(ICommandContext context)
    {
        if (context.IsSentByPlayer)
        {
            return;
        }

        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null)
        {
            return;
        }

        match.readySystem.Skip();
    }
}
