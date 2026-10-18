using System.Globalization;
using System.Text;
using FiveStack.Entities.Practice;

namespace FiveStack.Utilities;

// Whether a callout walk read the map the server is on, or the one it left
// (DEAFCS).
//
// On 2026-10-18 a practice server changed Mirage -> Cache and its first walk
// after the change still found Mirage's env_cs_place entities: Mirage's 23
// places went to the panel as de_cache, and every board showed Cache's radar
// with Mirage's callouts. Two different maps never share every place name and
// box to the unit, so a walk on a new map that is identical to the last map
// reported is the old map's leftovers, and the reporter asks again a moment
// later instead of sending it.
public static class MapCalloutsGuard
{
    /// <summary>
    /// The places and boxes, order-independent, rounded to the unit so float
    /// noise between two reads of the same entities cannot tell them apart.
    /// </summary>
    public static string Signature(IReadOnlyList<MapCalloutPayload> callouts)
    {
        var parts = new List<string>(callouts.Count);

        foreach (MapCalloutPayload callout in callouts)
        {
            var boxes = callout
                .boxes.Select(box =>
                    string.Join(",", box.min.Concat(box.max).Select(Round))
                )
                .OrderBy(text => text, StringComparer.Ordinal);

            parts.Add(callout.name.ToLowerInvariant() + ":" + string.Join(";", boxes));
        }

        parts.Sort(StringComparer.Ordinal);

        return new StringBuilder().AppendJoin("|", parts).ToString();
    }

    /// <summary>
    /// True when this walk, on <paramref name="map" />, is the previous map's
    /// places: the map changed but the set did not. A reload of the same map
    /// is never leftovers.
    /// </summary>
    public static bool IsLeftover(
        string map,
        IReadOnlyList<MapCalloutPayload> callouts,
        string? previousMap,
        string? previousSignature
    )
    {
        if (
            callouts.Count == 0
            || string.IsNullOrEmpty(previousMap)
            || string.IsNullOrEmpty(previousSignature)
            || string.Equals(map, previousMap, StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        return Signature(callouts) == previousSignature;
    }

    private static string Round(float value)
    {
        return MathF.Round(value).ToString(CultureInfo.InvariantCulture);
    }
}
