using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace OverTheHillPlus.Core.Install;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>What an install, update or uninstall did.</summary>
internal sealed class InstallResult
{
    public bool Ok = true;
    /// <summary>Why it failed (shown on the page); null when it worked.</summary>
    public string Error;
    /// <summary>Files put in place now (they load from the next start).</summary>
    public readonly List<string> Placed = new();
    /// <summary>Files whose replace or delete waits for the next start (they were in use).</summary>
    public readonly List<string> Queued = new();
    /// <summary>Files removed now.</summary>
    public readonly List<string> Removed = new();
    /// <summary>Ids of the mods downloaded, dependencies first.</summary>
    public readonly List<string> Ids = new();

    public static InstallResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// Downloads mods listed in the <see cref="Manifest"/> into the plugin folder (BepInEx/plugins/OverTheHillMods).
/// A download is checked against the manifest's size and SHA-256 in memory before anything is written, so a bad
/// download leaves the folder untouched. It is then written to .pending/&lt;file&gt;.new and moved into place; a DLL
/// that is loaded can't be replaced on Windows, so that one is queued as a <see cref="PendingKind.Replace"/> for the
/// next start. The fetch is injected (tests need no network); in the game it is <see cref="HttpFetch"/>.
/// Pure logic (no BepInEx or Unity types) so it can be unit tested.
/// </summary>
internal sealed class Installer
{
    /// <summary>Core's id and DLL: core can't replace its own running file, so its update finishes over two restarts.</summary>
    public const string CoreId = "core";
    public const string CoreFile = "OTH.Core.dll";

    /// <summary>Why core isn't updated while a private mod is installed (shown on the AVAILABLE tab and core's page).</summary>
    public const string CoreFromPrivateBuild = "Core updates come with your private build.";

    private readonly Func<string, Task<byte[]>> _fetch;
    private readonly string _pluginDir;

    /// <summary>The release's manifest: where required mods are looked up.</summary>
    public Manifest Manifest;
    /// <summary>True when a DLL of this file name is loaded in the game (so it can't be replaced or deleted now).
    /// Default: an assembly of the AppDomain was loaded from a file of that name.</summary>
    public Func<string, bool> IsLoaded = IsLoadedInAppDomain;
    /// <summary>True when the mod with this id is installed (a required mod that is installed isn't downloaded).
    /// Default: its manifest file is in the plugin folder.</summary>
    public Func<string, bool> IsInstalled;
    /// <summary>True while a private mod is registered: core (OTH.Core.dll) is then never replaced or removed, see
    /// <see cref="CoreLocked"/>.</summary>
    public bool PrivateModsInstalled;
    /// <summary>DLL file names of the registered private mods: a manifest entry with one of these files is refused, so a
    /// release can never overwrite a private mod (<see cref="BelongsToPrivateMod"/>).</summary>
    public IEnumerable<string> PrivateFiles;

    /// <summary>Why a private mod's file is never downloaded (shown on the page).</summary>
    public const string PrivateFileRefused = "belongs to a private mod and is never replaced from the release.";

    public Installer(Func<string, Task<byte[]>> fetch, string pluginDir)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _pluginDir = pluginDir ?? throw new ArgumentNullException(nameof(pluginDir));
    }

    public string PluginDir => _pluginDir;

    /// <summary>
    /// Ruling R12: true when any of <paramref name="mods"/> is private. The private mods use core's internals, which only
    /// a privately built core grants them (the release's core is built from the public source), so the installer must
    /// never put the release's OTH.Core.dll over it: core comes with the private build instead.
    /// </summary>
    public static bool CoreLocked(IEnumerable<ModInfo> mods)
    {
        if (mods == null) return false;
        foreach (var m in mods)
            if (m != null && m.Private) return true;
        return false;
    }

    /// <summary>A manifest entry or file that is core: its id is "core" or its file is OTH.Core.dll.</summary>
    public static bool IsCore(string id, string file) =>
        string.Equals(id, CoreId, StringComparison.OrdinalIgnoreCase) || string.Equals(file, CoreFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ruling R12, the pure rule: core is refused while a private mod is installed.</summary>
    public static bool RefusesCore(string id, string file, bool privateModsInstalled) => privateModsInstalled && IsCore(id, file);

    /// <summary>True when <paramref name="file"/> is one of <paramref name="privateFiles"/> (any case).</summary>
    public static bool BelongsToPrivateMod(string file, IEnumerable<string> privateFiles)
    {
        if (string.IsNullOrEmpty(file) || privateFiles == null) return false;
        foreach (var f in privateFiles)
            if (f != null && string.Equals(f, file, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// True while core's own update waits in the queue (a Replace of OTH.Core.dll). Every other download is then queued
    /// too, never put in place: PendingActions applies those only once the new core runs, so a new mod never loads
    /// next to the old core.
    /// </summary>
    public bool CoreUpdateQueued() => PendingActions.Find(_pluginDir, CoreFile)?.Kind == PendingKind.Replace;

    /// <summary>Downloads <paramref name="e"/>, after the mods it requires that aren't installed yet. Never throws.</summary>
    public async Task<InstallResult> Install(ManifestEntry e)
    {
        var order = PlanFor(e, out string missing);
        if (order == null) return InstallResult.Fail(missing);

        var result = new InstallResult();
        foreach (var entry in order)
        {
            string error;
            try { error = await InstallOne(entry, result).ConfigureAwait(false); }
            catch (Exception ex) { DebugLog.Error("core", ex, "Installer.Install"); error = $"{entry.File}: {ex.Message}"; }
            if (error == null) continue;
            result.Ok = false;
            result.Error = error;
            DebugLog.Error("core", $"install of {e.Id} failed at {entry.Id}: {error}");
            return result;
        }
        DebugLog.Info("core", $"install of {e.Id}: {order.Count} mod(s) downloaded ({string.Join(", ", result.Ids)}); placed {result.Placed.Count}, queued for the next start {result.Queued.Count}");
        return result;
    }

    /// <summary>What <see cref="Install"/> would download for <paramref name="e"/>: the required mods that aren't
    /// installed yet, then <paramref name="e"/>. Null (with <paramref name="error"/>) when a requirement is missing.</summary>
    public List<ManifestEntry> PlanFor(ManifestEntry e, out string error)
    {
        error = null;
        if (e == null)
        {
            error = "Nothing to install.";
            return null;
        }
        var order = new List<ManifestEntry>();
        error = Plan(e, order, new HashSet<string>(StringComparer.OrdinalIgnoreCase), true);
        if (error == null)
            foreach (var entry in order)
                if (RefusesCore(entry.Id, entry.File, PrivateModsInstalled)) { error = CoreFromPrivateBuild; break; }
        return error == null ? order : null;
    }

    /// <summary>Depth-first: requirements before the mod itself, each once (a loop in "requires" ends there).
    /// Returns an error when a required mod is neither installed nor in the manifest.</summary>
    private string Plan(ManifestEntry e, List<ManifestEntry> order, HashSet<string> seen, bool asked)
    {
        if (!seen.Add(e.Id)) return null;
        foreach (var id in e.Requires)
        {
            if (seen.Contains(id) || Installed(id)) continue;
            var dep = Manifest?.Find(id);
            if (dep == null) return $"{e.Name} needs \"{id}\", which isn't installed and isn't in the release.";
            string err = Plan(dep, order, seen, false);
            if (err != null) return err;
        }
        if (asked || !Installed(e.Id)) order.Add(e);
        return null;
    }

    private bool Installed(string id)
    {
        // Core runs this installer, so it is there (its own update is asked for explicitly, never pulled in).
        if (string.Equals(id, CoreId, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsInstalled != null) return IsInstalled(id);
        var entry = Manifest?.Find(id);
        return entry != null && File.Exists(Path.Combine(_pluginDir, entry.File));
    }

    /// <summary>Null when it worked, else the message for the page.</summary>
    private async Task<string> InstallOne(ManifestEntry e, InstallResult result)
    {
        if (!Manifest.IsModFileName(e.File)) return $"{e.File} isn't a mod file name (OTH.<name>.dll).";
        if (!Manifest.IsRepoUrl(e.Url)) return $"{e.File}: downloads only come from {Manifest.RepoPrefix}.";
        if (RefusesCore(e.Id, e.File, PrivateModsInstalled)) return CoreFromPrivateBuild;
        if (BelongsToPrivateMod(e.File, PrivateFiles)) return $"{e.File} {PrivateFileRefused}";

        byte[] bytes;
        try { bytes = await _fetch(e.Url).ConfigureAwait(false); }
        catch (Exception ex) { DebugLog.Error("core", ex, "Installer.InstallOne"); return $"Download of {e.File} failed: {Unwrap(ex).Message}"; }
        if (bytes == null) return $"Download of {e.File} failed: no data.";
        if (bytes.LongLength != e.Size)
            return $"Download of {e.File} has the wrong size ({bytes.LongLength} bytes, the release says {e.Size}). Nothing was changed.";
        if (!string.Equals(Sha256Hex(bytes), e.Sha256, StringComparison.OrdinalIgnoreCase))
            return $"Download of {e.File} failed the SHA-256 check. Nothing was changed.";

        // Staged first; the hash is checked again on disk so a bad write never goes into place.
        string staged = PendingActions.StagedPath(_pluginDir, e.File);
        Directory.CreateDirectory(Path.GetDirectoryName(staged));
        try
        {
            File.WriteAllBytes(staged, bytes);
            if (!string.Equals(Sha256Hex(File.ReadAllBytes(staged)), e.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("the file on disk doesn't match the download");
        }
        catch (Exception ex)
        {
            DebugLog.Error("core", ex, "Installer.InstallOne");
            TryDelete(staged);
            return $"Couldn't save {e.File}: {ex.Message}. Nothing was changed.";
        }

        string target = Path.Combine(_pluginDir, e.File);
        result.Ids.Add(e.Id);
        // While core's update is queued, nothing goes in place now (see CoreUpdateQueued).
        if (!IsLoaded(e.File) && !CoreUpdateQueued())
        {
            try
            {
                File.Move(staged, target, true);
                PendingActions.Remove(_pluginDir, e.File); // e.g. an uninstall queued earlier is now moot
                result.Placed.Add(e.File);
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                DebugLog.Error("core", ex, "Installer.InstallOne");
                // In use after all: falls through to the queue.
            }
        }
        PendingActions.Add(_pluginDir, new PendingAction(PendingKind.Replace, e.File));
        result.Queued.Add(e.File);
        return null;
    }

    /// <summary>Removes a mod's DLL: now when it isn't loaded, else queued as a <see cref="PendingKind.Delete"/>
    /// for the next start. Never throws.</summary>
    public InstallResult Uninstall(string file)
    {
        if (!Manifest.IsModFileName(file)) return InstallResult.Fail($"{file} isn't a mod file name (OTH.<name>.dll).");
        if (RefusesCore(null, file, PrivateModsInstalled)) return InstallResult.Fail(CoreFromPrivateBuild);
        var result = new InstallResult();
        try
        {
            TryDelete(PendingActions.StagedPath(_pluginDir, file)); // a downloaded update that would come back
            string target = Path.Combine(_pluginDir, file);
            if (!IsLoaded(file))
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    PendingActions.Remove(_pluginDir, file);
                    result.Removed.Add(file);
                    DebugLog.Info("core", $"uninstall: {file} removed now");
                    return result;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    DebugLog.Error("core", ex, "Installer.Uninstall");
                    // In use after all: queued below.
                }
            }
            PendingActions.Add(_pluginDir, new PendingAction(PendingKind.Delete, file));
            result.Queued.Add(file);
            DebugLog.Info("core", $"uninstall: {file} is in use, its removal is queued for the next start");
            return result;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "Installer.Uninstall"); return InstallResult.Fail($"Couldn't remove {file}: {ex.Message}"); }
    }

    internal static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    /// <summary>An assembly of the AppDomain was loaded from a file of this name.</summary>
    internal static bool IsLoadedInAppDomain(string file)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (a.IsDynamic || string.IsNullOrEmpty(a.Location)) continue;
                if (string.Equals(Path.GetFileName(a.Location), file, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "Installer.IsLoadedInAppDomain"); /* an assembly without a location */ }
        }
        return false;
    }

    private static Exception Unwrap(Exception e)
    {
        while (e is AggregateException { InnerException: not null } || e is TaskCanceledException { InnerException: not null })
            e = e.InnerException;
        return e is TaskCanceledException ? new TimeoutException("timed out") : e;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { DebugLog.Error("core", ex, "Installer.TryDelete"); /* best effort */ }
    }
}
