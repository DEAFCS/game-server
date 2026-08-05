using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Players;

namespace FiveStack;

public partial class FiveStackPlugin
{
    [Command("gg", registerRaw: false, permission: "")]
    public void OnForfeitVote(ICommandContext context)
    {
        IPlayer? player = context.Sender;

        if (player == null)
        {
            return;
        }

        _surrenderSystem.SetupForfeitVote(player);
    }
}
