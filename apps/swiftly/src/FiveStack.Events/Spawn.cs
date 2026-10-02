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

    // Test: the engine can re-apply Valve's comms-abuse mute a moment after
    // spawn, once the penalty status arrives from the session/GC side -- a
    // single clear at spawn can lose that race. Re-check for a few seconds
    // after each spawn instead of clearing only once.
    private readonly Dictionary<ulong, CancellationTokenSource> _communicationAbuseMuteRecheckTimers =
        new();

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

        // Valve can apply its own "social penalty" comms mute (reports from
        // outside DEAFCS) independently of our admin mute system, which uses
        // player.VoiceFlags instead (see MatchManager.GetExpectedTeam) -- so
        // clearing this never bypasses a DEAFCS-issued mute. Clearing it on
        // connect (PlayerConnected.cs) is too early: the engine re-applies
        // it once the penalty status arrives from the session/GC side, so it
        // has to be cleared on every spawn instead. See DEAFCS/deafcs-web#115
        // -- sv_mute_players_with_social_penalties 0 does not work on our
        // current CS2 server setup.
        try
        {
            spawnedPlayer.Controller.HasCommunicationAbuseMute = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not clear Valve communication abuse mute");
        }

        ScheduleCommunicationAbuseMuteRecheck(spawnedPlayer);

        if ((match.GetMatchData()?.options.default_models ?? false) == false)
        {
            return HookResult.Continue;
        }

        IPlayer player = @event.UserIdPlayer;

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

    // Test: re-check HasCommunicationAbuseMute every 0.5s for 5s after spawn,
    // instead of clearing it only once. Replaces any still-running recheck
    // for this player (e.g. a quick re-spawn) rather than stacking timers.
    private void ScheduleCommunicationAbuseMuteRecheck(IPlayer spawnedPlayer)
    {
        ulong steamId = spawnedPlayer.SteamID;

        TimerUtility.Kill(_communicationAbuseMuteRecheckTimers.GetValueOrDefault(steamId));

        int rechecksRemaining = 10;
        CancellationTokenSource timer = null!;
        timer = TimerUtility.Repeat(
            0.5f,
            () =>
            {
                rechecksRemaining--;

                if (!spawnedPlayer.IsValid || spawnedPlayer.Controller == null)
                {
                    TimerUtility.Kill(timer);
                    _communicationAbuseMuteRecheckTimers.Remove(steamId);
                    return;
                }

                try
                {
                    if (spawnedPlayer.Controller.HasCommunicationAbuseMute)
                    {
                        _logger.LogInformation(
                            $"Communication abuse mute recheck: still set for {spawnedPlayer.Name}, clearing again"
                        );
                        spawnedPlayer.Controller.HasCommunicationAbuseMute = false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not clear Valve communication abuse mute (recheck)");
                }

                if (rechecksRemaining <= 0)
                {
                    TimerUtility.Kill(timer);
                    _communicationAbuseMuteRecheckTimers.Remove(steamId);
                }
            }
        );
        _communicationAbuseMuteRecheckTimers[steamId] = timer;
    }

    public static void SetModelNextServerFrame(CCSPlayerPawn playerPawn, string model)
    {
        MatchUtility.Core.Scheduler.NextTick(() =>
        {
            playerPawn.SetModel(model);
        });
    }
}
