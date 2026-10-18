using System;
using System.Collections.Generic;
using System.Text;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ModTypes;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Server mods in a live session (the pure part is Core/Manager/RequiredMods):
/// <list type="bullet">
/// <item>Host: adds <see cref="RequiredMods.Key"/> to the session at StartGame, listing the Server mods that run here
///   (Photon only lets a host update keys that existed at creation; the list never changes during a session, since a
///   mod's switch only takes effect after a restart).</item>
/// <item>Guest: reads the host's list, so <see cref="ModManager.ServerModActive"/> can tell a Server mod whether its
///   networked part may run, and shows a one-time notice when the host runs Server mods this game can't run with it
///   (missing, switched off, another major version). Version disagreements are logged.</item>
/// <item>Server browser: <see cref="For"/> reads and caches another session's list for its row and join check.</item>
/// </list>
/// </summary>
internal static class RequiredModsSession
{
    private const float NoticeSeconds = 10f;

    private static IntPtr _runner;
    private static NetworkRunner _sessionRunner;
    private static bool _wasGuest;
    private static RequiredList _advertised;
    private static bool _advertisedFound, _noticeDone, _loggedStartError;
    private static float _nextRead;
    private static string _notice;
    private static float _noticeAt = -100f;
    private static int _roleFrame = -1;
    private static SessionRole _role;
    private static string _hostNotice;

    /// <summary>The value this game added at its last hosted StartGame (null: none was added).</summary>
    public static string LastStartValue { get; private set; }

    /// <summary>The Server mods the last hosted StartGame could not list (guests keep them off).</summary>
    public static IReadOnlyList<string> LastLeftOut { get; private set; } = Array.Empty<string>();

    // ---------------------------------------------------------------- host

    /// <summary>The Server mods running here that the session lists, in advert order (prefab mods, ours, other
    /// creators'): private mods are left out.</summary>
    internal static List<AdvertMod> RunningServerMods()
    {
        // Not ready for sessions yet (see SetServerModReady): left out like a failed mod.
        return RequiredMods.AdvertisedMods(ModManager.All,
            id => ModManager.IsLoaded(id) && ModManager.FailedReason(id) == null && ModManager.ServerModReady(id),
            ThirdParty.IsApiMod);
    }

    /// <summary>The prefab token for this game's registered network prefabs (NetPrefabRegistry's StartGame prefix runs
    /// first, so the set is fixed for the session by now), or null when there are none.</summary>
    private static string PrefabToken()
    {
        string marker = Core.Shared.NetPrefabRegistry.MarkerValue;
        if (marker == null) return null;
        return RequiredMods.PrefabToken(Core.Shared.NetPrefabPlan.MaskOf(Core.Shared.NetPrefabRegistry.RegisteredOwners()), marker);
    }

    /// <summary>Adds the key to a hosted session's start arguments (Host/Server only: never to a join).</summary>
    internal static void OnStartGame(StartGameArgs args)
    {
        LastStartValue = null;
        LastLeftOut = Array.Empty<string>();
        _hostNotice = null;
        try
        {
            var mode = args.GameMode;
            if (mode != GameMode.Host && mode != GameMode.Server) return;
            CrashTrail.Mark("server mods: advert");
            var mods = RunningServerMods();
            string value = RequiredMods.Encode(mods, PrefabToken(), RequiredMods.MaxValueBytes, out var leftOut);
            if (value == null) return; // no Server mods: no key, no bytes used
            var props = args.SessionProperties;
            if (props == null)
            {
                props = new Il2CppSystem.Collections.Generic.Dictionary<string, SessionProperty>();
                args.SessionProperties = props;
            }
            // Photon allows 10 custom properties: a fixed rank among ours decides, whatever order the prefixes run in.
            var present = props;
            if (!SessionSlots.MayAdd(RequiredMods.Key, props.Count, k => present.ContainsKey(k)))
            {
                Core.CorePlugin.Logger?.LogWarning("[ModTypes] No room for the server mods' session property: guests won't see " + value);
                var all = new List<string>();
                foreach (var m in mods) all.Add(m.Id);
                LastLeftOut = all;
                _hostNotice = RequiredMods.LeftOutNotice(all, NameOf);
                return;
            }
            props[RequiredMods.Key] = value;
            LastStartValue = value;
            LastLeftOut = leftOut;
            DebugLog.Info("core", $"hosting ({mode}): the session advertises its server mods ({mods.Count} mod(s), {leftOut.Count} left out, {value.Length} B): {value}");
            Core.CorePlugin.Logger?.LogInfo("[ModTypes] This server advertises its server mods: " + value);
            if (leftOut.Count > 0)
            {
                _hostNotice = RequiredMods.LeftOutNotice(leftOut, NameOf);
                Core.CorePlugin.Logger?.LogWarning("[ModTypes] " + _hostNotice);
            }
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "RequiredModsSession.OnStartGame");
            if (_loggedStartError) return;
            _loggedStartError = true;
            Core.CorePlugin.Logger?.LogWarning("[ModTypes] could not advertise the server mods (further errors suppressed): " + e.Message);
        }
    }

    /// <summary>Bytes the key and value take in this session (0 when absent): the player names publisher gives them up.</summary>
    public static int BytesIn(SessionInfo session)
    {
        try
        {
            string v = GameState.SessionString(session, RequiredMods.Key, out bool found);
            return found ? RequiredMods.Key.Length + Encoding.UTF8.GetByteCount(v ?? "") + 4 : 0;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "RequiredModsSession.BytesIn"); return 0; }
    }

    // ---------------------------------------------------------------- this game's session

    /// <summary>Where this game is right now (worked out once per frame: mods may ask every frame).</summary>
    public static SessionRole Role
    {
        get
        {
            Resolve();
            return _role;
        }
    }

    /// <summary>
    /// The game's runner, read directly (GameState.Runner is refreshed only once a second and only holds running
    /// runners, so for a moment after a join it would still say "no session"). Picked like PlayerListRoster.Runner
    /// (RequiredMods.PickRunner): the overland runner while it runs, then GameState's, then the idle overland runner.
    /// </summary>
    private static NetworkRunner CurrentRunner()
    {
        NetworkRunner overland = null;
        bool running = false;
        try
        {
            overland = OverlandNetworkRunner.Instance?.NetworkRunner;
            running = overland != null && overland.IsRunning;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "RequiredModsSession.CurrentRunner"); }
        return RequiredMods.PickRunner(overland, running, GameState.Runner);
    }

    /// <summary>
    /// Once per frame: the role, and a fresh start of the session state when the runner changed or this game stopped
    /// being a guest (so a later session never sees an earlier host's list).
    /// </summary>
    private static void Resolve()
    {
        int frame = Time.frameCount;
        if (frame == _roleFrame) return;
        _roleFrame = frame;
        var r = CurrentRunner();
        IntPtr ptr = IntPtr.Zero;
        try { if (r != null) ptr = r.Pointer; } catch (Exception ex) { DebugLog.Error("core", ex, "RequiredModsSession.Resolve"); r = null; }
        _sessionRunner = r;
        _role = ComputeRole(r);
        if (ptr != _runner || (_role != SessionRole.Guest && _wasGuest))
        {
            DebugLog.Verbose("core", () => $"server mods: session role {_role}, runner {(ptr == IntPtr.Zero ? "gone" : "new")}, advert read again");
            _runner = ptr;
            _advertised = null;
            _advertisedFound = false;
            _noticeDone = false;
            _nextRead = 0f;
        }
        _wasGuest = _role == SessionRole.Guest;
    }

    private static SessionRole ComputeRole(NetworkRunner r)
    {
        if (r == null) return SessionRole.None;
        try
        {
            // Not running yet: joining (or only the browser's lobby connection). Nothing is known about the session.
            if (!r.IsRunning) return SessionRole.Connecting;
            if (r.IsSinglePlayer) return SessionRole.Solo;
            if (r.IsServer) return SessionRole.Host;
            if (r.GameMode == GameMode.Shared && r.IsSharedModeMasterClient) return SessionRole.Host;
            return SessionRole.Guest;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "RequiredModsSession.ComputeRole"); return SessionRole.Connecting; } // unreadable: the safe side for Server mods
    }

    /// <summary>The Server mods the joined session advertises (null: none, or a vanilla / older host).</summary>
    public static RequiredList Advertised => _advertised;

    /// <summary>See <see cref="ModManager.ServerModActive"/>.</summary>
    public static bool ServerModActive(string id)
    {
        var m = ModManager.Find(id);
        if (m == null) return false;
        var role = Role;
        bool loaded = ModManager.IsLoaded(m.Id) && ModManager.ServerModReady(m.Id);
        // Never advertised (RequiredMods.AdvertisedMods): such a mod finds its peers over the mod network itself.
        if (m.Private) return RequiredMods.PrivateServerModActive(role, loaded);
        if (role == SessionRole.Guest) RefreshAdvert(force: false); // until it is read, a guest's Server mods stay off
        return RequiredMods.ServerModActive(role, loaded, m.Version, _advertised, m.Id);
    }

    /// <summary>One line for a Server mod's MODS page while in a session, or null.</summary>
    public static string StatusFor(ModInfo m)
    {
        if (m == null || m.Side != ModSide.Server || !ModManager.IsLoaded(m.Id)) return null;
        if (m.Private)
            return Role is SessionRole.Host or SessionRole.Guest
                ? "Private mod: not listed for this server. It works with the players who run it too."
                : null;
        switch (Role)
        {
            case SessionRole.Host: return LastStartValue != null && RequiredMods.Decode(LastStartValue)?.Find(m.Id) != null
                ? "Running for this server: guests who join are told they need it."
                : "Running, but this server started without listing it: modded guests keep its networked part off.";
            case SessionRole.Guest:
                var adv = _advertised?.Find(m.Id);
                if (adv == null) return "Off in this session: the host doesn't run it.";
                if (!RequiredMods.ServerModActive(SessionRole.Guest, true, m.Version, _advertised, m.Id))
                    return $"Off in this session: the host runs v{adv.Version}, you have v{RequiredMods.ShortVersion(m.Version) ?? m.Version}.";
                return "Running in this session: the host runs it too.";
            default: return null;
        }
    }

    /// <summary>Every installed mod (switched off ones too) as id → version.</summary>
    internal static Dictionary<string, string> InstalledVersions()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ModManager.All)
            if (m != null && !string.IsNullOrEmpty(m.Id)) d[m.Id] = m.Version;
        return d;
    }

    /// <summary>What stands between this game and a server's list.</summary>
    internal static List<RequiredModIssue> Check(RequiredList required) =>
        RequiredMods.Compare(required, InstalledVersions(), ModManager.IsLoaded);

    /// <summary>Before joining: <see cref="Check"/> plus the host's network prefab check (its prefab mods and join
    /// marker against this game's), so a join the host would refuse is named before it is tried.</summary>
    internal static List<RequiredModIssue> CheckJoin(RequiredList required)
    {
        List<string> local;
        string marker;
        bool pending;
        try
        {
            local = Core.Shared.NetPrefabRegistry.RegisteredOwners();
            marker = Core.Shared.NetPrefabRegistry.MarkerValue;
            pending = Core.Shared.NetPrefabRegistry.AnyPending;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "RequiredModsSession.CheckJoin"); local = new List<string>(); marker = null; pending = true; }
        return RequiredMods.CompareForJoin(required, InstalledVersions(), ModManager.IsLoaded, local, marker, pending);
    }

    /// <summary>A mod's name: installed, listed on the AVAILABLE tab, or its id.</summary>
    internal static string NameOf(string id) => ModManager.Find(id)?.Name ?? AvailableTab.ListedName(id) ?? id;

    internal static void Tick()
    {
        var role = Role;
        if (role == SessionRole.Host && _hostNotice != null)
        {
            // Once the hosted session runs (its StartGame was behind a loading screen): what its listing left out.
            _notice = _hostNotice;
            _noticeAt = Time.unscaledTime;
            _hostNotice = null;
        }
        if (role != SessionRole.Guest) return;
        RefreshAdvert(force: false);
        if (_noticeDone || !_advertisedFound) return;
        _noticeDone = true;
        var issues = Check(_advertised);
        foreach (var i in issues)
            if (i.Kind is RequiredIssueKind.VersionMajor or RequiredIssueKind.VersionMinor)
                Core.CorePlugin.Logger?.LogWarning($"[ModTypes] {NameOf(i.Id)}: the host runs v{i.Required}, this game v{i.Installed}" +
                                                   (i.Blocks ? " (another major version: its networked part stays off)" : ""));
        string text = RequiredMods.SessionNotice(issues, NameOf);
        string more = RequiredMods.MoreNotice(_advertised);
        if (more != null) text = text == null ? more : text + " " + more;
        if (text == null) return;
        Core.CorePlugin.Logger?.LogInfo("[ModTypes] " + text);
        _notice = text;
        _noticeAt = Time.unscaledTime;
    }

    /// <summary>
    /// Reads the joined session's list once a second until it is found, then keeps it for the session: the
    /// properties can read back without a key for a moment, and the host never changes the list.
    /// </summary>
    private static void RefreshAdvert(bool force)
    {
        if (_advertisedFound && !force) return;
        float now = Time.unscaledTime;
        if (!force && now < _nextRead) return;
        _nextRead = now + 1f;
        try
        {
            string v = GameState.SessionString(_sessionRunner?.SessionInfo, RequiredMods.Key, out bool found);
            if (!found) return;
            _advertised = RequiredMods.Decode(v);
            _advertisedFound = true;
            DebugLog.Info("core", $"the host's server mods advert read ({v?.Length ?? 0} B): {v}");
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "RequiredModsSession.RefreshAdvert"); /* the next read tries again */ }
    }

    /// <summary>The one-time notice, top centre, for <see cref="NoticeSeconds"/> (fades over the last second).</summary>
    internal static void DrawNotice()
    {
        float age = Time.unscaledTime - _noticeAt;
        if (age < 0f || age > NoticeSeconds || string.IsNullOrEmpty(_notice)) return;
        if (CoreBehaviour.GuiHidden) return; // never in a screenshot
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        Theme.Ensure();
        float scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        float sw = Screen.width / scale;
        var r = new Rect(Mathf.Round((sw - 560f) / 2f), 24f, 560f, 64f);
        var prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(NoticeSeconds - age));
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.92f), 10);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), Theme.Warn, 2);
        GUI.Label(new Rect(r.x + 16f, r.y + 6f, r.width - 24f, 20f), "Server mods", Theme.Label);
        GUI.Label(new Rect(r.x + 16f, r.y + 26f, r.width - 24f, r.height - 30f), _notice, Theme.Small);
        GUI.color = prev;
    }

    // ---------------------------------------------------------------- other sessions (server browser)

    private sealed class CacheEntry
    {
        public RequiredList Parsed;
        public float At;
    }

    private static readonly Dictionary<string, CacheEntry> Cache = new();
    private static readonly Dictionary<string, RequiredList> ParsedByValue = new();

    /// <summary>The Server mods a listed session advertises (null: none). Cached per session for a second: the browser
    /// asks for every row it restyles.</summary>
    public static RequiredList For(SessionInfo session)
    {
        if (session == null) return null;
        string id;
        try { id = session.Name ?? ""; } catch (Exception ex) { DebugLog.Error("core", ex, "RequiredModsSession.For"); return null; }
        float now = Time.unscaledTime;
        if (Cache.TryGetValue(id, out var c) && now - c.At < 1f && now >= c.At) return c.Parsed;
        if (Cache.Count > 256) Cache.Clear();
        c ??= new CacheEntry();
        c.At = now;
        c.Parsed = null;
        try
        {
            string raw = GameState.SessionString(session, RequiredMods.Key, out bool found);
            if (found)
            {
                if (!ParsedByValue.TryGetValue(raw ?? "", out var parsed))
                {
                    parsed = RequiredMods.Decode(raw);
                    if (ParsedByValue.Count > 256) ParsedByValue.Clear();
                    ParsedByValue[raw ?? ""] = parsed;
                }
                c.Parsed = parsed;
            }
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "RequiredModsSession.For"); /* unreadable: shown as needing nothing */ }
        Cache[id] = c;
        return c.Parsed;
    }
}

/// <summary>Runs the session side of Server mods (tick and the one-time notice).</summary>
internal sealed class RequiredModsFeature : IFeature
{
    public void Register()
    {
        Registry.OnTick(RequiredModsSession.Tick);
        Registry.OnGui(RequiredModsSession.DrawNotice);
    }
}

/// <summary>Adds the server mods' session property when hosting (runs alongside the other StartGame prefixes).</summary>
[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.StartGame))]
internal static class RequiredModsStartGamePatch
{
    private static void Prefix(StartGameArgs args) => RequiredModsSession.OnStartGame(args);
}
