namespace FiveStack.Utilities;

// Which bots have to go when more joined than were asked for.
//
// One "Place A Bot" press has been seen to bring in three -- one on the side
// asked for and two more on the other -- and the next press three more, so a
// practice server meant to hold one frozen target filled with bots that walk,
// shoot back and take player slots. Whatever the engine's quota does on top
// of bot_add, the placed count is the truth: anything past it is kicked, the
// newest first, and the quota is pinned back to it so the engine does not
// bring them straight back.
public static class PracticeBotRoster
{
    /// <summary>
    /// The player slots to kick, newest first, given the bots in the game on
    /// a team (SourceTV and other unassigned fake clients excluded) and how
    /// many have been placed.
    /// </summary>
    public static IReadOnlyList<int> Surplus(IEnumerable<int> botSlotsOnTeams, int placed)
    {
        List<int> slots = botSlotsOnTeams.Distinct().OrderBy(slot => slot).ToList();
        int extra = slots.Count - Math.Max(0, placed);

        if (extra <= 0)
        {
            return Array.Empty<int>();
        }

        return slots.Skip(slots.Count - extra).Reverse().ToList();
    }
}
