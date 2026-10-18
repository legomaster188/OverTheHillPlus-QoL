using System;
using System.Collections.Generic;

namespace OverTheHillPlus.UI;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The INSTALLED tab's "Other creators" rows: every BepInEx plugin that isn't one of ours (ThirdPartyScan), with its
/// version, file, GUID (in the row's hint) and an OTHER CREATOR badge. Its switch renames the plugin's file to
/// .dll.disabled and back (<see cref="ThirdParty.SetOn"/>); the value says when that takes effect.
/// </summary>
internal static class ThirdPartyRows
{
    private sealed class Shown
    {
        public bool On;
        public string Value;
    }

    // The switch reads the queue and the file: once, and again after each change, not on every IMGUI event.
    private static readonly Dictionary<ThirdPartyPlugin, Shown> State = new();

    internal static void Draw()
    {
        List<ThirdPartyPlugin> list = ThirdPartyScan.List;
        if (list.Count == 0) return;
        SimpleMenu.Heading(GameMenu.ModListModel.Heading(GameMenu.ModListModel.Group.OtherCreators));
        foreach (var p in list)
        {
            var s = StateOf(p);
            bool canSwitch = ThirdParty.CanSwitch(p);
            var card = new GameMenu.CardInfo
            {
                Seed = string.IsNullOrEmpty(p.Guid) ? p.Name : p.Guid, Title = p.Name, Author = GameMenu.ModCards.OtherAuthor,
                Version = p.Version, Status = p.State == PluginState.Failed ? "FAILED" : (s.Value ?? "").ToUpperInvariant(),
            };
            bool pressed = SimpleMenu.ListRow(p.Name, Detail(p), GameMenu.ModListModel.OtherCreator, s.Value, s.On, Hint(p), canSwitch, out int step, card);
            if ((pressed || step != 0) && canSwitch) Switch(p, !s.On);
        }
        SimpleMenu.Note("Plugins of other creators are switched off by renaming their file to .dll.disabled (BepInEx only loads .dll files).");
    }

    private static Shown StateOf(ThirdPartyPlugin p)
    {
        if (State.TryGetValue(p, out var s)) return s;
        s = new Shown();
        try
        {
            s.On = ThirdParty.IsOn(ThirdPartyScan.PluginsRoot, ThirdPartyScan.PluginDir, p);
            s.Value = ThirdParty.SwitchText(ThirdPartyScan.PluginsRoot, ThirdPartyScan.PluginDir, p);
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "ThirdPartyRows.StateOf");
            s.On = ThirdParty.StartedOn(p);
            s.Value = s.On ? "ON" : "OFF";
            Core.CorePlugin.Logger?.LogWarning($"[Core] {p.Name}: {e.Message}");
        }
        State[p] = s;
        return s;
    }

    private static void Switch(ThirdPartyPlugin p, bool on)
    {
        try
        {
            ThirdParty.SetOn(ThirdPartyScan.PluginsRoot, ThirdPartyScan.PluginDir, p, on);
            State.Remove(p);
            var s = StateOf(p);
            Menu.SetStatus(s.On == ThirdParty.StartedOn(p) ? $"{p.Name}: {(s.On ? "on" : "off")}, as when the game started" : $"{p.Name}: {s.Value}");
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "ThirdPartyRows.Switch");
            State.Remove(p);
            Menu.SetStatus($"Failed to switch {p.Name}: {e.Message}");
        }
    }

    private static string Detail(ThirdPartyPlugin p)
    {
        string detail = string.IsNullOrEmpty(p.Version) ? "" : "v" + p.Version;
        string file = p.File == null ? null : p.File.Substring(p.File.LastIndexOf('/') + 1);
        if (file != null) detail += (detail.Length > 0 ? "  ·  " : "") + file;
        if (p.State == PluginState.Failed) detail += "  <color=#E5534B>didn't load</color>";
        return detail;
    }

    private static string Hint(ThirdPartyPlugin p)
    {
        string where = (string.IsNullOrEmpty(p.Guid) ? "" : p.Guid + "  ·  ") + (p.File == null ? "outside BepInEx/plugins" : "BepInEx/plugins/" + p.File) + ". ";
        if (!ThirdParty.CanSwitch(p)) return where + "Its file is outside BepInEx/plugins, so it can't be switched off here.";
        return p.State switch
        {
            PluginState.Failed => where + p.Problem + " Off renames it to .dll.disabled now.",
            PluginState.Disabled => where + "Switched off. On renames it back now; it loads after a restart.",
            _ when p.LoadedBeforeCore => where + "Off renames it at the next start; it loads before the Mod Manager, so it stops after the second restart.",
            _ => where + "Off renames it to .dll.disabled at the next start, so it stops after a restart.",
        };
    }
}
