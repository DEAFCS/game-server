using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Convars;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace DeafCS;

[PluginMetadata(Id = "DeafCSTeamDamage", Version = "1.0.0",
    Name = "DeafCS Team Damage", Author = "DeafCS",
    Description = "Blocks teammate bullet and knife damage; preserves other CS2 damage.")]
public sealed class DeafCSTeamDamage(ISwiftlyCore core) : BasePlugin(core)
{
    // FiveStack sets this to false for tournament matches (see MatchManager.SetupMatch).
    private IConVar<bool>? _enabled;

    public override void Load(bool hotReload)
    {
        _enabled = Core.ConVar.CreateOrFind(
            "deafcs_teamdamage_enabled",
            "Block teammate bullet and knife damage. Disabled for tournament matches.",
            true);
        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
    }

    public override void Unload()
    {
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
    }

    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        if (_enabled is { Value: false })
            return;

        if (ctx.Params.Entity is not CCSPlayerPawn victim || !victim.IsValid)
            return;

        ref var info = ref ctx.Params.Info;
        if (!info.Attacker.IsValid ||
            info.Attacker.Value is not CCSPlayerPawn attacker || !attacker.IsValid)
            return;

        // Compare entity identity before teams: self-damage belongs to the engine.
        if (!DamagePolicy.ShouldBlock(attacker.Address == victim.Address,
                attacker.TeamNum, victim.TeamNum, (uint)info.DamageType))
            return;

        // Cancel before HP/armor changes without stopping other plugins' Pre listeners.
        ctx.SetHookResult(HookResult.CancelOriginal);
    }
}
