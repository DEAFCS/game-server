using FiveStack.Utilities;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace UtilityPractice;

// One player's view of a shared custom_hud_layout entity. Every write goes
// through a ...ForPlayer call, so a single entity serves the whole server with
// independent content.
public class HudSurface
{
    private readonly CCSCustomHudLayout _layout;
    private readonly HudLayoutSlots _slots;
    private readonly int _playerId;
    private readonly HudDiffCache _sent = new();

    public HudSurface(CCSCustomHudLayout layout, HudLayoutSlots slots, int playerId)
    {
        _layout = layout;
        _slots = slots;
        _playerId = playerId;
    }

    public CCSCustomHudLayout Layout => _layout;

    public void Set(string name, string value)
    {
        if (!_sent.Variable(name, value))
        {
            return;
        }

        _layout.SetDialogVariableStringForPlayer(_playerId, _slots.ElementFor(name), name, value);
    }

    public void Class(string elementId, string name, bool on)
    {
        if (!_sent.Class(elementId, name, on))
        {
            return;
        }

        _layout.SetHasClassForPlayer(
            _playerId,
            elementId,
            name,
            on
                ? EHudPanelClassStatus_t.k_eHudPanelClassStatus_HasClass
                : EHudPanelClassStatus_t.k_eHudPanelClassStatus_DoesNotHaveClass
        );
    }

    // A position Panorama can render: exactly one of prefix0..prefix(count-1)
    // is present. Clearing the previous one first, so the two are never both
    // set even for a frame.
    public void Pick(string elementId, string prefix, int index, int count)
    {
        int chosen = Math.Clamp(index, 0, count - 1);

        for (int step = 0; step < count; step++)
        {
            if (step != chosen)
            {
                Class(elementId, $"{prefix}{step}", false);
            }
        }

        Class(elementId, $"{prefix}{chosen}", true);
    }

    // Pick, for something that never stops moving: only the cell it left is
    // cleared, where Pick clears every cell it is not in. Never mix the two on
    // one element -- each keeps its own idea of where the thing is.
    public void Move(string elementId, string prefix, int index)
    {
        if (!_sent.Move(elementId, prefix, index, out int? left))
        {
            return;
        }

        if (left != null)
        {
            Class(elementId, $"{prefix}{left}", false);
        }

        Class(elementId, $"{prefix}{index}", true);
    }

    public void Show(bool on)
    {
        Class(_slots.RootId, HudSlots.Shown, on);
    }

    public void Input(bool on)
    {
        _layout.SetInputCaptureEnabledForPlayer(_playerId, on);
    }

    // The diff above assumes the client is holding what we last sent it. A
    // respawned entity or a reconnected player is holding nothing, so the cache
    // has to be dropped or every unchanged value is suppressed forever.
    public void Invalidate()
    {
        _sent.Invalidate();
    }

    // Whether the engine has dropped this slot's state under us: the cache says
    // the root was shown and the entity no longer says so.
    public bool EngineLost()
    {
        return HudHeal.Lost(_sent.Holds(_slots.RootId, HudSlots.Shown), EngineShown());
    }

    // Looked up by PlayerSlot rather than by vector index: nothing promises the
    // engine keeps one entry per slot in slot order, and reading another
    // player's entry would heal the wrong surface or none.
    private bool? EngineShown()
    {
        int panel = IndexOf(_layout.PanelIds, _slots.RootId);
        int name = IndexOf(_layout.ClassNames, HudSlots.Shown);

        if (panel < 0 || name < 0)
        {
            return null;
        }

        CUtlVector<CCSCustomHudLayoutState> states = _layout.PlayerLayoutStates;

        for (int i = 0; i < states.Count; i++)
        {
            CCSCustomHudLayoutState state = states[i];

            if (state.PlayerSlot != (uint)_playerId)
            {
                continue;
            }

            foreach (HUDPanelHasClass_t has in state.HasClasses)
            {
                if (has.PanelIdIndex == panel && has.ClassNameIndex == name)
                {
                    return has.ClassStatus == EHudPanelClassStatus_t.k_eHudPanelClassStatus_HasClass;
                }
            }

            return null;
        }

        return null;
    }

    private static int IndexOf(CUtlVector<CUtlString> names, string wanted)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] == wanted)
            {
                return i;
            }
        }

        return -1;
    }
}
