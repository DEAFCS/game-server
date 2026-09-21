using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Utilities;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FiveStack;

public partial class FiveStackPlugin
{
    public static readonly string ModelPathCtmSas = "agents\\models\\ctm_sas\\ctm_sas.vmdl";
    public static readonly string ModelPathTmPhoenix =
        "agents\\models\\tm_phoenix\\tm_phoenix.vmdl";

    [GameEventHandler]
    public HookResult OnEventPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        MatchManager? match = _matchService.GetCurrentMatch();

        if (@event.Userid == null || !@event.Userid.IsValid || @event.Userid.IsBot || match == null)
        {
            return HookResult.Continue;
        }

        CCSPlayerController spawnedPlayer = @event.Userid;

        // Same race as OnPlayerConnect (see PlayerConnected.cs): CS2 can sync
        // the client's own userinfo (raw Steam name, e.g. after the player
        // renamed themselves on Steam mid-match) and silently stomp our
        // DEAFCS-name override at any point during the round, not just right
        // after spawn -- reproduced live with a ~48s gap between the correct
        // name and the stomp, well past a single one-shot recheck. Re-assert
        // every few seconds for the rest of the round instead of once:
        // GetExpectedTeam() re-derives and re-applies the correct name as a
        // side effect, same call the connect path uses. Capped rather than
        // indefinite so a long-lived player doesn't accumulate one of these
        // per spawn forever.
        int nameEnforcementTicks = 0;
        Timer? nameEnforcementTimer = null;
        nameEnforcementTimer = TimerUtility.AddTimer(
            3.0f,
            () =>
            {
                nameEnforcementTicks++;
                if (!spawnedPlayer.IsValid || nameEnforcementTicks >= 40)
                {
                    nameEnforcementTimer?.Kill();
                    return;
                }

                match.GetExpectedTeam(spawnedPlayer);
            },
            TimerFlags.REPEAT
        );

        if ((match.GetMatchData()?.options.default_models ?? false) == false)
        {
            return HookResult.Continue;
        }

        CCSPlayerController player = spawnedPlayer;

        if (
            player == null
            || !player.IsValid
            || player.PlayerPawn == null
            || !player.PlayerPawn.IsValid
            || player.PlayerPawn.Value == null
            || !player.PlayerPawn.Value.IsValid
        )
        {
            return HookResult.Continue;
        }

        try
        {
            // TODO: Server crash if player connects, mp_swapteams and reconnect
            CsTeam team =
                player.PendingTeamNum != player.TeamNum
                    ? (CsTeam)player.PendingTeamNum
                    : (CsTeam)player.TeamNum;

            if ((CsTeam)player.TeamNum == CsTeam.CounterTerrorist)
            {
                SetModelNextServerFrame(player.PlayerPawn.Value, ModelPathCtmSas);
            }
            if ((CsTeam)player.TeamNum == CsTeam.Terrorist)
            {
                SetModelNextServerFrame(player.PlayerPawn.Value, ModelPathTmPhoenix);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Could not set player model: {0}", ex);
        }
        return HookResult.Continue;
    }

    public static void SetModelNextServerFrame(CCSPlayerPawn playerPawn, string model)
    {
        Server.NextFrame(() =>
        {
            playerPawn.SetModel(model);
        });
    }
}
