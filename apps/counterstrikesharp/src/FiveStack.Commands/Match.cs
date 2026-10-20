using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Enums;
using FiveStack.Utilities;

namespace FiveStack;

public partial class FiveStackPlugin
{
    [ConsoleCommand("get_match", "Gets match information from api")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void get_match(CCSPlayerController? player, CommandInfo command)
    {
        _matchService.GetMatchFromApi();
    }

    // Console-only, triggered via RCON from CancelExpiredMatches so the
    // no-show cancellation announcement goes through the plugin's own
    // colored/localized message pipeline instead of a plain RCON `say`
    // (which shows as unstyled "Console: ..." text with no color support).
    [ConsoleCommand("announce_no_show_cancel", "Announces a no-show match cancellation")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnAnnounceNoShowCancel(CCSPlayerController? player, CommandInfo command)
    {
        _gameServer.Message(HudDestination.Chat, _localizer["auto_cancel.confirmed"]);
    }

    [ConsoleCommand("match_state", "Forces a match to update its current state")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void SetMatchState(CCSPlayerController? player, CommandInfo command)
    {
        MatchManager? match = _matchService.GetCurrentMatch();
        eMapStatus status = MatchUtility.MapStatusStringToEnum(command.ArgString);

        if (status == eMapStatus.Warmup)
        {
            string? refusal = RoundRestoreGateUtility.WarmupRefusal(
                match?.GetMatchData() != null,
                match?.GetCurrentMapStatus() ?? eMapStatus.Unknown
            );
            if (refusal != null)
            {
                command.ReplyToCommand(
                    $"{RoundRestoreGateUtility.MatchStateRefusedPrefix} {refusal}"
                );
                return;
            }
        }

        match?.UpdateMapStatus(status);
    }
}
