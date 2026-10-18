using System;
using System.Collections.Generic;
using OverTheHillPlus.Core.Install;
using OverTheHillPlus.Features.Public.ModTypes;
using OverTheHillPlus.UI.Native;
using UnityEngine;

namespace OverTheHillPlus.UI;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The MODS page's "this server needs mods" prompt, shown in place of the tabs when the server browser holds a join
/// (Server Browser+ asks for it with <see cref="Show"/>; core never calls the browser). It lists the server's issues
/// (RequiredMods.Compare) and offers what RequiredMods.PlanJoin allows:
/// <list type="bullet">
/// <item><b>Install &amp; join</b>: downloads the missing mods with the AVAILABLE tab's installer (the prompt says
///   which files from GitHub first, like every download), switches on the ones that are off, then the browser
///   remembers the server so it is rejoined after the restart (<see cref="Request.PrepareRejoin"/>).</item>
/// <item><b>Switch on &amp; restart</b>: only switched-off mods: switches them on, same rejoin after the restart.</item>
/// <item><b>Join anyway / Join without them</b>: unless another major version blocks the join, and never when a mod
///   with network prefabs (cars, trailers) differs from the host's: the host refuses that join, so the prompt names
///   the difference (RequiredMods.PrefabSummary) and offers install / switch on / switch off &amp; restart instead.</item>
/// </list>
/// </summary>
internal static class RequiredModsPrompt
{
    internal sealed class Request
    {
        /// <summary>The server's name as the browser shows it (plain text).</summary>
        public string ServerName;
        public List<RequiredModIssue> Issues = new();
        /// <summary>Server mods the host runs that did not fit into its list.</summary>
        public int More;
        /// <summary>Joins as things are (null: can't). Called from OnGUI: the browser runs the join on its next Update.</summary>
        public Action Join;
        /// <summary>Remembers the server so the browser rejoins it after the restart.</summary>
        public Action PrepareRejoin;
    }

    private enum Step { Ask, Downloading, Restart, Failed }

    private static Request _req;
    private static Step _step;
    private static string _error;
    private static int _lastDrawFrame = -100;
    private static bool _drawn;
    // Downloading in the background: the prompt is hidden (the tabs show again) and reports in the status line.
    private static bool _hidden;

    /// <summary>True while a prompt is waiting on the MODS page.</summary>
    public static bool Active => _req != null;

    /// <summary>Opens the MODS page on the prompt (replacing any earlier one).</summary>
    public static void Show(Request request)
    {
        if (request == null) return;
        request.Issues ??= new List<RequiredModIssue>();
        _req = request;
        _step = Step.Ask;
        _error = null;
        _drawn = false;
        _hidden = false;
        AvailableTab.EnsureList(); // names and downloads come from the mod list
        ModsPage.Open();
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
    }

    public static void Dismiss()
    {
        if (_req == null) return;
        _req = null;
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
    }

    /// <summary>Draws the prompt in the MODS page's content area. True while it is shown (the tab draws nothing).</summary>
    internal static bool Draw()
    {
        if (_req == null || _hidden) return false;
        // Left behind (the page was closed): dropped, unless its downloads are still running.
        if (_drawn && Time.frameCount - _lastDrawFrame > 2 && _step != Step.Downloading)
        {
            _req = null;
            return false;
        }
        _drawn = true;
        _lastDrawFrame = Time.frameCount;

        var req = _req;
        string server = string.IsNullOrWhiteSpace(req.ServerName) ? "this server" : req.ServerName;
        SimpleMenu.Heading("Join " + server);
        switch (_step)
        {
            case Step.Downloading:
                Menu.Banner("Downloading the mods this server needs...", Theme.Info);
                SimpleMenu.Note("They take effect after you restart the game; Server Browser+ then rejoins " + server + ".");
                SimpleMenu.Button("Hide", null, "The downloads carry on; the status line says when they are done.", Hide);
                return true;
            case Step.Restart:
                Menu.Banner("Ready. Restart the game to finish: once it has started, Server Browser+ rejoins " + server + ".", Theme.Good);
                SimpleMenu.Button("Quit the game", null, "Closes the game. Start it again (from Steam) and the rejoin starts by itself.", Application.Quit);
                SimpleMenu.Button("Close", null, "Restart later: the rejoin waits up to 30 minutes.", Close);
                return true;
            case Step.Failed:
                Menu.Banner("Download failed: " + _error, Theme.Bad);
                SimpleMenu.Button("Back", null, "Back to the list of mods this server needs.", () => _step = Step.Ask);
                SimpleMenu.Button("Cancel", null, "Doesn't join.", Close);
                return true;
        }

        var plan = RequiredMods.PlanJoin(req.Issues, id => AvailableTab.Listed(id)?.Version);
        string prefab = plan.PrefabIssues ? RequiredMods.PrefabSummary(req.Issues, RequiredModsSession.NameOf) : null;
        if (plan.Blocking.Count > 0)
            Menu.Banner("You can't join yet: " + Names(plan.Blocking) + (plan.Blocking.Count == 1 ? " is" : " are") +
                        " another major version than the server's. Install the same version as the server, then join again.", Theme.Bad);
        else if (prefab != null)
            Menu.Banner(prefab + (plan.CanFix ? " Fix it below, then the game rejoins after a restart." : " You can't join until they match."),
                plan.CanFix ? Theme.Warn : Theme.Bad);
        else if (plan.WarningsOnly)
            Menu.Banner("Some of your mods are another minor version than the server's. That should still work.", Theme.Warn);
        else
            Menu.Banner("This server runs mods that everyone in it needs.", Theme.Info);

        foreach (var i in req.Issues)
        {
            string badge = i.Kind switch
            {
                RequiredIssueKind.Missing => "MISSING",
                RequiredIssueKind.Off => "OFF",
                RequiredIssueKind.VersionMinor => "VERSION",
                RequiredIssueKind.PrefabExtra => "EXTRA",
                RequiredIssueKind.PrefabNotReady => "FAILED",
                RequiredIssueKind.PrefabBuild => "BUILD",
                _ => "BLOCKED",
            };
            string value = i.Kind switch
            {
                RequiredIssueKind.Missing => plan.Install.Contains(i.Id) ? "Download" : i.Prefab ? "Can't join" : "Not listed",
                RequiredIssueKind.Off => "Switch on",
                RequiredIssueKind.VersionMinor => "Warning",
                RequiredIssueKind.PrefabExtra => "Switch off",
                _ => plan.Install.Contains(i.Id) ? "Download" : "Can't join",
            };
            bool fixable = i.Kind is RequiredIssueKind.Off or RequiredIssueKind.VersionMinor or RequiredIssueKind.PrefabExtra || plan.Install.Contains(i.Id) ||
                           plan.MissingUnlisted.Contains(i.Id);
            SimpleMenu.ListRow(RequiredModsSession.NameOf(i.Id), RequiredMods.IssueText(i), badge, value, fixable,
                RequiredMods.IssueText(i), false, out _);
        }
        if (req.More > 0)
            SimpleMenu.Note($"The server runs {req.More} more server mod{(req.More == 1 ? "" : "s")} it couldn't list.");

        bool needList = plan.MissingUnlisted.Count > 0 || plan.Blocking.Count > 0 || plan.PrefabBlocking.Count > 0;
        if (needList && !AvailableTab.ListReady)
        {
            if (AvailableTab.ListChecking) SimpleMenu.Note("Checking the list of mods you can download...");
            else if (AvailableTab.ListError != null) Menu.Banner("Couldn't get the list of mods: " + AvailableTab.ListError, Theme.Warn);
        }
        else if (plan.MissingUnlisted.Count > 0)
            SimpleMenu.Note(Names(plan.MissingUnlisted) + (plan.MissingUnlisted.Count == 1 ? " isn't" : " aren't") +
                            " on the AVAILABLE tab: get it from whoever made it, or join without it.");

        if (plan.CanFix)
        {
            var downloads = Downloads(plan, out long total, out string files);
            if (plan.Install.Count > 0)
            {
                SimpleMenu.Note("Download " + files + " from GitHub? " +
                                "They are checked (size and SHA-256) before they are used, and take effect after you restart the game.");
                SimpleMenu.Button("Install & join", ModVersion.FormatSize(total),
                    "Downloads them" + (plan.SwitchOn.Count > 0 ? ", switches on " + Names(plan.SwitchOn) : "") +
                    (plan.SwitchOff.Count > 0 ? ", switches off " + Names(plan.SwitchOff) : "") +
                    ", then rejoins this server after you restart the game.",
                    () => InstallAndJoin(plan, downloads), !AvailableTab.Busy);
            }
            else
            {
                string label = plan.SwitchOn.Count > 0 && plan.SwitchOff.Count > 0 ? "Switch on and off & restart"
                    : plan.SwitchOff.Count > 0 ? "Switch off & restart" : "Switch on & restart";
                var all = new List<string>(plan.SwitchOn);
                all.AddRange(plan.SwitchOff);
                string what = (plan.SwitchOn.Count > 0 ? "Switches on " + Names(plan.SwitchOn) : "") +
                              (plan.SwitchOn.Count > 0 && plan.SwitchOff.Count > 0 ? " and switches off " : plan.SwitchOff.Count > 0 ? "Switches off " : "") +
                              (plan.SwitchOff.Count > 0 ? Names(plan.SwitchOff) : "");
                SimpleMenu.Button(label, Names(all), what + " (the change takes effect when the game starts), then rejoins this server after you restart.",
                    () => SwitchAndRestart(plan));
            }
        }
        if (plan.CanJoinNow && req.Join != null)
            SimpleMenu.Button(plan.WarningsOnly ? "Join anyway" : "Join without them", null,
                plan.WarningsOnly ? "Joins the server." : "Joins the server; these mods' features are off for you there.", JoinNow);
        SimpleMenu.Button("Cancel", null, "Doesn't join.", Close);
        return true;
    }

    private static List<ManifestEntry> Downloads(JoinPlan plan, out long total, out string files)
    {
        var list = new List<ManifestEntry>();
        var parts = new List<string>();
        total = 0;
        foreach (string id in plan.Install)
        {
            var e = AvailableTab.Listed(id);
            if (e == null) continue;
            list.Add(e);
            parts.Add($"{e.File} ({ModVersion.FormatSize(e.Size)})");
            total += e.Size;
        }
        files = RequiredMods.JoinAnd(parts);
        return list;
    }

    private static void InstallAndJoin(JoinPlan plan, List<ManifestEntry> downloads)
    {
        var req = _req;
        if (req == null) return;
        _step = Step.Downloading;
        bool started = AvailableTab.InstallMany(downloads, error =>
        {
            if (_req != req) return; // replaced meanwhile
            if (error != null)
            {
                _error = error;
                _step = Step.Failed;
                if (_hidden) Finish("Failed: " + error); // nothing was switched on
                return;
            }
            // Only now, so a cancelled or failed download leaves the switches as they were. An update of a
            // switched-off mod would still be off after the restart: switch those on too.
            foreach (string id in plan.Install)
                if (ModManager.Find(id) != null && !ModManager.SwitchedOn(id)) ModManager.SetEnabled(id, true);
            foreach (string id in plan.SwitchOn) ModManager.SetEnabled(id, true);
            foreach (string id in plan.SwitchOff) ModManager.SetEnabled(id, false);
            Prepare(req);
            if (_hidden) Finish($"The mods for {req.ServerName ?? "the server"} are downloaded: restart the game to rejoin it.");
        });
        if (!started)
        {
            _error = "another download is running; try again when it has finished";
            _step = Step.Failed;
        }
    }

    private static void SwitchAndRestart(JoinPlan plan)
    {
        if (_req == null) return;
        foreach (string id in plan.SwitchOn) ModManager.SetEnabled(id, true);
        foreach (string id in plan.SwitchOff) ModManager.SetEnabled(id, false);
        Prepare(_req);
    }

    private static void Prepare(Request req)
    {
        try { req.PrepareRejoin?.Invoke(); }
        catch (Exception e) { DebugLog.Error("core", e, "RequiredModsPrompt.Prepare"); Core.CorePlugin.Logger?.LogWarning("[ModTypes] could not remember the server to rejoin: " + e.Message); }
        _step = Step.Restart;
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
    }

    /// <summary>Hides the prompt while its downloads run; the tabs show again.</summary>
    private static void Hide()
    {
        _hidden = true;
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
    }

    /// <summary>A hidden prompt's downloads ended: say so in the status line, and the prompt is done.</summary>
    private static void Finish(string status)
    {
        Menu.SetStatus(status);
        _req = null;
        _hidden = false;
    }

    private static void JoinNow()
    {
        var join = _req?.Join;
        Close();
        Menu.CloseRequested = true; // give the game's UI back: the join runs in its menus
        try { join?.Invoke(); }
        catch (Exception e) { DebugLog.Error("core", e, "RequiredModsPrompt.JoinNow"); Core.CorePlugin.Logger?.LogWarning("[ModTypes] join failed: " + e.Message); }
    }

    private static void Close()
    {
        Dismiss();
        Menu.CloseRequested = true;
    }

    private static string Names(List<string> ids)
    {
        var names = new List<string>();
        foreach (string id in ids) names.Add(RequiredModsSession.NameOf(id));
        return RequiredMods.JoinAnd(names);
    }
}
