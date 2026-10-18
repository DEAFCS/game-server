using FiveStack.Utilities;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.ProtobufDefinitions;
using SwiftlyS2.Shared.SchemaDefinitions;
using static SwiftlyS2.Shared.Helper;

namespace UtilityPractice;

// DEAFCS: AFK protection (PracticeAfk). A player connected but doing nothing
// keeps a practice server from everyone else, and the reaper only ends a
// session once it is empty. Warned at 25 minutes, again at 29, kicked at 30;
// the empty-server reaper then ends the session as it always has.
//
// What counts as doing something: any button held (moving, shooting, jumping,
// crouching, use, reload -- read every tick in UseWatch), looking around or
// moving between one-second samples, a HUD key or click, a grenade thrown.
// Standing on a lineup adjusting the aim is looking around, so practising
// still never gets anyone kicked.
public partial class UtilityPracticePlugin
{
    private readonly PracticeAfk _afk = new();

    private readonly Dictionary<ulong, (float Yaw, float Pitch, float X, float Y, float Z)> _afkSeen = new();

    private static int AfkNow => (int)(Environment.TickCount64 / 1000);

    // Every tick from UseWatch: a held button is a person at the keyboard.
    private void AfkInput(ulong steamId, uint buttons)
    {
        if (buttons != 0)
        {
            _afk.Active(steamId, AfkNow);
        }
    }

    private void AfkActive(ulong steamId)
    {
        _afk.Active(steamId, AfkNow);
    }

    private void AfkForget(ulong steamId)
    {
        _afk.Forget(steamId);
        _afkSeen.Remove(steamId);
    }

    // Once a second, from OnSecond.
    private void AfkWatch()
    {
        // A render or a discovery has no player to protect the server from.
        if (_config.RenderMode)
        {
            return;
        }

        int now = AfkNow;

        foreach (IPlayer player in Core.PlayerManager.GetAllPlayers())
        {
            if (player == null || !player.IsValid || player.IsFakeClient)
            {
                continue;
            }

            ulong steamId = player.SteamID;
            CCSPlayerPawn? pawn = player.PlayerPawn;

            if (pawn != null && pawn.IsValid)
            {
                QAngle eyes = pawn.EyeAngles;
                Vector at = pawn.AbsOrigin ?? new Vector(0, 0, 0);

                if (_afkSeen.TryGetValue(steamId, out var seen)
                    && (
                        PracticeAfk.Looked(seen.Yaw, seen.Pitch, eyes.Y, eyes.X)
                        || PracticeAfk.Moved(seen.X, seen.Y, seen.Z, at.X, at.Y, at.Z)
                    ))
                {
                    _afk.Active(steamId, now);
                }

                _afkSeen[steamId] = (eyes.Y, eyes.X, at.X, at.Y, at.Z);
            }

            switch (_afk.Check(steamId, now))
            {
                case PracticeAfk.Step.Warn:
                    AfkWarn(player, "You have been inactive for 25 minutes. Move or press any key, or you will be kicked in 5 minutes.");
                    break;
                case PracticeAfk.Step.FinalWarn:
                    AfkWarn(player, "Final warning: you will be kicked for inactivity in 1 minute.");
                    break;
                case PracticeAfk.Step.Kick:
                    AfkKick(player);
                    break;
            }
        }
    }

    // On screen three ways, so it is seen whatever the player is looking at:
    // the HUD toast, the centre of the screen and chat.
    private void AfkWarn(IPlayer player, string text)
    {
        try
        {
            Toast(player, text);
            player.SendCenter(text);
            Tell(player.SteamID, $" {ChatColors.Red}{text}");
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "unable to warn {steam} about inactivity", player.SteamID);
        }
    }

    private void AfkKick(IPlayer player)
    {
        ulong steamId = player.SteamID;

        _logger.LogInformation("kicking {steam}: inactive for {minutes} minutes", steamId, PracticeAfk.KickSeconds / 60);
        AfkForget(steamId);

        try
        {
            player.Kick(
                "Kicked for 30 minutes of inactivity in Pracc",
                ENetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED_IDLE
            );
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "unable to kick {steam} for inactivity", steamId);
        }
    }
}
