using System;
using System.Collections.Generic;
using OverTheHillPlus.Core.Install;
using OverTheHillPlus.Features.Public.ModTypes;
using OverTheHillPlus.UI.GameMenu;
using OverTheHillPlus.UI.Native;
using UnityEngine;

namespace OverTheHillPlus.UI;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The "this server needs mods" question, asked when the server browser holds a join (Server Browser+ asks for it with
/// <see cref="Show"/>; core never calls the browser). It shows in the game's own pop-up (<see cref="GameModal"/>: the
/// title, a short text with the server's mods and what happens to each, and a column of the game's buttons); when that
/// pop-up can't be opened, the same content shows as rows on the MODS screen. It lists the server's issues
/// (RequiredMods.Compare) and offers what RequiredMods.PlanJoin allows:
/// <list type="bullet">
/// <item><b>Install &amp; join</b>: downloads the missing mods with the AVAILABLE tab's installer (the question says
///   which files from GitHub first, like every download), switches on the ones that are off, then the browser
///   remembers the server so it is rejoined after the restart (<see cref="Request.PrepareRejoin"/>).</item>
/// <item><b>Switch on &amp; restart</b>: only switched-off mods: switches them on, same rejoin after the restart.</item>
/// <item><b>Join anyway / Join without them</b>: unless another major version blocks the join, and never when a mod
///   with network prefabs (cars, trailers) differs from the host's: the host refuses that join, so the question names
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
        /// <summary>Joins as things are (null: can't). The browser runs the join on its next Update.</summary>
        public Action Join;
        /// <summary>Remembers the server so the browser rejoins it after the restart.</summary>
        public Action PrepareRejoin;
    }

    private enum Step { Ask, Downloading, Restart, Failed }

    /// <summary>What the question shows right now, for both looks (the pop-up and the rows).</summary>
    private sealed class Content
    {
        public string Title;
        public string Lead;
        /// <summary>0 info, 1 warning, 2 bad, 3 good (the rows' banner colour).</summary>
        public int LeadTone;
        public readonly List<ModsScreenModel.Line> Lines = new();
        public readonly List<string> Notes = new();
        public readonly List<(string label, string value, string hint, Action act, bool enabled)> Buttons = new();
    }

    private static Request _req;
    private static Step _step;
    private static string _error;
    private static int _lastDrawFrame = -100;
    private static bool _drawn;
    // Downloading in the background: the question is hidden and comes back when the downloads end.
    private static bool _hidden;
    // Shown in the game's pop-up (else as rows on the MODS screen).
    private static bool _inModal;

    /// <summary>True while a question is waiting.</summary>
    public static bool Active => _req != null;

    /// <summary>Asks (replacing any earlier question): in the game's pop-up, else on the MODS screen.</summary>
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
        Present();
    }

    /// <summary>The pop-up when it can be opened, else the MODS screen (its rows).</summary>
    private static void Present()
    {
        _inModal = true; // before Show: only its callbacks (cancel, failed) turn it off again
        bool shown = GameModal.Show(BuildModal, Dismiss, () =>
        {
            _inModal = false;
            if (_req != null && !_hidden) ModsPage.Open();
        });
        if (!shown)
        {
            _inModal = false;
            ModsPage.Open();
        }
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
    }

    public static void Dismiss()
    {
        if (_req == null) return;
        // Closing the question while downloads run only hides it: they carry on, and it comes back when they end.
        if (_step == Step.Downloading)
        {
            _hidden = true;
            _inModal = false;
            return;
        }
        _req = null;
        _inModal = false;
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
    }

    // ---------------------------------------------------------------- the two looks

    private static GameModal.View BuildModal()
    {
        if (_req == null || _hidden) return null;
        var c = Compose();
        var v = new GameModal.View { Title = c.Title, Body = ModsScreenModel.Body(c.Lead, c.Lines, c.Notes) };
        foreach (var b in c.Buttons)
            v.Add(string.IsNullOrEmpty(b.value) ? b.label : b.label + "  <alpha=#99><size=80%>" + ModsScreenModel.Plain(b.value) + "</size></alpha>", b.act, b.enabled);
        return v;
    }

    /// <summary>The question as rows on the MODS screen (when the pop-up can't be used). True while it is shown (the
    /// page draws nothing else).</summary>
    internal static bool Draw()
    {
        if (_req == null || _hidden || _inModal) return false;
        // Left behind (the page was closed): dropped, unless its downloads are still running.
        if (_drawn && Time.frameCount - _lastDrawFrame > 2 && _step != Step.Downloading)
        {
            _req = null;
            return false;
        }
        _drawn = true;
        _lastDrawFrame = Time.frameCount;

        var c = Compose();
        SimpleMenu.Heading(c.Title);
        if (!string.IsNullOrEmpty(c.Lead))
            Menu.Banner(c.Lead, c.LeadTone switch { 1 => Theme.Warn, 2 => Theme.Bad, 3 => Theme.Good, _ => Theme.Info });
        foreach (var l in c.Lines)
            SimpleMenu.ListRow(l.Name, l.Text, null, l.Tag, l.Tone < 2, l.Text, false, out _);
        foreach (var n in c.Notes) SimpleMenu.Note(n);
        foreach (var b in c.Buttons) SimpleMenu.Button(b.label, b.value, b.hint, b.act, b.enabled);
        return true;
    }

    // ---------------------------------------------------------------- what it says

    private static Content Compose()
    {
        var req = _req;
        var c = new Content();
        string server = string.IsNullOrWhiteSpace(req.ServerName) ? "this server" : req.ServerName;
        c.Title = "Join " + ModsScreenModel.Plain(server);
        switch (_step)
        {
            case Step.Downloading:
                c.Lead = "Getting the mods this server needs...";
                c.Notes.Add("They take effect after you restart the game. Then Server Browser+ rejoins " + ModsScreenModel.Plain(server) + ".");
                c.Buttons.Add(("Hide", null, "The downloads carry on. This comes back when they're done.", Hide, true));
                return c;
            case Step.Restart:
                c.Lead = "All set. Restart the game to finish: once it's up again, Server Browser+ rejoins " + ModsScreenModel.Plain(server) + ".";
                c.LeadTone = 3;
                c.Buttons.Add(("Quit the game", null, "Closes the game. Start it again from Steam and the rejoin starts by itself.", Application.Quit, true));
                c.Buttons.Add(("Later", null, "Restart later: the rejoin waits up to 30 minutes.", Close, true));
                return c;
            case Step.Failed:
                c.Lead = "The download failed: " + _error;
                c.LeadTone = 2;
                c.Buttons.Add(("Back", null, "Back to the list of mods this server needs.", () => _step = Step.Ask, true));
                c.Buttons.Add(("Cancel", null, "Doesn't join.", Close, true));
                return c;
        }

        var plan = RequiredMods.PlanJoin(req.Issues, id => AvailableTab.Listed(id)?.Version);
        string prefab = plan.PrefabIssues ? RequiredMods.PrefabSummary(req.Issues, RequiredModsSession.NameOf) : null;
        if (plan.Blocking.Count > 0)
        {
            c.Lead = "You can't join yet: " + Names(plan.Blocking) + (plan.Blocking.Count == 1 ? " is" : " are") +
                     " another major version than the server's. Install the same version as the server, then join again.";
            c.LeadTone = 2;
        }
        else if (prefab != null)
        {
            c.Lead = prefab + (plan.CanFix ? " Fix it below and the game rejoins after a restart." : " You can't join until they match.");
            c.LeadTone = plan.CanFix ? 1 : 2;
        }
        else if (plan.WarningsOnly)
        {
            c.Lead = "Some of your mods are on another minor version than the server's. That should still work.";
            c.LeadTone = 1;
        }
        else c.Lead = "This server runs mods that everyone in it needs.";

        foreach (var i in req.Issues)
        {
            string tag = i.Kind switch
            {
                RequiredIssueKind.Missing => plan.Install.Contains(i.Id) ? "DOWNLOAD" : i.Prefab ? "MISSING" : "NOT LISTED",
                RequiredIssueKind.Off => "SWITCH ON",
                RequiredIssueKind.VersionMinor => "VERSION",
                RequiredIssueKind.PrefabExtra => "SWITCH OFF",
                RequiredIssueKind.PrefabNotReady => "FAILED",
                RequiredIssueKind.PrefabBuild => plan.Install.Contains(i.Id) ? "DOWNLOAD" : "BUILD",
                _ => plan.Install.Contains(i.Id) ? "DOWNLOAD" : "BLOCKED",
            };
            bool fixable = i.Kind is RequiredIssueKind.Off or RequiredIssueKind.VersionMinor or RequiredIssueKind.PrefabExtra || plan.Install.Contains(i.Id) ||
                           plan.MissingUnlisted.Contains(i.Id);
            c.Lines.Add(new ModsScreenModel.Line(RequiredModsSession.NameOf(i.Id), tag, RequiredMods.IssueText(i), i.Blocks && !plan.Install.Contains(i.Id) ? 2 : fixable ? 1 : 2));
        }
        if (req.More > 0)
            c.Notes.Add($"The server runs {req.More} more server mod{(req.More == 1 ? "" : "s")} it couldn't list.");

        bool needList = plan.MissingUnlisted.Count > 0 || plan.Blocking.Count > 0 || plan.PrefabBlocking.Count > 0;
        if (needList && !AvailableTab.ListReady)
        {
            if (AvailableTab.ListChecking) c.Notes.Add("Checking the list of mods you can download...");
            else if (AvailableTab.ListError != null) c.Notes.Add("Couldn't get the list of mods: " + AvailableTab.ListError);
        }
        else if (plan.MissingUnlisted.Count > 0)
            c.Notes.Add(Names(plan.MissingUnlisted) + (plan.MissingUnlisted.Count == 1 ? " isn't" : " aren't") +
                        " in Get more mods: get it from whoever made it, or join without it.");

        if (plan.CanFix)
        {
            var downloads = Downloads(plan, out long total, out string files);
            if (plan.Install.Count > 0)
            {
                c.Notes.Add("Downloads " + files + " from GitHub. They're checked (size and SHA-256) before use and take effect after a restart.");
                c.Buttons.Add(("Install & join", ModVersion.FormatSize(total),
                    "Downloads them" + (plan.SwitchOn.Count > 0 ? ", switches on " + Names(plan.SwitchOn) : "") +
                    (plan.SwitchOff.Count > 0 ? ", switches off " + Names(plan.SwitchOff) : "") +
                    ", then rejoins this server after you restart the game.",
                    () => InstallAndJoin(plan, downloads), !AvailableTab.Busy));
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
                c.Buttons.Add((label, Names(all), what + " (it takes effect when the game starts), then rejoins this server after you restart.",
                    () => SwitchAndRestart(plan), true));
            }
        }
        if (plan.CanJoinNow && req.Join != null)
            c.Buttons.Add((plan.WarningsOnly ? "Join anyway" : "Join without them", null,
                plan.WarningsOnly ? "Joins the server." : "Joins the server; these mods' features are off for you there.", JoinNow, true));
        c.Buttons.Add(("Cancel", null, "Doesn't join.", Close, true));
        return c;
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

    // ---------------------------------------------------------------- the choices

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
                if (Unseen) ComeBack(); // nothing was switched on
                return;
            }
            // Only now, so a cancelled or failed download leaves the switches as they were. An update of a
            // switched-off mod would still be off after the restart: switch those on too.
            foreach (string id in plan.Install)
                if (ModManager.Find(id) != null && !ModManager.SwitchedOn(id)) ModManager.SetEnabled(id, true);
            foreach (string id in plan.SwitchOn) ModManager.SetEnabled(id, true);
            foreach (string id in plan.SwitchOff) ModManager.SetEnabled(id, false);
            Prepare(req);
            if (Unseen) ComeBack();
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

    /// <summary>Hides the question while its downloads run (it comes back when they end).</summary>
    private static void Hide()
    {
        _hidden = true;
        if (_inModal) GameModal.Close();
        _inModal = false;
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
    }

    /// <summary>Nobody sees the question now: hidden, or its rows left behind (the MODS screen was closed meanwhile).</summary>
    private static bool Unseen => _hidden || (!_inModal && Time.frameCount - _lastDrawFrame > 2);

    /// <summary>A hidden question's downloads ended: it shows again with how it went.</summary>
    private static void ComeBack()
    {
        _hidden = false;
        _drawn = false;
        Menu.SetStatus(_step == Step.Failed ? "Failed: " + _error : $"The mods for {_req?.ServerName ?? "the server"} are ready: restart the game to rejoin it.");
        Present();
    }

    private static void JoinNow()
    {
        var join = _req?.Join;
        Close();
        try { join?.Invoke(); }
        catch (Exception e) { DebugLog.Error("core", e, "RequiredModsPrompt.JoinNow"); Core.CorePlugin.Logger?.LogWarning("[ModTypes] join failed: " + e.Message); }
    }

    private static void Close()
    {
        bool modal = _inModal;
        _req = null;
        _inModal = false;
        _hidden = false;
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
        if (modal) GameModal.Close();
        else Menu.CloseRequested = true; // give the game's UI back: the join runs in its menus
    }

    private static string Names(List<string> ids)
    {
        var names = new List<string>();
        foreach (string id in ids) names.Add(RequiredModsSession.NameOf(id));
        return RequiredMods.JoinAnd(names);
    }
}
