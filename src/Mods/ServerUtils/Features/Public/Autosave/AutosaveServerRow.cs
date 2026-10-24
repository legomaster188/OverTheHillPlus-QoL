using System;
using System.Collections.Generic;
using OverTheHillPlus.ServerUtils;

namespace OverTheHillPlus.Features.Public.Autosave;

/// <summary>
/// The Autosave row on the Create Server screen (drawn by Server Creator+ through core's <see cref="ServerSettings"/>):
/// OFF / 5 / 10 / 15 MIN, plus the configured interval if it's another one.
/// </summary>
internal static class AutosaveServerRow
{
    private static readonly List<string> Labels = new();
    private static readonly List<int> Minutes = new();

    public static ServerSettingRow Create(int order) => new()
    {
        Id = "autosave",
        Label = "Autosave",
        Order = order,
        ModId = ServerUtilsPlugin.ModId,
        Available = () => AutosaveConfig.Enabled != null && AutosaveConfig.IntervalMinutes != null,
        Selected = () =>
        {
            Build();
            if (!AutosaveConfig.Enabled.Value) return 0;
            int now = (int)Math.Round(AutosaveConfig.IntervalMinutes.Value);
            return Math.Max(0, Minutes.IndexOf(now));
        },
        Options = () => Labels,
        Choose = i =>
        {
            if (i <= 0 || i >= Minutes.Count) { AutosaveConfig.Enabled.Value = false; return; }
            AutosaveConfig.Enabled.Value = true;
            AutosaveConfig.IntervalMinutes.Value = Minutes[i];
        },
    };

    private static void Build()
    {
        Labels.Clear();
        Minutes.Clear();
        Labels.Add("OFF");
        Minutes.Add(0);
        int current = (int)Math.Round(AutosaveConfig.IntervalMinutes.Value);
        var minutes = new List<int> { 5, 10, 15 };
        if (AutosaveConfig.Enabled.Value && current > 0 && !minutes.Contains(current)) minutes.Add(current);
        minutes.Sort();
        foreach (int m in minutes)
        {
            Labels.Add(m + " MIN");
            Minutes.Add(m);
        }
    }
}
