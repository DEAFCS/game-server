using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Entities;
using FiveStack.Utilities;

namespace FiveStack;

public partial class FiveStackPlugin
{
    // "say" (all chat) and "say_team" (team-only chat) are two separate
    // client commands with no shared callback signature carrying which
    // one fired -- these are the two listener entry points registered
    // in Load(), each just fixing teamOnly for its own command and
    // handing off to the actual logic below.
    public HookResult OnPlayerChat(CCSPlayerController? player, CommandInfo info)
    {
        return HandlePlayerChat(player, info, teamOnly: false);
    }

    public HookResult OnPlayerTeamChat(CCSPlayerController? player, CommandInfo info)
    {
        return HandlePlayerChat(player, info, teamOnly: true);
    }

    private HookResult HandlePlayerChat(
        CCSPlayerController? player,
        CommandInfo info,
        bool teamOnly
    )
    {
        if (player == null || !player.IsValid)
        {
            return HookResult.Continue;
        }

        if (player.Team == CsTeam.Spectator)
        {
            // Spectators aren't on a match lineup, so "team chat" from
            // them has no DEAFCS-side room to go to -- leave the
            // existing all-chat behavior untouched and simply don't
            // relay their say_team at all rather than mislabeling it.
            if (teamOnly)
            {
                return HookResult.Continue;
            }

            PublishChatEvent(player, info.ArgString.Trim('"'), teamOnly: false, lineupId: null);

            _gameServer.Message(
                HudDestination.Chat,
                $" {ChatColors.Red}{player.Clan}{ChatColors.White} {player.PlayerName}: {info.ArgString.Trim('"')}"
            );

            return HookResult.Stop;
        }

        MatchManager? match = _matchService.GetCurrentMatch();

        if (match == null)
        {
            return HookResult.Continue;
        }

        MatchData? matchData = match.GetMatchData();

        if (matchData == null)
        {
            return HookResult.Continue;
        }

        MatchMember? member = MatchUtility.GetMemberFromLineup(
            matchData,
            player.SteamID.ToString(),
            player.PlayerName
        );

        if (member != null)
        {
            if (member.is_gagged)
            {
                return HookResult.Stop;
            }
        }

        PublishChatEvent(
            player,
            info.ArgString.Trim('"'),
            teamOnly,
            teamOnly ? member?.match_lineup_id.ToString() : null
        );

        return HookResult.Continue;
    }

    private void PublishChatEvent(
        CCSPlayerController player,
        string message,
        bool teamOnly,
        string? lineupId
    )
    {
        Dictionary<string, object> data = new()
        {
            { "player", player.SteamID.ToString() },
            { "message", message },
            { "teamOnly", teamOnly },
        };
        if (lineupId != null)
        {
            data["lineupId"] = lineupId;
        }
        _matchEvents.PublishGameEvent("chat", data);
    }
}
