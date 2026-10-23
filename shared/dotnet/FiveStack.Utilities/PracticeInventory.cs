using System.Text.RegularExpressions;
using FiveStack.Entities.Practice;

namespace FiveStack.Utilities;

// DEAFCS: the skins plugin's settings on a practice server.
//
// A match server sets InventorySimulator up from the Competitive server cfg the
// match plugin execs (invsim_url pointing at the DEAFCS inventory service, its
// key, ...). A practice server runs no match plugin, so without these the
// plugin asks its public default service and players see their real Steam
// items. The panel now sends the same lines with the session; they are run
// here as console commands, the way an exec'd cfg runs them.
//
// Checked again on this side, whatever the panel sent: only invsim_ names,
// and no value that could end the command and start another.
public static class PracticeInventory
{
    private static readonly Regex Name = new(
        "^invsim_[a-z0-9_]{1,48}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    public const int MaxValueLength = 512;

    public static bool IsValid(PracticeServerSetting? setting)
    {
        if (setting?.name == null || setting.value == null)
        {
            return false;
        }

        if (!Name.IsMatch(setting.name) || setting.value.Length > MaxValueLength)
        {
            return false;
        }

        foreach (char c in setting.value)
        {
            if (c == '"' || c == ';' || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    // The console commands, in the order the cfg had them.
    public static List<string> Commands(IEnumerable<PracticeServerSetting>? settings)
    {
        return (settings ?? Enumerable.Empty<PracticeServerSetting>())
            .Where(IsValid)
            .Select(setting => $"{setting.name!.ToLowerInvariant()} \"{setting.value}\"")
            .ToList();
    }

    // For the log: which settings were applied, never their values (the
    // service key is one of them).
    public static List<string> Names(IEnumerable<PracticeServerSetting>? settings)
    {
        return (settings ?? Enumerable.Empty<PracticeServerSetting>())
            .Where(IsValid)
            .Select(setting => setting.name!.ToLowerInvariant())
            .ToList();
    }
}
