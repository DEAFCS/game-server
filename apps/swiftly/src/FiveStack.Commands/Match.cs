using FiveStack.Enums;
using FiveStack.Utilities;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace FiveStack;

public partial class FiveStackPlugin
{
    [Command("get_match", registerRaw: true, permission: "")]
    public void get_match(ICommandContext context)
    {
        if (context.IsSentByPlayer)
        {
            return;
        }

        _matchService.GetMatchFromApi();
    }

    // Console-only, triggered via RCON from CancelExpiredMatches so the
    // no-show cancellation announcement goes through the plugin's own
    // colored/localized message pipeline instead of a plain RCON `say`
    // (which shows as unstyled "Console: ..." text with no color support).
    [Command("announce_no_show_cancel", registerRaw: true, permission: "")]
    public void OnAnnounceNoShowCancel(ICommandContext context)
    {
        if (context.IsSentByPlayer)
        {
            return;
        }

        _gameServer.Message(MessageType.Chat, _localizer["auto_cancel.confirmed"]);
    }

    [Command("match_state", registerRaw: true, permission: "")]
    public void SetMatchState(ICommandContext context)
    {
        if (context.IsSentByPlayer)
        {
            return;
        }

        MatchManager? match = _matchService.GetCurrentMatch();
        eMapStatus status = MatchUtility.MapStatusStringToEnum(string.Join(" ", context.Args));

        if (status == eMapStatus.Warmup)
        {
            string? refusal = RoundRestoreGateUtility.WarmupRefusal(
                match?.GetMatchData() != null,
                match?.GetCurrentMapStatus() ?? eMapStatus.Unknown
            );
            if (refusal != null)
            {
                context.Reply($"{RoundRestoreGateUtility.MatchStateRefusedPrefix} {refusal}");
                return;
            }
        }

        match?.UpdateMapStatus(status);
    }
}
