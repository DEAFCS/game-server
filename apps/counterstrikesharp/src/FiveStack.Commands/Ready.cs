using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Entities;

namespace FiveStack;

public partial class FiveStackPlugin
{
    [
        ConsoleCommand("css_r", "Toggles the player as ready"),
        ConsoleCommand("css_ready", "Toggles the player as ready"),
        ConsoleCommand("css_unready", "Toggles the player as ready"),
        ConsoleCommand("css_ur", "Toggles the player as ready")
    ]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnReady(CCSPlayerController? player, CommandInfo? command)
    {
        MatchManager? match = _matchService.GetCurrentMatch();

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
                HudDestination.Chat,
                _localizer["ready.tournament_only", ChatColors.Red],
                player
            );
            return;
        }

        match.readySystem.ToggleReady(player);
    }

    [ConsoleCommand("force_ready", "Forces the match to start")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnForceStart(CCSPlayerController? player, CommandInfo? command)
    {
        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null)
        {
            return;
        }

        match.readySystem.Skip();
    }
}
