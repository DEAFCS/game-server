using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

namespace FiveStack;

public partial class FiveStackPlugin
{
    // Read-only tracing to find where/when a player's DEAFCS name gets
    // forced back to their raw Steam name (reported around knife round
    // transitions). Nothing here writes a name back -- it only logs, so the
    // "[name-sync]" lines in the server log can be lined up chronologically
    // against a live repro to see who wrote the name last before it flips.

    // MatchManager.UpdatePlayerName logs its own writes directly (tagged
    // "[name-sync] DEAFCS wrote name ...") -- if a later "engine changed
    // name" line below shows a different value right after one of those,
    // the engine won.

    // The engine's own "a player's name changed" event, Post mode so this
    // cannot change anything. Fires regardless of which internal mechanism
    // CS2 used to change it (this catches it even if it doesn't go through
    // a convar SwiftlyS2 can hook).
    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerInfoNameChangeLog(EventPlayerInfo @event)
    {
        if (@event.UserIdPlayer == null || !@event.UserIdPlayer.IsValid || @event.Bot)
        {
            return HookResult.Continue;
        }

        _logger.LogInformation(
            $"[name-sync] engine player_info steamid={@event.SteamID} event-name=\"{@event.Name}\" current-name=\"{@event.UserIdPlayer.Name}\""
        );

        return HookResult.Continue;
    }

    // Independent SDK mechanism from the player_info game event -- kept
    // narrow to convars whose name suggests identity, since this fires for
    // every convar change a client sends and would otherwise be too noisy
    // to leave running permanently.
    public void OnConVarChangeNameLog(IOnConVarValueChanged @event)
    {
        if (!@event.ConVarName.Contains("name", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        IPlayer? player = _core.PlayerManager.GetPlayer(@event.PlayerId);

        if (player == null || player.IsFakeClient)
        {
            return;
        }

        _logger.LogInformation(
            $"[name-sync] OnConVarValueChanged steamid={player.SteamID} cvar=\"{@event.ConVarName}\" old=\"{@event.OldValue}\" new=\"{@event.NewValue}\" current-name=\"{player.Name}\""
        );
    }
}
