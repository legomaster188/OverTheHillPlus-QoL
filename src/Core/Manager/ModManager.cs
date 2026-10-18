using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;

namespace OverTheHillPlus.Core.Manager;

/// <summary>Registry of the installed mods: who is loaded, which are switched on, and each mod's own config file.</summary>
internal static class ModManager
{
    private static readonly List<ModInfo> Mods = new();
    private static readonly Dictionary<string, ConfigFile> Configs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly FailureLog Failures = new();
    private static EnabledStore _enabled;

    public static IReadOnlyList<ModInfo> All => Mods;

    private static EnabledStore Enabled =>
        _enabled ??= new EnabledStore(Path.Combine(Paths.ConfigPath, "oth.core.enabled.txt"));

    /// <summary>The assembly that registered each mod (its card image, card.png, is an embedded resource of it).</summary>
    private static readonly Dictionary<string, Assembly> Sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a mod to the list (from the calling plugin's assembly). A second registration of the same id replaces the first.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Register(ModInfo info) => Register(info, Assembly.GetCallingAssembly());

    /// <summary>Adds a mod registered by <paramref name="source"/> (the API passes its caller's assembly).</summary>
    internal static void Register(ModInfo info, Assembly source)
    {
        if (info == null || string.IsNullOrEmpty(info.Id)) return;
        Mods.RemoveAll(m => string.Equals(m.Id, info.Id, StringComparison.OrdinalIgnoreCase));
        Mods.Add(info);
        DebugLog.Info(info.Id, $"registered {info.Name} {info.Version} ({info.Side}{(info.Private ? ", private" : "")}) from {source?.GetName().Name ?? "?"}");
        if (source != null) Sources[info.Id] = source;
        else Sources.Remove(info.Id);
        // Settings migrated from OverTheHillPlus while this mod wasn't installed: hand them over before it binds.
        try { ConfigMigration.ApplyPending(Paths.ConfigPath, info.Id); }
        catch (Exception e) { CorePlugin.Logger?.LogError($"[{info.Id}] could not take its migrated settings: {e.Message}"); }
    }

    /// <summary>The assembly that registered mod <paramref name="id"/>, or null.</summary>
    public static Assembly SourceOf(string id) => id != null && Sources.TryGetValue(id, out var a) ? a : null;

    /// <summary>The registered mod with this id, or null.</summary>
    public static ModInfo Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var m in Mods)
            if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    /// <summary>
    /// True unless the player switched this mod off, or the old OverTheHillPlus plugin is loaded (<see cref="Blocked"/>).
    /// Read once, by each mod's plugin, when the game starts.
    /// </summary>
    public static bool IsEnabled(string id)
    {
        bool on = !Blocked && Enabled.Get(id);
        if (EnabledLogged.Add(id ?? "?"))
            DebugLog.Info(id ?? "?", on ? "enabled: loading" : Blocked ? "off: blocked by the old plugin" : "off: switched off on the MODS page, nothing of it runs");
        return on;
    }

    private static readonly HashSet<string> EnabledLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mod's ON/OFF switch as the player set it (the MODS page's row), even while <see cref="Blocked"/>.</summary>
    public static bool SwitchedOn(string id) => Enabled.Get(id);

    /// <summary>
    /// True when the old OverTheHillPlus plugin is loaded too: every mod stays off this session (its features would run
    /// twice) and the MODS page shows <see cref="BlockedText"/>. Core still runs, to show that page.
    /// </summary>
    public static bool Blocked { get; private set; }

    /// <summary>The MODS page's line while <see cref="Blocked"/>, or null.</summary>
    public static string BlockedText { get; private set; }

    /// <summary>Called by core's Load when <see cref="LegacyDetector"/> finds the old plugin (its DLL file, if known).</summary>
    internal static void Block(string legacyDll)
    {
        Blocked = true;
        BlockedText = LegacyDetector.BlockText(legacyDll);
    }

    /// <summary>
    /// True when the mod is registered and was switched on when the game started, so its Load went past the
    /// <see cref="IsEnabled"/> check and its settings exist. (A switch changed since then waits for a restart.)
    /// </summary>
    public static bool IsLoaded(string id) =>
        Find(id) != null && Enabled.AtStartup(id) && (!Blocked || string.Equals(id, "core", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// For <see cref="ModSide.Server"/> mods: true when the mod's networked part may run right now. That is when it is
    /// loaded and this game is offline, in single-player or hosting, or a guest in a session whose host advertises the
    /// mod at the same major version (session property <see cref="RequiredMods.Key"/>). A guest's Server mod stays off
    /// on a vanilla host, a host without it, or one with another major version. Cheap enough to ask every frame.
    /// A private mod is never advertised: for it this is true whenever it is loaded and the session role is known, and the
    /// mod must only talk to players that run its channel (ModNet.HostHas / PeerHas).
    /// </summary>
    public static bool ServerModActive(string id) => global::OverTheHillPlus.Features.Public.ModTypes.RequiredModsSession.ServerModActive(id);

    private static readonly HashSet<string> ServerNotReady = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For <see cref="ModSide.Server"/> mods that need something before they can serve a session (e.g. a runtime
    /// network prefab): while not ready the mod is left out of a hosted session's advert (othp.req) and
    /// <see cref="ServerModActive"/> is false for it. Ready by default; a hosted session keeps the advert it started
    /// with, so a mod that turns ready mid-session is listed from the next session on.
    /// </summary>
    public static void SetServerModReady(string id, bool ready)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (ready) ServerNotReady.Remove(id);
        else ServerNotReady.Add(id);
    }

    /// <summary>See <see cref="SetServerModReady"/>.</summary>
    public static bool ServerModReady(string id) => !string.IsNullOrEmpty(id) && !ServerNotReady.Contains(id);

    /// <summary>Switches a mod on or off from the next start (the MODS page's ON/OFF row).</summary>
    public static void SetEnabled(string id, bool on)
    {
        if (string.IsNullOrEmpty(id)) return;
        Enabled.Set(id, on);
        DebugLog.Info(id, $"switched {(on ? "on" : "off")} on the MODS page (takes effect after a restart)");
    }

    /// <summary>True when the mod's switch was changed since the game started (shown as "ON/OFF after restart").</summary>
    public static bool PendingRestart(string id) => !string.IsNullOrEmpty(id) && Enabled.PendingRestart(id);

    /// <summary>
    /// Marks a mod whose Load threw (after <see cref="Register"/>): its page shows "Failed to start: ..." with the
    /// first line of <paramref name="error"/>, and the other mods keep running. Call it from the catch around the
    /// plugin's Load body. Only the first failure of a mod is kept.
    /// </summary>
    public static void MarkFailed(string id, string error)
    {
        if (string.IsNullOrEmpty(id)) return;
        Failures.Mark(id, error);
        DebugLog.Error(id, "failed to start: " + ModStatus.FirstLine(error));
        CorePlugin.Logger?.LogError($"[{id}] failed to start: {ModStatus.FirstLine(error)}");
    }

    /// <summary>The first error line of a mod that failed to start, or null.</summary>
    public static string FailedReason(string id) => Failures.Get(id);

    /// <summary>The mod's own BepInEx config file, BepInEx/config/oth.&lt;id&gt;.cfg, created on first use.</summary>
    public static ConfigFile ConfigFor(string id)
    {
        if (!Configs.TryGetValue(id, out var file))
        {
            string path = Path.Combine(Paths.ConfigPath, $"oth.{id}.cfg");
            // The file's text, before the mod binds and saves it: the start-up self-checks look for lines BepInEx could not read.
            Logging.StartupChecks.CaptureConfig(path);
            file = new ConfigFile(path, true);
            // Sliders change values every frame: ConfigAutosave writes tracked files once things are quiet.
            file.SaveOnConfigSet = false;
            Configs[id] = file;
            CoreConfig.Track(file);
        }
        return file;
    }
}
