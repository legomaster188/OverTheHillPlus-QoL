using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using OverTheHillPlus.Shared;

namespace OverTheHillPlus.LiveryPacks;

/// <summary>
/// Hands the packs in BepInEx/config/oth.liveries/ to the game's own livery list, as local liveries: right after the
/// game registers its LocalLiveries folder (ModLiveryService.RegisterLocalLiveries, which both its full refresh and
/// the garage's "Refresh" run), each usable pack is registered the same way the game registers a local folder
/// (RegisterFromDirectory with the game's id for that folder name, isLocal = true). So they show in the garage's
/// Livery Mods tab with "Show local mods" on, apply like any mod livery, and are detached and re-read on refresh.
///
/// <para>Only packs that <see cref="LiveryPackScan"/> found safe are handed over. A pack folder that also exists in
/// the game's own LocalLiveries folder is left to the game (same id: it would only be a duplicate).</para>
///
/// <para>Multiplayer: the game sends your equipped livery as its (mod id, sub id). Other players see it only when
/// they have the same pack under the same folder name (here or in their LocalLiveries); otherwise they see your
/// car's normal paint (the game never downloads local liveries).</para>
/// </summary>
internal static class LiveryPacksLoader
{
    public static string Root => Path.Combine(Paths.ConfigPath, "oth.liveries");

    /// <summary>The last scan, for the mod page.</summary>
    public static IReadOnlyList<LiveryPack> Packs { get; private set; } = Array.Empty<LiveryPack>();

    /// <summary>Folder names handed to the game in the last registration.</summary>
    public static readonly HashSet<string> Registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folder names skipped because the game's LocalLiveries folder has a pack of that name.</summary>
    public static readonly HashSet<string> ShadowedByGame = new(StringComparer.OrdinalIgnoreCase);

    public static bool HookSeen { get; private set; }

    public static void EnsureFolder()
    {
        try { Directory.CreateDirectory(Root); }
        catch (Exception e) { DebugLog.Error("liverypacks", e, "LiveryPacksLoader.EnsureFolder"); LiveryPacksPlugin.Logger.LogWarning("[Livery Packs] Could not create " + Root + ": " + e.Message); }
    }

    /// <summary>A scan has run at least once (the page scans on demand, never every frame).</summary>
    public static bool Scanned { get; private set; }

    public static void Rescan()
    {
        // Modded cars' skins come from the same folders: they are read again too.
        if (Scanned) { try { Api.Cars.Rescan(); } catch (Exception ex) { DebugLog.Error("liverypacks", ex, "LiveryPacksLoader.Rescan"); } }
        Scanned = true;
        try
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Packs = LiveryPackScan.ScanRoot(Root);
            ReportScan(Packs, (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
        catch (Exception e)
        {
            DebugLog.Error("liverypacks", e, "LiveryPacksLoader.Rescan");
            LiveryPacksPlugin.Logger.LogWarning("[Livery Packs] Scan failed: " + e.Message);
            Packs = Array.Empty<LiveryPack>();
        }
    }

    /// <summary>The scan's result in the debug log: the totals at Info, each pack's problem and warnings at Verbose.</summary>
    private static void ReportScan(IReadOnlyList<LiveryPack> packs, double ms)
    {
        int usable = 0, refused = 0, liveries = 0;
        foreach (var p in packs)
        {
            if (p.Usable) usable++;
            if (p.Problem != null) refused++;
            liveries += p.Loadable;
        }
        DebugLog.Info(LiveryPacksPlugin.ModId, $"pack scan of {Root}: {packs.Count} pack(s), {usable} usable, {refused} refused, {liveries} loadable livery(ies), {ms:0.0} ms");
        if (!DebugLog.IsVerbose) return;
        foreach (var p in packs)
        {
            if (p.Problem != null) DebugLog.Verbose(LiveryPacksPlugin.ModId, $"pack '{p.Folder}' refused: {p.Problem}");
            else DebugLog.Verbose(LiveryPacksPlugin.ModId, $"pack '{p.Folder}': {p.Loadable} of {p.Entries.Count} livery(ies) loadable" + (p.Warnings.Count > 0 ? ", warnings: " + string.Join("; ", p.Warnings) : ""));
            foreach (var e in p.Entries)
                if (!e.Loadable) DebugLog.Verbose(LiveryPacksPlugin.ModId, $"  in {p.Folder}: livery {e.Name} for {e.Car} is not loadable (texture {e.Texture} not found in the pack)");
        }
    }

    /// <summary>Called right after the game registered its own local liveries.</summary>
    internal static void RegisterInto(ModLiveryService service)
    {
        HookSeen = true;
        Registered.Clear();
        ShadowedByGame.Clear();
        Rescan();
        if (service == null || !LiveryPacksConfig.LoadPacks.Value) return;
        var gameFolders = GameLocalFolders();
        int added = 0;
        foreach (var pack in Packs)
        {
            if (!pack.Usable) continue;
            if (gameFolders.Contains(pack.Folder)) { ShadowedByGame.Add(pack.Folder); continue; }
            try
            {
                service.RegisterFromDirectory(pack.Id, pack.Folder, pack.Dir, true);
                Registered.Add(pack.Folder);
                added++;
            }
            catch (Exception e) { DebugLog.Error("liverypacks", e, "LiveryPacksLoader.RegisterInto"); LiveryPacksPlugin.Logger.LogWarning($"[Livery Packs] '{pack.Folder}' could not be added: {e.Message}"); }
        }
        DebugLog.Info(LiveryPacksPlugin.ModId, $"{added} of {Packs.Count} pack(s) added to the game's livery list ({ShadowedByGame.Count} left to the game's own folder of the same name)");
        if (added > 0 || Packs.Count > 0)
            LiveryPacksPlugin.Logger.LogInfo($"[Livery Packs] {added} of {Packs.Count} pack(s) added to the game's livery list.");
    }

    private static HashSet<string> GameLocalFolders()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string dir = ModLiveryService.GetLocalLiveriesDirectory();
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                foreach (string d in Directory.GetDirectories(dir)) set.Add(Path.GetFileName(d));
        }
        catch (Exception ex) { DebugLog.Error("liverypacks", ex, "LiveryPacksLoader.GameLocalFolders"); }
        return set;
    }

    /// <summary>The garage's "Refresh": the game re-reads its local folder, and the postfix adds the packs again.</summary>
    public static string Reload()
    {
        try
        {
            var service = global::ModManager.Instance?.Liveries;
            if (service == null)
            {
                Rescan();
                return "Scanned. The game's livery list isn't ready yet (it starts with the game's mod support).";
            }
            service.RefreshLocalLiveries();
            if (!LiveryPacksConfig.LoadPacks.Value) return "Reloaded: adding packs to the garage is off, so none are in its list.";
            return $"Reloaded: {Registered.Count} pack(s) in the garage's livery list.";
        }
        catch (Exception e)
        {
            DebugLog.Error("liverypacks", e, "LiveryPacksLoader.Reload");
            LiveryPacksPlugin.Logger.LogWarning("[Livery Packs] Reload failed: " + e.Message);
            return "Failed: " + e.Message;
        }
    }

    /// <summary>
    /// The game's "Show local mods" switch in the garage's Livery Mods tab (Profile.ShowLocalLiveryMods, which
    /// StateMerchantMenu.ShowLocalLiveries reads; saved with your profile). Null before a profile is loaded.
    /// </summary>
    public static bool? ShowLocalInGarage
    {
        get
        {
            try
            {
                var profile = OverlandSaver.CurrentProfileSave?.Profile;
                return profile == null ? null : profile.ShowLocalLiveryMods;
            }
            catch (Exception ex) { DebugLog.Error("liverypacks", ex, "LiveryPacksLoader.ShowLocalInGarage"); return null; }
        }
        set
        {
            if (value == null) return;
            try
            {
                var profile = OverlandSaver.CurrentProfileSave?.Profile;
                if (profile != null) profile.ShowLocalLiveryMods = value.Value;
            }
            catch (Exception e) { DebugLog.Error("liverypacks", e, "LiveryPacksLoader.ShowLocalInGarage"); LiveryPacksPlugin.Logger.LogWarning("[Livery Packs] Could not switch local mods: " + e.Message); }
        }
    }
}

[HarmonyPatch(typeof(ModLiveryService), nameof(ModLiveryService.RegisterLocalLiveries))]
internal static class RegisterLocalLiveriesPatch
{
    private static void Postfix(ModLiveryService __instance)
    {
        // A throw here would end the game's own refresh half-way.
        try { LiveryPacksLoader.RegisterInto(__instance); }
        catch (Exception e) { DebugLog.Error("liverypacks", e, "RegisterLocalLiveriesPatch.Postfix"); LiveryPacksPlugin.Logger.LogWarning("[Livery Packs] Adding packs failed: " + e.Message); }
    }
}
