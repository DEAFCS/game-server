using FiveStack.Entities;
using FiveStack.Utilities;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace FiveStack;

public partial class FiveStackPlugin
{
    public HookResult OnPlayerChat(IPlayer? player, string message, bool teamOnly)
    {
        if (player == null || !player.IsValid)
        {
            return HookResult.Continue;
        }

        if (player.Controller.Team == Team.Spectator)
        {
            // Spectators aren't on a match lineup, so "team chat" from
            // them has no DEAFCS-side room to go to -- leave the
            // existing all-chat behavior untouched and simply don't
            // relay their say_team at all rather than mislabeling it.
            if (teamOnly)
            {
                return HookResult.Continue;
            }

            PublishChatEvent(player, message, teamOnly: false, lineupId: null);

            _gameServer.Message(
                MessageType.Chat,
                $" [red]{player.Controller.Clan}[white] {player.Name}: {message}"
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
            player.Name
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
            message,
            teamOnly,
            teamOnly ? member?.match_lineup_id.ToString() : null
        );

        return HookResult.Continue;
    }

    private void PublishChatEvent(
        IPlayer player,
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
