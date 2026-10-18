using System;
using System.Collections.Generic;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// The words and order of the MODS list (both the game-widget page and the full view draw it from these): which
/// mod comes first, the small badge and the detail line under each name, and the groups the list is split into.
/// Pure logic, unit tested.
/// </summary>
internal static class ModListModel
{
    /// <summary>The badge of plugins of other creators.</summary>
    internal const string OtherCreator = "OTHER CREATOR";

    /// <summary>The list's groups, top to bottom.</summary>
    internal enum Group
    {
        /// <summary>The Mod Manager and our mods.</summary>
        Ours,
        /// <summary>Other creators' plugins (BepInEx plugins that aren't ours).</summary>
        OtherCreators,
        /// <summary>Mods listed in the release that can be downloaded.</summary>
        Available,
    }

    /// <summary>The heading over a group (none over our own mods, they come first).</summary>
    internal static string Heading(Group g) => g switch
    {
        Group.OtherCreators => "Other creators",
        Group.Available => "Mods",
        _ => null,
    };

    /// <summary>Core first, then by name (the id when there is no name), ignoring case.</summary>
    internal static int Compare(bool aCore, string aName, bool bCore, string bName)
    {
        if (aCore != bCore) return aCore ? -1 : 1;
        return string.Compare(aName ?? "", bName ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sorts <paramref name="items"/> in list order.</summary>
    internal static void Sort<T>(List<T> items, Func<T, bool> isCore, Func<T, string> name) =>
        items.Sort((a, b) => Compare(isCore(a), name(a), isCore(b), name(b)));

    /// <summary>
    /// The detail line under a mod's name: its version, then whether it failed to start or lost features after a
    /// game update, then the update mark (e.g. "update available").
    /// </summary>
    internal static string Detail(string version, bool failedToStart, bool partlyUnavailable, string updateMark)
    {
        string detail = string.IsNullOrEmpty(version) ? "" : "v" + version;
        if (failedToStart) detail = Add(detail, "<color=#E5534B>failed to start</color>");
        else if (partlyUnavailable) detail = Add(detail, "<color=#F2B33D>partly unavailable</color>");
        if (!string.IsNullOrEmpty(updateMark)) detail = Add(detail, updateMark);
        return detail;
    }

    /// <summary>The badge of a mod: OTHER CREATOR and its type for a mod of another creator made with our API, else its
    /// own badge (CLIENT / HOST / SERVER, plus PRIVATE).</summary>
    internal static string Badge(bool otherCreatorsApiMod, string sideBadge, string ownBadge) =>
        otherCreatorsApiMod ? OtherCreator + " · " + sideBadge : ownBadge;

    /// <summary>The value of a mod's switch on the list: core is always ON.</summary>
    internal static string SwitchValue(bool core, string switchText) => core ? "ON" : switchText;

    private static string Add(string a, string b) => a.Length == 0 ? b : a + "  " + b;
}
