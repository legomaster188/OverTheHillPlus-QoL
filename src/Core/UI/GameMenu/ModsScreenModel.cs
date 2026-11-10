using System;
using System.Collections.Generic;
using System.Text;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// The logic of the MODS screen in the game's Settings menu (<see cref="GameModsTab"/>): what its list on the left holds
/// and in which order, what Esc / B does, and the text of the game-style pop-ups (<see cref="GameModal"/>).
/// Pure logic (no Unity or loader types), unit tested.
/// </summary>
internal static class ModsScreenModel
{
    /// <summary>The overview: every mod with its switch, the search and the plugins of other creators.</summary>
    internal const string AllKey = "#all";
    /// <summary>The mods of the session you are in, as the host lists them.</summary>
    internal const string ServerKey = "#server";
    private const string TabPrefix = "#tab:";

    internal static string TabKey(int tab) => TabPrefix + tab;

    /// <summary>The tab a key opens (the AVAILABLE tab and the like), or -1.</summary>
    internal static int TabOf(string key) =>
        key != null && key.StartsWith(TabPrefix, StringComparison.Ordinal) && int.TryParse(key.Substring(TabPrefix.Length), out int t) ? t : -1;

    /// <summary>True when the key is a mod's id (not one of the screen's own pages).</summary>
    internal static bool IsMod(string key) => !string.IsNullOrEmpty(key) && key[0] != '#';

    /// <summary>One installed mod, as the list needs it.</summary>
    internal readonly struct Mod
    {
        public readonly string Id, Name;
        public readonly bool Core, OtherCreator, On, PendingRestart, Failed, Update;

        public Mod(string id, string name, bool core, bool otherCreator, bool on, bool pendingRestart = false, bool failed = false, bool update = false)
        {
            Id = id;
            Name = name;
            Core = core;
            OtherCreator = otherCreator;
            On = on;
            PendingRestart = pendingRestart;
            Failed = failed;
            Update = update;
        }
    }

    /// <summary>One row of the list: the page it opens, the name, and a short state on the right (dimmed).</summary>
    internal readonly struct Entry
    {
        public readonly string Key, Label, Value;
        /// <summary>A heading over a group (not selectable).</summary>
        public readonly bool Heading;

        public Entry(string key, string label, string value, bool heading = false)
        {
            Key = key;
            Label = label;
            Value = value;
            Heading = heading;
        }
    }

    /// <summary>
    /// The list: "All mods" first, then core and our mods by name, then the mods of other creators made with our API
    /// (under a heading), then "This server" while in a session that lists its mods, then the extra tabs (e.g.
    /// AVAILABLE, the installer) by their names.
    /// </summary>
    internal static List<Entry> Build(IReadOnlyList<Mod> mods, bool inSession, IReadOnlyList<string> extraTabs)
    {
        var list = new List<Entry> { new(AllKey, "All mods", mods == null ? "" : mods.Count.ToString()) };
        var ours = new List<Mod>();
        var others = new List<Mod>();
        if (mods != null)
            foreach (var m in mods)
                (m.OtherCreator ? others : ours).Add(m);
        Comparison<Mod> order = (a, b) => ModListModel.Compare(a.Core, a.Name ?? a.Id, b.Core, b.Name ?? b.Id);
        ours.Sort(order);
        others.Sort(order);
        foreach (var m in ours) list.Add(new Entry(m.Id, m.Name ?? m.Id, State(m)));
        if (others.Count > 0)
        {
            list.Add(new Entry(null, ModListModel.Heading(ModListModel.Group.OtherCreators), null, true));
            foreach (var m in others) list.Add(new Entry(m.Id, m.Name ?? m.Id, State(m)));
        }
        bool more = inSession || (extraTabs != null && extraTabs.Count > 0);
        if (more) list.Add(new Entry(null, "More", null, true));
        if (inSession) list.Add(new Entry(ServerKey, "This server", ""));
        if (extraTabs != null)
            for (int i = 0; i < extraTabs.Count; i++)
                if (!string.IsNullOrEmpty(extraTabs[i])) list.Add(new Entry(TabKey(i + 1), TabLabel(extraTabs[i]), ""));
        return list;
    }

    /// <summary>"Available" reads as "Get more mods" in the list (it is the installer).</summary>
    internal static string TabLabel(string tab) =>
        string.Equals(tab, "Available", StringComparison.OrdinalIgnoreCase) ? "Get more mods" : tab;

    /// <summary>The short state on a mod's row: failed, restart, off, update; nothing when it simply runs.</summary>
    internal static string State(Mod m)
    {
        if (m.Failed) return "FAILED";
        if (m.PendingRestart) return "RESTART";
        if (!m.Core && !m.On) return "OFF";
        if (m.Update) return "UPDATE";
        return "";
    }

    /// <summary>The index of <paramref name="key"/> in <paramref name="list"/> (case-insensitive for mod ids), or -1.</summary>
    internal static int IndexOf(IReadOnlyList<Entry> list, string key)
    {
        if (list == null || key == null) return -1;
        for (int i = 0; i < list.Count; i++)
            if (!list[i].Heading && string.Equals(list[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>What Esc / B does on the screen.</summary>
    internal enum BackStep
    {
        /// <summary>Nothing of ours: the keys belong to a text box or a key capture.</summary>
        None,
        /// <summary>From the settings on the right back to the list.</summary>
        ToList,
        /// <summary>The game closes the screen (back to Settings, or to where it was opened from).</summary>
        Close,
    }

    /// <summary>
    /// Esc / B: from the settings back to the list, unless the screen was opened straight on a mod's page from somewhere
    /// else (then it closes, back to there); from the list the screen closes.
    /// </summary>
    internal static BackStep Back(bool inPage, bool closeOnBack, bool keysElsewhere)
    {
        if (keysElsewhere) return BackStep.None;
        if (inPage && !closeOnBack) return BackStep.ToList;
        return BackStep.Close;
    }

    /// <summary>Whether the game may close the screen on its back input this frame.</summary>
    internal static bool CanPop(bool inPage, bool closeOnBack, bool keysElsewhere, bool justWentBack) =>
        !justWentBack && Back(inPage, closeOnBack, keysElsewhere) == BackStep.Close;

    // ---------------------------------------------------------------- pop-up text

    /// <summary>One line of a pop-up's list: a name, a short tag (MISSING, OFF ...) and what happens to it.</summary>
    internal readonly struct Line
    {
        public readonly string Name, Tag, Text;
        /// <summary>0 fine / info, 1 warning (fixable), 2 blocks.</summary>
        public readonly int Tone;

        public Line(string name, string tag, string text, int tone)
        {
            Name = name;
            Tag = tag;
            Text = text;
            Tone = tone;
        }
    }

    /// <summary>The game's yellow (its highlight) and a muted red that sits with the game's cream text.</summary>
    internal const string Yellow = "#FFD865", Red = "#F0705F", Cream = "#FFF9EE";

    /// <summary>
    /// A pop-up's body: the lead sentence, then up to <paramref name="maxLines"/> list lines ("Name  TAG  what happens",
    /// the tag in the line's tone), "and N more" when there are more, then the notes, each as its own paragraph.
    /// Markup in names is neutralised (a server or mod name can't restyle the pop-up).
    /// </summary>
    internal static string Body(string lead, IReadOnlyList<Line> lines, IReadOnlyList<string> notes, int maxLines = 8)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(lead)) sb.Append(lead);
        if (lines != null && lines.Count > 0)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            int shown = Math.Min(lines.Count, Math.Max(1, maxLines));
            for (int i = 0; i < shown; i++)
            {
                var l = lines[i];
                if (i > 0) sb.Append('\n');
                sb.Append(Plain(l.Name));
                if (!string.IsNullOrEmpty(l.Tag))
                    sb.Append("  <color=").Append(l.Tone >= 2 ? Red : l.Tone == 1 ? Yellow : Cream).Append("><size=80%>").Append(Plain(l.Tag)).Append("</size></color>");
                if (!string.IsNullOrEmpty(l.Text)) sb.Append("  <alpha=#99>").Append(Plain(l.Text)).Append("</alpha>");
            }
            if (lines.Count > shown) sb.Append("\n<alpha=#99>and ").Append(lines.Count - shown).Append(" more</alpha>");
        }
        if (notes != null)
            foreach (var n in notes)
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("<alpha=#B3>").Append(n).Append("</alpha>");
            }
        return sb.ToString();
    }

    /// <summary>Text shown as it is: '&lt;' and '&gt;' become look-alike quotes, so no rich-text tag gets through.</summary>
    internal static string Plain(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace('<', '‹').Replace('>', '›');
}
