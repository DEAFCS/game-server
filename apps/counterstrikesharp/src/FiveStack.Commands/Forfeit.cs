using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;

namespace FiveStack;

public partial class FiveStackPlugin
{
    [ConsoleCommand("css_gg", "Votes to forfeit when your team is short a player")]
    public void OnForfeitVote(CCSPlayerController? player, CommandInfo? command)
    {
        if (player == null)
        {
            return;
        }

        _surrenderSystem.SetupForfeitVote(player);
    }
}
