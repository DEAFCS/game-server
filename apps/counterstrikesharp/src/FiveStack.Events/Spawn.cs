using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using FiveStack.Utilities;
using Microsoft.Extensions.Logging;

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
        // DEAFCS-name override at any point, not just on initial connect.
        // Every round's spawn is a convenient, frequent point to win that
        // race back -- GetExpectedTeam() re-derives and re-applies the
        // correct name as a side effect, same call the connect path uses.
        TimerUtility.AddTimer(
            0.5f,
            () =>
            {
                if (!spawnedPlayer.IsValid)
                {
                    return;
                }

                match.GetExpectedTeam(spawnedPlayer);
            }
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
