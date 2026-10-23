namespace FiveStack.Utilities;

// What one player's custom_hud_layout surface was last sent, so an unchanged
// value is not sent again every frame.
//
// The suppression is only right while the engine still holds what was sent.
// It does not always: the per-player state lives on the layout entity, keyed
// by player slot, and the engine drops a slot's state when that slot changes
// hands. A cache that outlives the engine state suppresses every unchanged
// write forever -- the HUD goes blank and the wheel comes up empty, and no
// amount of reconnecting fixes it. Hence Invalidate, and HudHeal deciding when.
public sealed class HudDiffCache
{
    private readonly Dictionary<string, string> _variables = new();
    private readonly Dictionary<(string, string), bool> _classes = new();
    private readonly Dictionary<(string, string), int> _moved = new();

    /// <summary>Records the value and says whether it has to be sent.</summary>
    public bool Variable(string name, string value)
    {
        if (_variables.TryGetValue(name, out string? showing) && showing == value)
        {
            return false;
        }

        _variables[name] = value;

        return true;
    }

    /// <summary>Records the class state and says whether it has to be sent.</summary>
    public bool Class(string elementId, string name, bool on)
    {
        (string, string) key = (elementId, name);

        if (_classes.TryGetValue(key, out bool was) && was == on)
        {
            return false;
        }

        _classes[key] = on;

        return true;
    }

    /// <summary>
    /// Where a moving element was before this index, or null when it has not
    /// moved (or was never placed). Records the new index either way.
    /// </summary>
    public bool Move(string elementId, string prefix, int index, out int? left)
    {
        (string, string) key = (elementId, prefix);
        left = null;

        if (_moved.TryGetValue(key, out int was))
        {
            if (was == index)
            {
                return false;
            }

            left = was;
        }

        _moved[key] = index;

        return true;
    }

    /// <summary>Whether the last thing sent for this class turned it on.</summary>
    public bool Holds(string elementId, string name)
    {
        return _classes.TryGetValue((elementId, name), out bool on) && on;
    }

    public bool IsEmpty => _variables.Count == 0 && _classes.Count == 0 && _moved.Count == 0;

    public void Invalidate()
    {
        _variables.Clear();
        _classes.Clear();
        _moved.Clear();
    }
}

// Whether a surface's engine-side state is gone, and whether to resend it now.
//
// Checked against the one class every shown surface sets (the root's "shown"):
// if the cache says it was sent and the engine no longer has it, the engine
// has dropped this slot's state and everything has to go again. Bounded, so a
// reading that is wrong for some other reason costs one resend every few
// seconds and a log line, never a resend every tick.
public sealed class HudHeal
{
    public const int MinSecondsBetween = 5;
    public const int MaxInARow = 6;

    private readonly Dictionary<(int, string), (int at, int inARow)> _healed = new();

    /// <param name="cachedShown">The cache says the surface was shown.</param>
    /// <param name="engineShown">
    /// What the engine holds for that class: true, false, or null when it has
    /// no state for the slot or the class at all.
    /// </param>
    public static bool Lost(bool cachedShown, bool? engineShown)
    {
        return cachedShown && engineShown != true;
    }

    /// <summary>
    /// Whether to resend this slot's layout now. A heal that keeps being
    /// needed is given up on after <see cref="MaxInARow" /> -- the engine
    /// reading is then the suspect, not the state -- until the slot changes
    /// hands (Forget) or a check finds it healthy again.
    /// </summary>
    public bool ShouldResend(int playerSlot, string layout, bool lost, int nowSeconds)
    {
        (int, string) key = (playerSlot, layout);

        if (!lost)
        {
            _healed.Remove(key);

            return false;
        }

        if (_healed.TryGetValue(key, out (int at, int inARow) last))
        {
            if (last.inARow >= MaxInARow || nowSeconds - last.at < MinSecondsBetween)
            {
                return false;
            }

            _healed[key] = (nowSeconds, last.inARow + 1);

            return true;
        }

        _healed[key] = (nowSeconds, 1);

        return true;
    }

    public bool GaveUp(int playerSlot, string layout)
    {
        return _healed.TryGetValue((playerSlot, layout), out (int at, int inARow) last) && last.inARow >= MaxInARow;
    }

    public void Forget(int playerSlot)
    {
        foreach ((int, string) key in _healed.Keys.Where(key => key.Item1 == playerSlot).ToList())
        {
            _healed.Remove(key);
        }
    }

    public void Reset()
    {
        _healed.Clear();
    }
}
