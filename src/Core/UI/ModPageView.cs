using System;
using System.Collections.Generic;
using OverTheHillPlus.Features;
using OverTheHillPlus.UI.Native;
using UnityEngine;

namespace OverTheHillPlus.UI;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// One mod's page on the MODS page: its description, "Failed to start: ..." / "N features unavailable after the game
/// update" when that happened, its ON/OFF switch (takes effect after a restart), then <see cref="ModInfo.DrawSimple"/>
/// and an "Advanced" fold holding <see cref="ModInfo.DrawAdvanced"/> and the cards its features added with
/// Registry.Section, grouped under their tab names as headings. The fold remembers its state in
/// [General] AdvancedMenu; Tab (or a gamepad's Select) switches it.
/// </summary>
internal static class ModPageView
{
    /// <summary>Core's own id: always on (it runs the page), so it has no switch.</summary>
    internal const string CoreId = "core";

    internal static bool IsCore(ModInfo m) => m != null && string.Equals(m.Id, CoreId, StringComparison.OrdinalIgnoreCase);

    private static bool AdvancedOpen => CoreConfig.AdvancedMenu != null && CoreConfig.AdvancedMenu.Value;

    internal static void ToggleAdvanced()
    {
        if (CoreConfig.AdvancedMenu == null) return;
        // The selection stays where it is (on the Advanced row when that was pressed).
        CoreConfig.AdvancedMenu.Value = !CoreConfig.AdvancedMenu.Value;
    }

    /// <summary>True when the mod has anything for the Advanced fold.</summary>
    internal static bool HasAdvanced(ModInfo m) => m != null && (m.DrawAdvanced != null || Registry.SectionsForMod(m.Id).Count > 0);

    internal static void Draw(ModInfo m)
    {
        Menu.BeginGroup(0, "mod/" + m.Id);
        if (!string.IsNullOrEmpty(m.Description)) SimpleMenu.Note(m.Description);
        if (!IsCore(m)) SideNote(m);

        string failed = ModManager.FailedReason(m.Id);
        if (failed != null) Menu.Banner(ModStatus.FailedText(failed), Theme.Bad);
        int unavailable = PatchReport.Failed(m.Id);
        if (unavailable > 0) Menu.Banner(ModStatus.UnavailableText(unavailable), Theme.Warn);

        if (ModManager.Blocked && !IsCore(m)) Menu.Banner(ModManager.BlockedText, Theme.Bad);

        if (!IsCore(m))
        {
            bool on = ModManager.SwitchedOn(m.Id);
            string value = ModStatus.SwitchText(on, ModManager.PendingRestart(m.Id));
            bool pressed = SimpleMenu.ListRow("Mod", null, null, value, on, "Switching a mod on or off takes effect after you restart the game.",
                true, out int step);
            if (pressed || step != 0) ModManager.SetEnabled(m.Id, !on);
        }

        // Update / Uninstall (AVAILABLE tab's installer); while it asks for confirmation, nothing else shows.
        if (AvailableTab.DrawModActions(m)) return;

        // A mod whose Load threw may not have bound its settings: no rows against unbound entries. The banner, the
        // ON/OFF switch and Update / Uninstall above stay, so the player can still switch it off or fix it.
        if (failed != null) return;

        if (!ModManager.IsLoaded(m.Id))
        {
            // While blocked, the banner above already says why it isn't running.
            if (!ModManager.Blocked)
                SimpleMenu.Note("This mod is switched off, so it isn't running. Switch it on and restart the game to change its settings.");
            return;
        }

        bool simple = m.DrawSimple != null;
        bool advanced = HasAdvanced(m);
        if (!simple && !advanced)
        {
            SimpleMenu.Note("This mod has no settings.");
            return;
        }
        if (simple)
        {
            Menu.BeginGroup(0, "mod/" + m.Id + "/simple");
            Safe(m.DrawSimple, m.Name ?? m.Id);
        }
        if (!advanced) return;
        if (simple)
        {
            Menu.Y += 6f;
            bool open = AdvancedOpen;
            SimpleMenu.Button("Advanced", open ? "Hide" : "Show", "Every setting of this mod. Tab switches it too.", ToggleAdvanced);
            if (!open) return;
        }
        DrawAdvanced(m);
    }

    private static void DrawAdvanced(ModInfo m)
    {
        bool first = true;
        if (m.DrawAdvanced != null)
        {
            Menu.BeginGroup(0, "mod/" + m.Id + "/advanced");
            Safe(m.DrawAdvanced, m.Name ?? m.Id);
            first = false;
        }
        foreach (var g in Groups(m.Id))
        {
            GroupTitle(g.Tab, first);
            first = false;
            // Fold keys per mod: two mods with a card of the same title under the same tab fold separately.
            Menu.BeginGroup(g.Mode, m.Id + "/" + g.Tab, g.Tab);
            foreach (var s in g.Items) Safe(s.Draw, s.Owner);
        }
    }

    // The sections list is rebuilt only when a mod registers a new section (SectionsForMod returns a new list then).
    private static readonly Dictionary<string, (IReadOnlyList<Registry.SectionEntry> source, List<PageGroups.Group<Registry.SectionEntry>> groups)> GroupCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static List<PageGroups.Group<Registry.SectionEntry>> Groups(string modId)
    {
        var sections = Registry.SectionsForMod(modId);
        if (GroupCache.TryGetValue(modId, out var cached) && ReferenceEquals(cached.source, sections)) return cached.groups;
        var groups = PageGroups.Build(sections, s => s.Mode, s => s.Tab, s => s.Order);
        GroupCache[modId] = (sections, groups);
        return groups;
    }

    /// <summary>A failure inside the native renderer propagates (the page falls back to Classic); anything else is
    /// the mod's own bug and shows inline.</summary>
    private static void Safe(Action draw, string owner)
    {
        if (draw == null) return;
        try { draw(); }
        catch (Exception e) when (!(Menu.NativeActive && ModsPage.IsNativeError(e))) { DebugLog.Error("core", e, "ModPageView.Safe"); Menu.Small($"<color=#E5534B>{owner} failed: {e.Message}</color>"); }
    }

    /// <summary>Who needs the mod (its CLIENT / HOST / SERVER type), and for a Server mod in a session whether it runs there.</summary>
    private static void SideNote(ModInfo m)
    {
        SimpleMenu.Note(RequiredMods.SideText(m.Side));
        string status = Features.Public.ModTypes.RequiredModsSession.StatusFor(m);
        if (status != null) Menu.Banner(status, status.StartsWith("Off") ? Theme.Warn : Theme.Info);
    }

    /// <summary>A heading over a group of settings on a mod page (upper case, with a rule under it).</summary>
    internal static void GroupTitle(string title, bool first)
    {
        if (GameMenu.RowRecorder.Recording) { GameMenu.Recorded.GroupTitle(title, first); return; }
        if (!first) Menu.Y += 8f;
        var r = new Rect(4f, Menu.Y, Menu.ContentWidth - 8f, 22f);
        bool native = Menu.NativeActive;
        Menu.Label(r, (title ?? "").ToUpperInvariant(), Theme.Section, native ? SimpleMenu.Yellow : Theme.Accent);
        Theme.Fill(new Rect(4f, Menu.Y + 23f, Menu.ContentWidth - 8f, 1f), native ? NativeLook.Line : Theme.Line, 0);
        Menu.Y += 32f;
    }
}
