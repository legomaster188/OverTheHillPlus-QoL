using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace OverTheHillPlus.Features.Public.CreateScreen;

/// <summary>
/// One option row added to the game's Create Server screen ("Label  ◂ VALUE ▸"), drawn by a clone of the game's own
/// Privacy row. Rows are plain data: the screen code clones, fills, places and wires them (see
/// <see cref="CreateScreenBuilder"/>). Values come from and go to config entries, so the rows and the PLUS menu always
/// agree.
/// </summary>
internal sealed class CreateRow
{
    public string Id;
    public string Label;

    /// <summary>Position among the added rows (lower = higher up). When the screen is short, the last rows are left out.</summary>
    public int Order;

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

    /// <summary>An OFF / ON row for a bool setting. The entry is looked up lazily: features bind in name order.</summary>
    public static CreateRow Toggle(string id, string label, int order, Func<ConfigEntry<bool>> entry) => new()
    {
        Id = id,
        Label = label,
        Order = order,
        Available = () => entry() != null,
        Options = () => OffOn,
        Selected = () => entry().Value ? 1 : 0,
        Choose = i => entry().Value = i == 1,
    };

    /// <summary>A row for a string setting with fixed choices (values[i] is shown as labels[i]).</summary>
    public static CreateRow Choice(string id, string label, int order, Func<ConfigEntry<string>> entry, string[] values, string[] labels) => new()
    {
        Id = id,
        Label = label,
        Order = order,
        Available = () => entry() != null && values.Length > 0,
        Options = () => labels,
        Selected = () =>
        {
            string v = entry().Value;
            for (int i = 0; i < values.Length; i++)
                if (string.Equals(values[i], v, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        },
        Choose = i => { if (i >= 0 && i < values.Length) entry().Value = values[i]; },
    };
}

/// <summary>The rows for the Create Server screen, registered by features in their <c>Register()</c>.</summary>
internal static class CreateScreenRows
{
    private static readonly List<CreateRow> Defs = new();

    /// <summary>
    /// Raised when the player creates a server from the Create Server screen (before the game starts hosting it), so
    /// "apply when the server starts" settings know which session to act on.
    /// </summary>
    public static event Action ServerCreated;

    public static void Add(CreateRow row)
    {
        if (row == null || string.IsNullOrEmpty(row.Id) || row.Options == null || row.Selected == null || row.Choose == null)
            throw new ArgumentException("A Create Server row needs an id, options, a selected index and a choose action.");
        Defs.RemoveAll(d => d.Id == row.Id);
        Defs.Add(row);
    }

    /// <summary>The rows in screen order (equal orders keep registration order).</summary>
    internal static List<CreateRow> Sorted() => Defs.OrderBy(d => d.Order).ToList();

    internal static void RaiseServerCreated()
    {
        var handlers = ServerCreated;
        if (handlers == null) return;
        foreach (Action h in handlers.GetInvocationList())
        {
            try { h(); }
            catch (Exception e) { Plugin.Logger.LogWarning("[CreateScreen] Server created handler failed: " + e.Message); }
        }
    }
}
