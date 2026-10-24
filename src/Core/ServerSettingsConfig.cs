using System;
using BepInEx.Configuration;

namespace OverTheHillPlus;

// The config-entry shortcuts for ServerSettingRow, apart from ServerSettings.cs so that one stays free of BepInEx
// (the tests compile it on its own).
internal sealed partial class ServerSettingRow
{
    /// <summary>An OFF / ON row for a bool setting. The entry is looked up lazily: features bind in name order, and
    /// a row whose entry isn't bound (yet) is left out.</summary>
    public static ServerSettingRow Toggle(string id, string label, int order, string modId, Func<ConfigEntry<bool>> entry) =>
        Toggle(id, label, order, modId, () => entry() != null, () => entry().Value, v => entry().Value = v);

    /// <summary>A row for a string setting with fixed choices (values[i] is shown as labels[i]).</summary>
    public static ServerSettingRow Choice(string id, string label, int order, string modId, Func<ConfigEntry<string>> entry,
        string[] values, string[] labels) =>
        Choice(id, label, order, modId, () => entry() != null, () => entry().Value, v => entry().Value = v, values, labels);
}
