using System;
using System.Collections.Generic;
using System.Linq;

namespace OverTheHillPlus;

/// <summary>
/// One option row on the game's Create Server screen ("Label  ◂ VALUE ▸"). Rows are plain data: Server Creator+
/// clones the game's own Privacy row for each, fills, places and wires it. Values come from and go to the owning mod's
/// settings, so the rows and that mod's MODS page always agree. The arrows step through <see cref="Options"/>
/// (left = previous, right = next); <see cref="Submit"/> is the optional Enter / click / A on the row (the server name
/// opens its text field with it).
/// </summary>
internal sealed partial class ServerSettingRow
{
    public string Id;
    public string Label;

    /// <summary>Position among the added rows (lower = higher up). When the screen is short, the last rows are left out.</summary>
    public int Order;

    /// <summary>Id of the mod whose setting the row shows (e.g. "servercreator", "core").</summary>
    public string ModId;

    /// <summary>True for a row of a private mod (one that is not part of the public release).</summary>
    public bool Private;

    /// <summary>False = leave the row out (e.g. its setting isn't bound). Asked each time the screen is built.</summary>
    public Func<bool> Available;

    /// <summary>The option labels, in order (the game shows them in its own value style).</summary>
    public Func<IReadOnlyList<string>> Options;

    /// <summary>Index of the option matching the current setting. Asked before <see cref="Options"/>.</summary>
    public Func<int> Selected;

    /// <summary>The player picked option <c>index</c> with the arrows.</summary>
    public Action<int> Choose;

    /// <summary>Optional: Enter / click / A on the row itself (argument: true when it came from a gamepad).</summary>
    public Action<bool> Submit;

    internal static readonly string[] OffOn = { "OFF", "ON" };

    /// <summary>An OFF / ON row. <paramref name="available"/> is asked first; the getter and setter only when it says yes.</summary>
    public static ServerSettingRow Toggle(string id, string label, int order, string modId,
        Func<bool> available, Func<bool> get, Action<bool> set) => new()
    {
        Id = id,
        Label = label,
        Order = order,
        ModId = modId,
        Available = available,
        Options = () => OffOn,
        Selected = () => get() ? 1 : 0,
        Choose = i => set(i == 1),
    };

    /// <summary>A row with fixed choices: <paramref name="values"/>[i] is stored, <paramref name="labels"/>[i] is shown.
    /// A stored value that isn't one of them shows as the first choice.</summary>
    public static ServerSettingRow Choice(string id, string label, int order, string modId,
        Func<bool> available, Func<string> get, Action<string> set, string[] values, string[] labels) => new()
    {
        Id = id,
        Label = label,
        Order = order,
        ModId = modId,
        Available = () => values.Length > 0 && (available == null || available()),
        Options = () => labels,
        Selected = () => IndexOf(values, get()),
        Choose = i => { if (i >= 0 && i < values.Length) set(values[i]); },
    };

    /// <summary>Index of <paramref name="value"/> in <paramref name="values"/> (ignoring case), 0 when it isn't there.</summary>
    internal static int IndexOf(string[] values, string value)
    {
        for (int i = 0; i < values.Length; i++)
            if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }
}

/// <summary>
/// The rows for the Create Server screen. Every mod adds the rows of its own settings (in a feature's Register or its
/// plugin's Load); Server Creator+ draws them, in <see cref="Rows"/> order. Without Server Creator+ nothing draws them.
/// </summary>
internal static class ServerSettings
{
    private static readonly List<ServerSettingRow> Defs = new();
    private static List<ServerSettingRow> _sorted;

    /// <summary>
    /// Raised when the player creates a server from the Create Server screen (before the game starts hosting it), so
    /// "apply when the server starts" settings know which session to act on.
    /// </summary>
    public static event Action ServerCreated;

    /// <summary>Adds a row, replacing an earlier one with the same id.</summary>
    public static void AddRow(ServerSettingRow row)
    {
        if (row == null || string.IsNullOrEmpty(row.Id) || row.Options == null || row.Selected == null || row.Choose == null)
            throw new ArgumentException("A Create Server row needs an id, options, a selected index and a choose action.");
        Defs.RemoveAll(d => d.Id == row.Id);
        Defs.Add(row);
        _sorted = null;
    }

    /// <summary>The rows in screen order (equal orders keep the order they were added in).</summary>
    public static IReadOnlyList<ServerSettingRow> Rows => _sorted ??= Defs.OrderBy(d => d.Order).ToList();

    /// <summary>Called by Server Creator+ when a server is created from its screen. A failing handler doesn't stop the
    /// others; its error goes to <paramref name="warn"/>.</summary>
    internal static void RaiseServerCreated(Action<string> warn = null)
    {
        var handlers = ServerCreated;
        if (handlers == null) return;
        foreach (Action h in handlers.GetInvocationList())
        {
            try { h(); }
            catch (Exception e) { DebugLog.Error("core", e, "ServerSettings.RaiseServerCreated"); warn?.Invoke("Server created handler failed: " + e.Message); }
        }
    }

    /// <summary>Tests only: forgets every row and handler.</summary>
    internal static void Reset()
    {
        Defs.Clear();
        _sorted = null;
        ServerCreated = null;
    }
}
