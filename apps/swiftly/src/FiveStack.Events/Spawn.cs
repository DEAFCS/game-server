using System.Threading;
using FiveStack.Utilities;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace FiveStack;

public partial class FiveStackPlugin
{
    public static readonly string ModelPathCtmSas = "agents\\models\\ctm_sas\\ctm_sas.vmdl";
    public static readonly string ModelPathTmPhoenix =
        "agents\\models\\tm_phoenix\\tm_phoenix.vmdl";

    [GameEventHandler(HookMode.Post)]
    public HookResult OnEventPlayerSpawn(EventPlayerSpawn @event)
    {
        MatchManager? match = _matchService.GetCurrentMatch();

        if (
            @event.UserIdPlayer == null
            || !@event.UserIdPlayer.IsValid
            || @event.UserIdPlayer.IsFakeClient
            || match == null
        )
        {
            return HookResult.Continue;
        }

        IPlayer spawnedPlayer = @event.UserIdPlayer;

        // Same race as OnPlayerConnect (see PlayerConnected.cs): the
        // client's own userinfo (raw Steam name, e.g. after the player
        // renamed themselves on Steam mid-match) can sync and silently
        // stomp our DEAFCS-name override at any point during the round, not
        // just right after spawn -- reproduced live with a ~48s gap between
        // the correct name and the stomp, well past a single one-shot
        // recheck. Re-assert every few seconds for the rest of the round
        // instead of once: GetExpectedTeam() re-derives and re-applies the
        // correct name as a side effect, same call the connect path uses.
        // Capped rather than indefinite so a long-lived player doesn't
        // accumulate one of these per spawn forever.
        int nameEnforcementTicks = 0;
        CancellationTokenSource? nameEnforcementTimer = null;
        nameEnforcementTimer = TimerUtility.Repeat(
            3.0f,
            () =>
            {
                nameEnforcementTicks++;
                if (!spawnedPlayer.IsValid || nameEnforcementTicks >= 40)
                {
                    TimerUtility.Kill(nameEnforcementTimer);
                    return;
                }

                match.GetExpectedTeam(spawnedPlayer);
            }
        );

        if ((match.GetMatchData()?.options.default_models ?? false) == false)
        {
            return HookResult.Continue;
        }

        IPlayer player = spawnedPlayer;

        if (
            player == null
            || !player.IsValid
            || player.PlayerPawn == null
            || !player.PlayerPawn.IsValid
        )
        {
            return HookResult.Continue;
        }

        try
        {
            Team team =
                player.Controller.PendingTeamNum != player.Controller.TeamNum
                    ? (Team)player.Controller.PendingTeamNum
                    : (Team)player.Controller.TeamNum;

            if ((Team)player.Controller.TeamNum == Team.CT)
            {
                SetModelNextServerFrame(player.PlayerPawn, ModelPathCtmSas);
            }
            if ((Team)player.Controller.TeamNum == Team.T)
            {
                SetModelNextServerFrame(player.PlayerPawn, ModelPathTmPhoenix);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not set player model");
        }
        return HookResult.Continue;
    }

    public static void SetModelNextServerFrame(CCSPlayerPawn playerPawn, string model)
    {
        MatchUtility.Core.Scheduler.NextTick(() =>
        {
            playerPawn.SetModel(model);
        });
    }
}
