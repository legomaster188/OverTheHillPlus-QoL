using System;
using System.Collections.Generic;
using System.Linq;

namespace OverTheHillPlus.UI;

/// <summary>
/// How a mod page lays out its sections: one group per (mode, tab) under the tab's name as a heading. Groups of a
/// lower mode come first, then in the order their first section was registered; inside a group the sections are
/// sorted by order (stable: equal orders keep registration order). Pure logic, unit tested.
/// </summary>
internal static class PageGroups
{
    internal sealed class Group<T>
    {
        public int Mode;
        public string Tab;
        public readonly List<T> Items = new();
    }

    internal static List<Group<T>> Build<T>(IEnumerable<T> items, Func<T, int> mode, Func<T, string> tab, Func<T, int> order)
    {
        var groups = new List<Group<T>>();
        if (items == null) return groups;
        foreach (var item in items)
        {
            int m = mode(item);
            string t = tab(item) ?? "";
            var g = groups.Find(x => x.Mode == m && string.Equals(x.Tab, t, StringComparison.Ordinal));
            if (g == null) groups.Add(g = new Group<T> { Mode = m, Tab = t });
            g.Items.Add(item);
        }
        var sorted = groups.OrderBy(g => g.Mode).ToList(); // OrderBy is stable: first registration order per mode
        foreach (var g in sorted)
        {
            var byOrder = g.Items.OrderBy(order).ToList();
            g.Items.Clear();
            g.Items.AddRange(byOrder);
        }
        return sorted;
    }
}
