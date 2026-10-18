using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using OverTheHillPlus.Core.Install;
using OverTheHillPlus.UI.Native;
using UnityEngine;

namespace OverTheHillPlus.UI;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The MODS page's AVAILABLE tab and the install / update / uninstall actions: the public mods of the latest GitHub
/// release (Core/Install/Manifest), each with its version, size, description and Install / Update. Every download
/// asks first ("Download &lt;file&gt; (&lt;size&gt;) from GitHub?"). The list is fetched only when this tab opens (if
/// [Mods] CheckForUpdates is on) or when the player presses Check for updates. A mod's page gets Update (when a
/// newer version is listed) and Uninstall; an INSTALLED row shows "update available". Changes take effect after a
/// restart; core's own update after two (it can't replace its own running file, see PendingActions).
/// Private mods never show here: the manifest only lists public mods, and an id matching an installed private mod
/// is skipped as well. While a private mod is installed, core itself is never updated (ruling R12, see
/// Installer.CoreLocked): "Core updates come with your private build".
/// </summary>
internal static class AvailableTab
{
    internal const string TabName = "Available";

    /// <summary>[Mods] CheckForUpdates: fetch the mod list when the AVAILABLE tab opens.</summary>
    internal static ConfigEntry<bool> CheckOnOpen;

    private static string _pluginDir;

    // The mod list.
    private static Manifest _manifest;
    private static string _manifestError;
    private static bool _checking;
    private static float _checkedAt = -1f;
    private static string _checkedClock;
    private static int _lastDrawFrame = -100;
    private const float RecheckSeconds = 600f;

    // Downloads and removals of this session.
    private static bool _busy;
    private static string _busyId;
    private static string _lastError;
    private static readonly HashSet<string> ChangedFiles = new(StringComparer.OrdinalIgnoreCase);
    private static List<PendingAction> _pending = new();

    // The confirmation in front of a download or removal.
    private enum Ask { None, Install, Uninstall }
    private static Ask _ask;
    private static ManifestEntry _askEntry;
    private static List<ManifestEntry> _askPlan;
    private static ModInfo _askMod;
    private static string _askFile;
    private static string _askOwner;
    private static int _askFrame;

    // ---------------------------------------------------------------- setup

    /// <summary>Binds the setting and adds the tab (CorePlugin.Load, after CoreConfig.Bind and PendingActions.ApplyAll).</summary>
    internal static void Register(string pluginDir)
    {
        _pluginDir = pluginDir;
        if (CoreConfig.File != null)
            CheckOnOpen = CoreConfig.File.Bind("Mods", "CheckForUpdates", true,
                "MODS page: fetch the list of mods from GitHub when the AVAILABLE tab opens. Off: only when you press Check for updates. Nothing about you is sent.");
        RefreshPending();
        LogStartupReport(PendingActions.LastReport);
        ModsPage.RegisterTab(TabName, Draw);
    }

    private static void LogStartupReport(ApplyReport r)
    {
        var log = Core.CorePlugin.Logger;
        if (r == null || log == null) return;
        if (r.Applied.Count > 0) log.LogInfo("[Core] applied pending mod changes: " + string.Join(", ", r.Applied));
        if (r.StillQueued.Count > 0) log.LogWarning("[Core] still in use, kept for the next start: " + string.Join(", ", r.StillQueued));
        if (r.Held.Count > 0) log.LogInfo("[Core] waiting for the new Mod Manager, applied at the next start: " + string.Join(", ", r.Held));
        foreach (var e in r.Errors) log.LogWarning("[Core] " + e);
        if (r.Quarantined != null) log.LogWarning("[Core] damaged pending-changes list moved to " + r.Quarantined);
    }

    private static void RefreshPending()
    {
        // Our mods' updates and uninstalls only: renames of other creators' plugins show on the INSTALLED tab.
        if (_pluginDir != null) _pending = PendingActions.Load(_pluginDir).FindAll(a => !a.IsRename);
    }

    private static PendingAction PendingFor(string file)
    {
        if (file == null) return null;
        foreach (var a in _pending)
            if (PendingActions.SameFile(a.File, file)) return a;
        return null;
    }

    private static bool CoreUpdateQueued => PendingFor(Installer.CoreFile)?.Kind == PendingKind.Replace;

    /// <summary>A private mod is registered: core is not updated from the release (Installer.CoreLocked).</summary>
    private static bool CoreLocked => Installer.CoreLocked(ModManager.All);

    // ---------------------------------------------------------------- what other pages ask

    /// <summary>The listed newer version of an installed public mod, or null (nothing listed, up to date, not checked yet).</summary>
    internal static ManifestEntry UpdateFor(ModInfo m)
    {
        if (m == null || m.Private || _manifest == null || ThirdParty.IsApiMod(m.Id)) return null; // another creator's mod: not ours to update
        if (ModPageView.IsCore(m) && CoreLocked) return null;
        var e = _manifest.Find(m.Id);
        return e != null && ModVersion.IsNewer(e.Version, m.Version) ? e : null;
    }

    /// <summary>Extra words for a mod's INSTALLED row ("update available", "removed after restart"), or null.</summary>
    internal static string InstalledMark(ModInfo m)
    {
        if (m == null) return null;
        var pending = PendingFor(FileFor(m));
        if (pending?.Kind == PendingKind.Delete) return "<color=#F2B33D>removed after restart</color>";
        if (pending?.Kind == PendingKind.Replace) return "<color=#4CC38A>update after restart</color>";
        if (ModPageView.IsCore(m) && CoreLocked) return "<color=#8A8F98>" + Installer.CoreFromPrivateBuild.TrimEnd('.') + "</color>";
        var e = UpdateFor(m);
        return e == null ? null : $"<color=#4CC38A>update available: v{e.Version}</color>";
    }

    private static readonly Dictionary<string, string> LoadedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The DLL file of an installed mod in the plugin folder (where BepInEx loaded it from), or null when it
    /// isn't there (core itself, or a mod installed somewhere else).</summary>
    internal static string FileFor(ModInfo m)
    {
        if (m == null || _pluginDir == null) return null;
        if (!LoadedFiles.TryGetValue(m.Id, out var file))
        {
            file = null;
            try
            {
                var plugins = IL2CPPChainloader.Instance?.Plugins;
                if (plugins != null && plugins.TryGetValue("com.oth." + m.Id, out var info) && !string.IsNullOrEmpty(info.Location))
                {
                    string full = Path.GetFullPath(info.Location);
                    if (string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(_pluginDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        file = Path.GetFileName(full);
                }
            }
            catch (Exception e) { DebugLog.Error("core", e, "AvailableTab.FileFor"); Core.CorePlugin.Logger?.LogDebug("Mod file lookup: " + e.Message); }
            LoadedFiles[m.Id] = file;
        }
        if (file != null) return file;
        var listed = _manifest?.Find(m.Id);
        return listed != null && File.Exists(Path.Combine(_pluginDir, listed.File)) ? listed.File : null;
    }

    // ---------------------------------------------------------------- the tab

    private static void Draw()
    {
        bool opened = Time.frameCount - _lastDrawFrame > 2;
        _lastDrawFrame = Time.frameCount;
        if (opened)
        {
            RefreshPending();
            bool stale = _checkedAt < 0f || Time.unscaledTime - _checkedAt > RecheckSeconds;
            if (CheckOnOpen != null && CheckOnOpen.Value && stale) StartCheck();
        }

        if (DrawConfirm("tab")) return;
        DrawNotices();

        string checkedText = _checking ? "Checking..." : _checkedClock != null ? "Checked " + _checkedClock : "";
        SimpleMenu.Button("Check for updates", checkedText,
            "Downloads the list of mods from the latest release on GitHub. Nothing about you is sent.", StartCheck, !_checking && !_busy);
        if (CheckOnOpen != null)
            SimpleMenu.Toggle(CheckOnOpen, "Check when this tab opens", "Off: the list is only fetched when you press Check for updates.");
        if (CoreLocked) SimpleMenu.Note(Installer.CoreFromPrivateBuild);

        if (_manifestError != null) Menu.Banner("Couldn't get the list of mods: " + _manifestError, Theme.Bad);
        if (_manifest == null)
        {
            if (!_checking) SimpleMenu.Note("Press Check for updates to see the mods you can download.");
            return;
        }

        SimpleMenu.Heading(GameMenu.ModListModel.Heading(GameMenu.ModListModel.Group.Available));
        int shown = 0;
        foreach (var e in _manifest.Mods)
        {
            var installed = ModManager.Find(e.Id);
            if (installed != null && installed.Private) continue;
            shown++;
            DrawEntry(e, installed);
        }
        if (shown == 0) SimpleMenu.Note("The release lists no mods.");
        SimpleMenu.Note("Downloads come only from the public OverTheHillPlus-QoL repository on GitHub and are checked (size and SHA-256) before they are used.");
    }

    private static void DrawEntry(ManifestEntry e, ModInfo installed)
    {
        string value, badge = null;
        bool on = true, canPress = !_busy;
        var pending = PendingFor(e.File);
        bool update = installed != null && ModVersion.IsNewer(e.Version, installed.Version);
        bool locked = Installer.IsCore(e.Id, e.File) && CoreLocked;
        if (locked) update = false;
        if (_busy && string.Equals(_busyId, e.Id, StringComparison.OrdinalIgnoreCase)) { value = "Downloading..."; canPress = false; }
        else if (locked && pending == null) { value = "Private build"; on = false; canPress = false; badge = installed != null ? "INSTALLED" : null; }
        else if (pending?.Kind == PendingKind.Delete) { value = "Removed after restart"; on = false; canPress = !_busy; badge = "INSTALLED"; }
        else if (pending?.Kind == PendingKind.Replace || ChangedFiles.Contains(e.File)) { value = "Restart to apply"; canPress = false; badge = installed != null ? "INSTALLED" : null; }
        else if (update) { value = "Update"; badge = "UPDATE"; }
        else if (installed != null) { value = "Installed"; on = false; canPress = false; badge = "INSTALLED"; }
        else value = "Install";

        string detail = (installed != null && update ? $"v{installed.Version} → v{e.Version}" : "v" + e.Version) + "  ·  " + ModVersion.FormatSize(e.Size);
        string hint = string.IsNullOrEmpty(e.Description) ? e.Name : e.Description;
        // A card in the MOD BROWSER: an installed mod's image comes from its DLL, the others get a generated card.
        var card = new GameMenu.CardInfo
        {
            ModId = installed?.Id, Seed = e.Id, Title = e.Name, Author = GameMenu.ModCards.OurAuthor,
            Version = e.Version, Side = installed != null ? RequiredMods.SideBadge(installed.Side) : "",
            Status = GameMenu.ModCards.AvailableStatus(value), Size = ModVersion.FormatSize(e.Size),
        };
        bool pressed = SimpleMenu.ListRow(e.Name, detail, badge, value, on, hint, false, out _, card);
        // Cards show the description in the details panel (a line under each card would split the grid).
        if (!string.IsNullOrEmpty(e.Description) && !GameMenu.GameModsTab.ShowsCards) SimpleMenu.Note(e.Description);
        if (!pressed) return;
        if (pending?.Kind == PendingKind.Delete && !_busy)
        {
            // Pressing a mod that is about to be removed keeps it.
            UndoUninstall(e.File, e.Name);
            return;
        }
        if (canPress) BeginInstall(e);
        else if (locked) Menu.SetStatus(Installer.CoreFromPrivateBuild);
        else if (installed != null && !update) Menu.SetStatus($"{e.Name} v{installed.Version} is up to date.");
    }

    /// <summary>Restart notes, the result of the last download, and what happened to queued changes at startup.</summary>
    private static void DrawNotices()
    {
        if (_lastError != null) Menu.Banner("Failed: " + _lastError, Theme.Bad);
        var r = PendingActions.LastReport;
        if (r != null)
        {
            if (r.Quarantined != null)
                Menu.Banner("The list of pending mod changes was damaged and was set aside (" + Path.GetFileName(r.Quarantined) + "); nothing from it was applied.", Theme.Warn);
            for (int i = 0; i < r.Errors.Count && i < 3; i++) Menu.Banner(r.Errors[i], Theme.Warn);
            var still = new List<string>();
            foreach (var f in r.StillQueued)
                if (PendingFor(f) != null) still.Add(f);
            if (still.Count > 0)
                Menu.Banner($"Still waiting: {string.Join(", ", still)} was in use when the game started. It is changed at the next start.", Theme.Warn);
            if (r.Swapped.Contains(Installer.CoreFile) && !CoreUpdateQueued)
                Menu.Banner(r.Held.Count > 0
                    ? $"Restart the game once more to finish updating the Mod Manager. {string.Join(", ", r.Held)} {(r.Held.Count == 1 ? "is" : "are")} updated then too, with the new Mod Manager."
                    : "Restart the game once more to finish updating the Mod Manager.", Theme.Warn);
            else if (r.Held.Count > 0 && !CoreUpdateQueued)
                Menu.Banner($"{string.Join(", ", r.Held)} waits for the new Mod Manager: restart the game once more to apply it.", Theme.Warn);
        }
        if (CoreUpdateQueued)
            Menu.Banner(_pending.Count > 1
                ? "Restart the game twice to finish updating the Mod Manager. The other downloaded mods wait for it and apply after the second restart."
                : "Restart the game twice to finish updating the Mod Manager.", Theme.Warn);
        else if (ChangedFiles.Count > 0 || _pending.Count > 0) Menu.Banner("Restart the game to apply the changes.", Theme.Good);
    }

    // ---------------------------------------------------------------- a mod's page

    /// <summary>
    /// A mod's page: Update (when a newer version is listed) and Uninstall, or the confirmation for one of them.
    /// True while that confirmation is shown, so the page draws nothing else under it.
    /// </summary>
    internal static bool DrawModActions(ModInfo m)
    {
        if (m == null) return false;
        if (DrawConfirm("mod/" + m.Id)) return true;
        bool core = ModPageView.IsCore(m);
        string file = core ? Installer.CoreFile : FileFor(m);
        var pending = PendingFor(file);
        if (pending?.Kind == PendingKind.Delete)
        {
            Menu.Banner("This mod is removed when you restart the game.", Theme.Warn);
            SimpleMenu.Button("Keep it", null, "Cancels the uninstall.", () => UndoUninstall(file, m.Name ?? m.Id), !_busy);
            return false;
        }
        if (pending?.Kind == PendingKind.Replace || file != null && ChangedFiles.Contains(file))
            Menu.Banner(core ? "Restart the game twice to finish updating the Mod Manager."
                : CoreUpdateQueued ? "The update is downloaded. It waits for the Mod Manager's update: restart the game twice to apply it."
                : "The update is downloaded. Restart the game to apply it.", Theme.Good);
        else if (core && CoreLocked) SimpleMenu.Note(Installer.CoreFromPrivateBuild);
        else
        {
            var update = UpdateFor(m);
            if (update != null)
                SimpleMenu.Button("Update", $"v{m.Version} → v{update.Version}", $"Downloads {update.File} ({ModVersion.FormatSize(update.Size)}) from GitHub.",
                    () => BeginInstall(update), !_busy);
        }
        if (!core && file != null)
            SimpleMenu.Button("Uninstall", file, "Removes this mod. It goes when you restart the game.", () => BeginUninstall(m, file), !_busy);
        return false;
    }

    // ---------------------------------------------------------------- confirmation

    private static void BeginInstall(ManifestEntry e)
    {
        var plan = NewInstaller().PlanFor(e, out string error);
        if (plan == null)
        {
            _lastError = error;
            Menu.SetStatus("Failed: " + error);
            return;
        }
        OpenAsk(Ask.Install);
        _askEntry = e;
        _askPlan = plan;
    }

    private static void BeginUninstall(ModInfo m, string file)
    {
        OpenAsk(Ask.Uninstall);
        _askMod = m;
        _askFile = file;
    }

    private static void OpenAsk(Ask ask)
    {
        _ask = ask;
        _askOwner = null; // taken by the first page that draws it
        _askFrame = Time.frameCount;
        _askEntry = null;
        _askPlan = null;
        _askMod = null;
        _askFile = null;
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
    }

    private static void CloseAsk()
    {
        _ask = Ask.None;
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
    }

    /// <summary>Draws the open confirmation on the page that asked for it. A confirmation left behind (the player
    /// went to another page) is dropped.</summary>
    private static bool DrawConfirm(string owner)
    {
        if (_ask == Ask.None) return false;
        if (_askOwner != null && Time.frameCount - _askFrame > 2) { _ask = Ask.None; return false; }
        _askOwner ??= owner;
        if (_askOwner != owner) return false;
        _askFrame = Time.frameCount;

        if (_ask == Ask.Install)
        {
            var parts = new List<string>();
            long total = 0;
            foreach (var p in _askPlan)
            {
                parts.Add($"{p.File} ({ModVersion.FormatSize(p.Size)})");
                total += p.Size;
            }
            SimpleMenu.Heading(_askPlan.Count > 1 ? "Download with the mods it needs" : "Download");
            Menu.Banner("Download " + JoinAnd(parts) + " from GitHub?", Theme.Info);
            if (string.Equals(_askEntry.Id, Installer.CoreId, StringComparison.OrdinalIgnoreCase)) SimpleMenu.Note("The Mod Manager can't replace itself while it runs: restart the game twice to finish the update.");
            else if (CoreUpdateQueued) SimpleMenu.Note("It is checked (size and SHA-256) before it is used. The Mod Manager's update is waiting, so this one waits for it: it takes effect after you restart the game twice.");
            else SimpleMenu.Note("It is checked (size and SHA-256) before it is used, and takes effect after you restart the game.");
            var entry = _askEntry;
            SimpleMenu.Button("Download", ModVersion.FormatSize(total), "Downloads and installs it.", () => { CloseAsk(); StartInstall(entry); }, !_busy);
            SimpleMenu.Button("Cancel", null, "Nothing is downloaded.", CloseAsk);
            return true;
        }

        string name = _askMod?.Name ?? _askMod?.Id ?? _askFile;
        SimpleMenu.Heading("Uninstall");
        Menu.Banner($"Remove {name} ({_askFile})? It is removed when you restart the game.", Theme.Warn);
        var needed = NeededBy(_askMod?.Id);
        if (needed.Count > 0) SimpleMenu.Note("These mods need it and stop working without it: " + string.Join(", ", needed) + ".");
        SimpleMenu.Note("Its settings file stays, so reinstalling it brings them back.");
        var mod = _askMod;
        string file = _askFile;
        SimpleMenu.Button("Uninstall", file, "Removes the mod's DLL.", () => { CloseAsk(); Uninstall(mod, file); }, !_busy);
        SimpleMenu.Button("Cancel", null, "Nothing is removed.", CloseAsk);
        return true;
    }

    /// <summary>Installed mods that list <paramref name="id"/> in "requires" (known once the mod list was fetched).</summary>
    private static List<string> NeededBy(string id)
    {
        var names = new List<string>();
        if (id == null || _manifest == null) return names;
        foreach (var e in _manifest.Mods)
        {
            var m = ModManager.Find(e.Id);
            if (m == null) continue;
            foreach (var r in e.Requires)
                if (string.Equals(r, id, StringComparison.OrdinalIgnoreCase)) { names.Add(m.Name ?? m.Id); break; }
        }
        return names;
    }

    private static string JoinAnd(List<string> parts) =>
        parts.Count <= 1 ? (parts.Count == 1 ? parts[0] : "") : string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " and " + parts[parts.Count - 1];

    // ---------------------------------------------------------------- work

    private static Installer NewInstaller()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ModManager.All) ids.Add(m.Id);
        string dir = _pluginDir;
        var manifest = _manifest;
        return new Installer(HttpFetch.Get, dir)
        {
            Manifest = manifest,
            PrivateModsInstalled = CoreLocked, // ruling R12: core is never replaced under the private mods
            PrivateFiles = PrivateModFiles(), // a release never overwrites a private mod's DLL
            // Registered (switched off counts), or downloaded this session and waiting for a restart.
            IsInstalled = id => ids.Contains(id) || manifest?.Find(id) is { } listed && File.Exists(Path.Combine(dir, listed.File)),
        };
    }

    /// <summary>The DLL file names the registered private mods were loaded from (wherever they are installed).</summary>
    private static List<string> PrivateModFiles()
    {
        var files = new List<string>();
        foreach (var m in ModManager.All)
        {
            if (m == null || !m.Private) continue;
            try
            {
                var plugins = IL2CPPChainloader.Instance?.Plugins;
                if (plugins != null && plugins.TryGetValue("com.oth." + m.Id, out var info) && !string.IsNullOrEmpty(info.Location))
                    files.Add(Path.GetFileName(info.Location));
            }
            catch (Exception e) { DebugLog.Error("core", e, "AvailableTab.PrivateModFiles"); Core.CorePlugin.Logger?.LogDebug("Private mod file lookup: " + e.Message); }
        }
        return files;
    }

    private static void StartCheck()
    {
        if (_checking) return;
        _checking = true;
        _manifestError = null;
        Background.Run(FetchManifest, (m, e) => CheckDone(m, e == null ? null : e is ManifestException ? e.Message : HttpFetch.Describe(e)));
    }

    /// <summary>Worker thread: downloads and parses mods.json.</summary>
    private static async Task<Manifest> FetchManifest()
    {
        var bytes = await HttpFetch.Get(Manifest.Url).ConfigureAwait(false);
        return Manifest.Parse(Encoding.UTF8.GetString(bytes));
    }

    private static void CheckDone(Manifest m, string error)
    {
        _checking = false;
        _checkedAt = Time.unscaledTime;
        _checkedClock = DateTime.Now.ToString("HH:mm");
        if (m != null)
        {
            _manifest = m;
            ThirdPartyScan.NoteListedIds(m.Mods.ConvertAll(e => e.Id)); // other creators can't take our ids
        }
        _manifestError = error;
        if (error != null) Core.CorePlugin.Logger?.LogWarning("[Core] mod list: " + error);
        else
        {
            int updates = 0;
            foreach (var mod in ModManager.All)
                if (UpdateFor(mod) != null) updates++;
            Core.CorePlugin.Logger?.LogInfo($"[Core] mod list: {m.Mods.Count} mods, {updates} update(s) for installed mods");
        }
    }

    private static void StartInstall(ManifestEntry e)
    {
        if (_busy || e == null) return;
        _busy = true;
        _busyId = e.Id;
        _lastError = null;
        var installer = NewInstaller();
        Background.Run(() => installer.Install(e), (r, ex) => InstallDone(e, r ?? InstallResult.Fail(ex?.Message ?? "unknown error")));
    }

    private static void InstallDone(ManifestEntry e, InstallResult r)
    {
        _busy = false;
        _busyId = null;
        foreach (var f in r.Placed) ChangedFiles.Add(f);
        foreach (var f in r.Queued) ChangedFiles.Add(f);
        RefreshPending();
        // A join prompt's downloads (RequiredModsPrompt) carry on with the next one, or report.
        var then = _then;
        _then = null;
        if (!r.Ok)
        {
            _lastError = r.Error;
            Menu.SetStatus("Failed: " + r.Error);
            Core.CorePlugin.Logger?.LogWarning("[Core] install of " + e.Id + " failed: " + r.Error);
            then?.Invoke(r);
            return;
        }
        Core.CorePlugin.Logger?.LogInfo($"[Core] downloaded {string.Join(", ", r.Ids)} (placed: {r.Placed.Count}, queued: {r.Queued.Count})");
        Menu.SetStatus(r.Queued.Contains(Installer.CoreFile)
            ? $"{e.Name} v{e.Version} downloaded. Restart the game twice to finish updating the Mod Manager."
            : CoreUpdateQueued
                ? $"{e.Name} v{e.Version} downloaded. It waits for the Mod Manager's update: restart the game twice to apply."
                : $"{e.Name} v{e.Version} downloaded. Restart the game to apply.");
        then?.Invoke(r);
    }

    // ---------------------------------------------------------------- the join prompt (RequiredModsPrompt)

    private static Action<InstallResult> _then;

    /// <summary>Fetches the mod list if it hasn't been yet (and isn't being fetched).</summary>
    internal static void EnsureList()
    {
        if (_manifest == null && !_checking) StartCheck();
    }

    internal static bool ListChecking => _checking;
    internal static string ListError => _manifest == null ? _manifestError : null;
    internal static bool ListReady => _manifest != null;
    internal static bool Busy => _busy;

    /// <summary>The mod's entry in the fetched mod list, or null (not listed, or the list isn't fetched yet).</summary>
    internal static ManifestEntry Listed(string id) => string.IsNullOrEmpty(id) ? null : _manifest?.Find(id);

    /// <summary>The listed name of a mod, or null.</summary>
    internal static string ListedName(string id) => Listed(id)?.Name;

    /// <summary>True when this entry's file was already downloaded this session (it applies at the next start).</summary>
    internal static bool Downloaded(ManifestEntry e)
    {
        if (e == null) return false;
        RefreshPending();
        return ChangedFiles.Contains(e.File) || PendingFor(e.File)?.Kind == PendingKind.Replace;
    }

    /// <summary>
    /// Downloads <paramref name="entries"/> one after another with the AVAILABLE tab's installer (each with the mods it
    /// requires, checked as usual). <paramref name="done"/> gets null when all of them were placed or queued, or the
    /// first error. False when a download is already running. The caller asked for the downloads first.
    /// </summary>
    internal static bool InstallMany(List<ManifestEntry> entries, Action<string> done)
    {
        if (_busy || entries == null) return false;
        var queue = new Queue<ManifestEntry>(entries);
        void Next(InstallResult previous)
        {
            if (previous != null && !previous.Ok)
            {
                _lastError = previous.Error;
                done?.Invoke(previous.Error ?? "download failed");
                return;
            }
            while (queue.Count > 0 && Downloaded(queue.Peek())) queue.Dequeue();
            if (queue.Count == 0)
            {
                done?.Invoke(null);
                return;
            }
            var e = queue.Dequeue();
            _then = Next;
            StartInstall(e);
        }
        Next(null);
        return true;
    }

    private static void Uninstall(ModInfo m, string file)
    {
        if (_busy || file == null) return;
        _lastError = null;
        var r = NewInstaller().Uninstall(file);
        RefreshPending();
        string name = m?.Name ?? m?.Id ?? file;
        if (!r.Ok)
        {
            _lastError = r.Error;
            Menu.SetStatus("Failed: " + r.Error);
            return;
        }
        // Removed now (it was installed this session and never loaded): it can be installed again straight away.
        foreach (var f in r.Removed) ChangedFiles.Remove(f);
        Core.CorePlugin.Logger?.LogInfo($"[Core] uninstall {file}: {(r.Queued.Count > 0 ? "queued for the next start" : "removed")}");
        Menu.SetStatus($"{name} is removed when you restart the game.");
    }

    private static void UndoUninstall(string file, string name)
    {
        if (file == null || _pluginDir == null) return;
        try { PendingActions.Remove(_pluginDir, file); }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "AvailableTab.UndoUninstall");
            _lastError = e.Message;
            Menu.SetStatus("Failed: " + e.Message);
            return;
        }
        RefreshPending();
        Menu.SetStatus($"{name} stays installed.");
    }
}
