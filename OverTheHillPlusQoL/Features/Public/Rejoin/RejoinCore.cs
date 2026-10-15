using System;
using FunCore.Managers;
using HarmonyLib;
using Il2CppInterop.Runtime;
using OverTheHillPlus.Features.Public.ServerBrowserPlus;
using OverTheHillPlus.ServerBrowser;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.Rejoin;

/// <summary>
/// Remembers the last server joined as a guest and drives the game's own join flow to get back into it.
///
/// How the game leaves a session (decompiled OverlandNetworkRunner): every way out funnels into
/// QuitToMenuAsync(MainMenuContext.Reason, details). OnDisconnectedFromServer maps Timeout to ServerDisconnected,
/// Requested (the host's KickPlayer) to PlayerKickedFromSession and ByRemote to ServerClosedRoom; OnShutdown maps
/// Error to ConnectionLost, PhotonCloudTimeout to PhotonCloudTimeout and DisconnectedByPluginLogic to
/// ServerClosedRoom; the pause menu's quit uses PlayerChoice. Only the first call counts (the game ignores repeats
/// while IsQuittingToMenu), and any reason other than PlayerChoice returns to the server browser in the same region.
///
/// How the game joins (StateServerBrowser.JoinSession / ContextUIServerJoinByCode.FindRoomByCode): the browser must
/// be open and connected to the room's region (ChangeLobbyRegion when it differs), the session is looked up in
/// OverlandNetworkRunner.SessionList and handed to JoinSession(session, joiningByCode). We do exactly that, from
/// Update, and only when the browser is the top state (a "disconnected" notice on top is dismissed first, which is
/// what its Continue button does).
/// </summary>
internal static class RejoinCore
{
    internal enum Phase { Idle, WaitMenu, Countdown, Seeking, Joining, Failed, Done }

    public static Phase State { get; private set; } = Phase.Idle;
    public static bool Automatic { get; private set; }
    public static int Attempt { get; private set; }
    public static string StatusText { get; private set; } = "";
    /// <summary>Unscaled time the countdown ends (Countdown) or the search gives up (Seeking).</summary>
    public static float Deadline { get; private set; }
    /// <summary>Why the last session ended (game's MainMenuContext reason), for the menu.</summary>
    public static string LastLeaveReason { get; private set; }

    /// <summary>True while something is in progress that the overlay should show.</summary>
    public static bool Active => State is Phase.WaitMenu or Phase.Countdown or Phase.Seeking or Phase.Joining;

    private static LastServerStore.Entry _target;
    private static float _phaseStart, _nextStep, _nextNav, _nextRegionSwitch, _nextPop, _messageUntil;
    private static float _mainMenuSince = -1f;
    private static bool _requestManual, _requestCancel, _requestNow, _quittingGame;
    private static bool _inClientSession;
    private static float _clientSessionSeenAt = -100f;
    private static string _sessionName;
    private static float _nextCodeRead;
    private static bool _loggedTickError, _loggedJoinError, _loggedQuitError;

    // What JoinSession was last called with: names the room we are about to be in (display name, privacy).
    private static string _candidateSession, _candidateDisplay, _candidateRegion;
    private static bool _candidatePrivate;

    // When automatic rejoins succeeded (unscaled time). A host that drops us again soon after every rejoin would
    // otherwise cycle join, drop, countdown, join for ever: MaxAttempts only counts failed joins within one cycle.
    private static readonly System.Collections.Generic.List<float> AutoRejoinedAt = new();
    private const float RecentRejoin = 120f, RejoinWindow = 900f;
    private const int MaxRejoinsInWindow = 3;

    // ---------------------------------------------------------------- requests (safe from OnGUI / UI callbacks)

    public static void RequestManual() => _requestManual = true;
    public static void RequestCancel() => _requestCancel = true;
    public static void RequestNow() => _requestNow = true;

    /// <summary>In a session (hosting, joined or single-player), refreshed four times a second (cheap for menus).</summary>
    public static bool InAnySession { get; private set; }

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        try { Step(); }
        catch (Exception e)
        {
            if (!_loggedTickError)
            {
                _loggedTickError = true;
                Plugin.Logger.LogError("[Rejoin] tick failed (further errors suppressed): " + e);
            }
            SetIdle("Rejoin stopped after an error (see the log)");
        }
    }

    private static void Step()
    {
        float now = Time.unscaledTime;

        if (_requestCancel)
        {
            _requestCancel = false;
            if (Active) SetIdle("Rejoin cancelled");
        }
        if (_requestManual)
        {
            _requestManual = false;
            StartManual();
        }

        // Cheap per-frame part: the countdown display and the "now" button.
        if (State == Phase.Countdown && (_requestNow || now >= Deadline))
        {
            _requestNow = false;
            EnterSeeking();
        }
        _requestNow = false;
        if ((State == Phase.Failed || State == Phase.Done) && now >= _messageUntil) State = Phase.Idle;

        if (now < _nextStep) return;
        _nextStep = now + 0.25f;

        TrackSession(now);

        switch (State)
        {
            case Phase.WaitMenu: StepWaitMenu(now); break;
            case Phase.Countdown:
                if (AnySession(out _)) SetIdle("Rejoin cancelled: you started another session");
                break;
            case Phase.Seeking: StepSeeking(now); break;
            case Phase.Joining: StepJoining(now); break;
        }
    }

    /// <summary>Follows the client session we are in: remembers it as the last server, and notes the room code.</summary>
    private static void TrackSession(float now)
    {
        InAnySession = AnySession(out _);
        OverlandNetworkRunner overland = null;
        Fusion.NetworkRunner runner = null;
        bool client = InAnySession && ClientSession(out overland, out runner);
        if (!client)
        {
            _inClientSession = false;
            return;
        }
        _clientSessionSeenAt = now;
        string name = null;
        try { name = runner.SessionInfo?.Name; } catch { }
        if (string.IsNullOrEmpty(name)) return;

        if (!_inClientSession || name != _sessionName)
        {
            _inClientSession = true;
            _sessionName = name;
            Remember(overland, runner, name);
        }
        else if (now >= _nextCodeRead)
        {
            _nextCodeRead = now + 3f;
            string code = null;
            try { code = overland.GameSetupContext?.RoomCode; } catch { }
            LastServerStore.Touch(name, code);
        }
    }

    private static void Remember(OverlandNetworkRunner overland, Fusion.NetworkRunner runner, string name)
    {
        var e = new LastServerStore.Entry { SessionName = name };
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        e.JoinedAt = now;
        e.LastSeenAt = now;
        try { e.Region = runner.SessionInfo?.Region; } catch { }
        if (string.IsNullOrEmpty(e.Region)) { try { e.Region = overland.GameSetupContext?.ServerRegion; } catch { } }
        if (string.IsNullOrEmpty(e.Region) && name == _candidateSession) e.Region = _candidateRegion;
        try { e.RoomCode = LastServerStore.NormaliseCode(overland.GameSetupContext?.RoomCode); } catch { }

        if (name == _candidateSession)
        {
            e.DisplayName = _candidateDisplay;
            e.Private = _candidatePrivate;
        }
        else
        {
            // Joined some other way (an invite): read what we can from the live session.
            try
            {
                var props = runner.SessionInfo?.Properties;
                if (props != null) e.Private = new OverlandRoomProperties(props).PrivateServer;
            }
            catch { }
            int cut = name.IndexOf('_');
            e.DisplayName = cut >= 0 ? name.Substring(cut + 1) : name;
        }
        if (string.IsNullOrWhiteSpace(e.DisplayName)) e.DisplayName = name;
        LastServerStore.Remember(e);
        Plugin.Logger.LogInfo($"[Rejoin] remembered last server '{Favourites.PlainName(e.DisplayName)}' ({e.Region ?? "?"}{(e.Private ? ", private" : "")})");

        if (State == Phase.Joining) Succeed();
    }

    // ---------------------------------------------------------------- phases

    private static void StartManual()
    {
        var last = LastServerStore.Current;
        if (last == null || string.IsNullOrEmpty(last.SessionName)) { Menu.SetStatus("Blocked: no server joined yet"); return; }
        if (AnySession(out _)) { Menu.SetStatus("Blocked: leave the current session first"); return; }
        if (Active) { Menu.SetStatus("A rejoin is already in progress"); return; }
        _target = last;
        Automatic = false;
        Attempt = 0;
        Menu.CloseRequested = true; // give the game's UI back: the join flow runs in its menus
        EnterSeeking();
    }

    private static void StartAutomatic(string why)
    {
        var last = LastServerStore.Current;
        if (last == null || string.IsNullOrEmpty(last.SessionName)) return;
        _target = last;
        Automatic = true;
        Attempt = 0;
        Enter(Phase.WaitMenu, $"Connection to {Name(_target)} lost ({why}): waiting for the menu");
        Plugin.Logger.LogInfo($"[Rejoin] unexpected disconnect ({why}): will try to rejoin '{Name(_target)}'");
    }

    private static void StepWaitMenu(float now)
    {
        if (AnySession(out _))
        {
            // The old session is still shutting down; much later it is a new one the player started.
            if (now - _phaseStart > 20f) SetIdle("Rejoin cancelled: you started another session");
            return;
        }
        var top = TopState();
        bool ready = Is<StateServerBrowser>(top) || Is<StateMainMenu>(top) || ContainsBrowser();
        if (!ready && now - _phaseStart < 30f) return;
        float delay = Mathf.Clamp(RejoinFeature.CountdownSeconds.Value, 0f, 60f);
        if (delay <= 0.05f) { EnterSeeking(); return; }
        Enter(Phase.Countdown, null);
        Deadline = now + delay;
    }

    private static void EnterSeeking()
    {
        Enter(Phase.Seeking, $"Looking for {Name(_target)}");
        Deadline = Time.unscaledTime + Mathf.Clamp(RejoinFeature.KeepTryingSeconds.Value, 10f, 600f);
        _nextNav = 0f;
        _nextRegionSwitch = 0f;
        _nextPop = 0f;
        _mainMenuSince = -1f;
    }

    private static void StepSeeking(float now)
    {
        if (AnySession(out _))
        {
            SetIdle("Rejoin cancelled: you started another session");
            return;
        }
        if (now >= Deadline)
        {
            FailAttempt($"{Name(_target)} was not found in the server list", retry: false);
            return;
        }

        var gsm = Gsm();
        var top = gsm?.PeekState();
        var browser = top?.TryCast<StateServerBrowser>();
        bool modalOnBrowser = browser == null && IsNotice(top) && ContainsBrowser();

        if (browser == null && !modalOnBrowser)
        {
            var mainMenu = top?.TryCast<StateMainMenu>();
            // The game may be about to open the browser by itself (it does after a disconnect): only press
            // Multiplayer once the main menu has been on top for a moment and no browser is on the stack.
            if (mainMenu == null) _mainMenuSince = -1f;
            else if (_mainMenuSince < 0f) _mainMenuSince = now;
            if (mainMenu != null && now >= _nextNav && now - _mainMenuSince >= 1.5f && !ContainsBrowser())
            {
                _nextNav = now + 3f;
                StatusText = "Opening the server browser";
                try { mainMenu.FusionMultiplayerButtonPressed(null); }
                catch (Exception e) { Plugin.Logger.LogWarning("[Rejoin] could not open the server browser: " + e.Message); }
            }
            else if (mainMenu == null) StatusText = "Waiting for the server browser";
            return;
        }

        if (OverlandNetworkRunner.Instance == null) return;
        if (!LobbyRegion.InLobby) { StatusText = "Connecting to the server list"; return; }

        string region = _target.Region;
        if (!LobbyRegion.Ensure(region, ref _nextRegionSwitch, 15f, "[Rejoin]"))
        {
            StatusText = $"Switching to region {region.ToUpperInvariant()}";
            return;
        }

        var info = FindTarget(out bool byExactRoom);
        if (info == null) { StatusText = $"Looking for {Name(_target)}"; return; }
        var s = info.FusionSessionInfo;
        ServerStatus status;
        try { status = RowStyler.Classify(info, s); } catch { status = ServerStatus.Public; }
        if (status == ServerStatus.Incompatible)
        {
            FailAttempt($"{Name(_target)} now runs another game version", retry: false);
            return;
        }
        if (!BrowserPlus.HasFreeSlot(s))
        {
            StatusText = s != null && !s.IsOpen ? $"{Name(_target)} is closed to new players, waiting" : $"{Name(_target)} is full, waiting for a slot";
            return;
        }

        if (modalOnBrowser)
        {
            // The game's "disconnected" notice sits on top: dismiss it like its Continue button does.
            if (now >= _nextPop)
            {
                _nextPop = now + 1f;
                try { gsm.PopState(); }
                catch (Exception e) { Plugin.Logger.LogWarning("[Rejoin] could not close the notice: " + e.Message); }
            }
            return;
        }

        bool isPrivate = false;
        try { isPrivate = info.RoomProperties != null && info.RoomProperties.PrivateServer; } catch { }
        if (isPrivate && !byExactRoom)
        {
            // Never open a private room we were not in (same name is not proof): only the exact room or its code.
            StatusText = $"{Name(_target)} is private now, waiting for the original room";
            return;
        }

        Join(browser, info, isPrivate);
    }

    private static void Join(StateServerBrowser browser, OverlandSessionInfo info, bool isPrivate)
    {
        Attempt++;
        var s = info.FusionSessionInfo;
        Plugin.Logger.LogInfo($"[Rejoin] joining '{Name(_target)}' ({s?.PlayerCount}/{s?.MaxPlayers}), attempt {Attempt}");
        Enter(Phase.Joining, $"Joining {Name(_target)}");
        try
        {
            // joiningByCode only skips the "this server is private" pop-up: we were in this exact room already.
            browser.JoinSession(info, isPrivate);
        }
        catch (Exception e)
        {
            if (!_loggedJoinError)
            {
                _loggedJoinError = true;
                Plugin.Logger.LogWarning("[Rejoin] JoinSession failed: " + e);
            }
            FailAttempt("the game refused the join: " + e.Message, retry: true);
        }
    }

    private static void StepJoining(float now)
    {
        if (ClientSession(out _, out _)) { Succeed(); return; }
        float t = now - _phaseStart;
        if (t > 8f && !AnySession(out _))
        {
            // Still sitting in the browser (or a pop-up over it): the game refused the join itself, e.g. the
            // version or full-server notice. Leave its pop-up for the player to read.
            var top = TopState();
            if (Is<StateServerBrowser>(top) || (IsNotice(top) && ContainsBrowser()) || Is<StateMainMenu>(top))
            {
                FailAttempt("the game did not start the join", retry: true);
                return;
            }
        }
        if (t > 120f) FailAttempt("joining took too long", retry: true);
    }

    private static void Succeed()
    {
        if (Automatic)
        {
            AutoRejoinedAt.Add(Time.unscaledTime);
            if (AutoRejoinedAt.Count > 16) AutoRejoinedAt.RemoveRange(0, AutoRejoinedAt.Count - 16);
        }
        string name = Name(_target);
        Enter(Phase.Done, $"Rejoined {name}");
        _messageUntil = Time.unscaledTime + 6f;
        Plugin.Logger.LogInfo($"[Rejoin] back in '{name}'");
        Menu.SetStatus($"Rejoined {name}");
    }

    private static void FailAttempt(string why, bool retry)
    {
        int max = Mathf.Clamp(RejoinFeature.MaxAttempts.Value, 1, 10);
        if (retry && Automatic && Attempt < max)
        {
            Plugin.Logger.LogInfo($"[Rejoin] attempt {Attempt} failed ({why}), trying again");
            Enter(Phase.WaitMenu, $"Attempt {Attempt} failed: {why}");
            return;
        }
        Plugin.Logger.LogInfo($"[Rejoin] gave up: {why}");
        Enter(Phase.Failed, "Rejoin failed: " + why);
        _messageUntil = Time.unscaledTime + 10f;
        Menu.SetStatus("Rejoin failed: " + why);
    }

    private static void SetIdle(string message)
    {
        bool was = Active;
        State = Phase.Idle;
        StatusText = message ?? "";
        _target = null;
        if (was && !string.IsNullOrEmpty(message))
        {
            Menu.SetStatus(message);
            Plugin.Logger.LogInfo("[Rejoin] " + message);
        }
    }

    private static void Enter(Phase p, string status)
    {
        State = p;
        _phaseStart = Time.unscaledTime;
        _nextStep = 0f;
        if (status != null) StatusText = status;
    }

    // ---------------------------------------------------------------- finding the room

    private static OverlandSessionInfo FindTarget(out bool exactRoom)
    {
        exactRoom = false;
        var t = _target;
        if (t == null) return null;
        string hash = null;
        if (!string.IsNullOrEmpty(t.RoomCode))
        {
            try { hash = OverlandNetworkUtils.HashRoomCode(t.RoomCode); } catch { }
        }
        string key = t.Private ? "" : Favourites.Key(t.DisplayName);

        OverlandSessionInfo byName = null, byCode = null, byDisplay = null;
        // Only the connected region's rooms: the region was switched to the target's above.
        foreach (var info in BrowserPlus.AllSessions(otherRegions: false))
        {
            string n = null;
            try { n = info.FusionSessionInfo?.Name; } catch { }
            if (n != null && n == t.SessionName) { byName = info; break; }
            if (byCode == null && hash != null)
            {
                try { if (info.RoomCodeHash == hash) byCode = info; } catch { }
            }
            if (byDisplay == null && key.Length > 0 && Favourites.Key(Favourites.DisplayNameOf(info)) == key) byDisplay = info;
        }
        if (byName != null || byCode != null) { exactRoom = true; return byName ?? byCode; }
        return byDisplay; // public room reopened by the same host under a new session id
    }

    // ---------------------------------------------------------------- game state helpers

    private static GameStateManager Gsm()
    {
        try { return HybridSingleton<GameStateManager>.Instance; } catch { return null; }
    }

    private static FunCore.StateMachine.State TopState()
    {
        try { return Gsm()?.PeekState(); } catch { return null; }
    }

    private static bool Is<T>(FunCore.StateMachine.State s) where T : Il2CppSystem.Object
    {
        if (s == null) return false;
        try { return s.TryCast<T>() != null; } catch { return false; }
    }

    /// <summary>The game's notices that sit on top of the browser after a disconnect or a refused join.</summary>
    private static bool IsNotice(FunCore.StateMachine.State s) => Is<StatePlayerKickedNotice>(s) || Is<StateGenericOneOptionModal>(s);

    private static bool ContainsBrowser()
    {
        try
        {
            var gsm = Gsm();
            return gsm != null && gsm.ContainsState(Il2CppType.Of<StateServerBrowser>());
        }
        catch { return false; }
    }

    /// <summary>A multiplayer session is running (hosting or joined); single-player counts too.</summary>
    private static bool AnySession(out Fusion.NetworkRunner runner)
    {
        runner = null;
        try
        {
            var overland = OverlandNetworkRunner.Instance;
            runner = overland == null ? null : overland.NetworkRunner;
            return runner != null && runner.IsRunning && (runner.IsClient || runner.IsServer);
        }
        catch { return false; }
    }

    /// <summary>Joined someone else's room as a guest and connected.</summary>
    private static bool ClientSession(out OverlandNetworkRunner overland, out Fusion.NetworkRunner runner)
    {
        overland = null;
        runner = null;
        try
        {
            overland = OverlandNetworkRunner.Instance;
            if (overland == null || overland.IsSinglePlayer) return false;
            runner = overland.NetworkRunner;
            return runner != null && runner.IsRunning && runner.IsClient && !runner.IsServer && runner.IsConnectedToServer;
        }
        catch { return false; }
    }

    internal static string Name(LastServerStore.Entry e)
    {
        string n = e == null ? "" : Favourites.PlainName(e.DisplayName);
        if (string.IsNullOrEmpty(n)) n = "the last server";
        return n.Length <= 40 ? n : n.Substring(0, 39) + "...";
    }

    // ---------------------------------------------------------------- game hooks

    internal static void OnJoinSession(OverlandSessionInfo info)
    {
        try
        {
            if (info == null) return;
            // Our own join runs in Phase.Joining: any other join (the player picked a server or used a room code)
            // ends a pending rejoin, so it can't start a second join while the game is already connecting.
            if (State is Phase.WaitMenu or Phase.Countdown or Phase.Seeking) SetIdle("Rejoin cancelled: you joined another server");
            _candidateSession = info.FusionSessionInfo?.Name;
            _candidateDisplay = Favourites.DisplayNameOf(info);
            _candidatePrivate = info.RoomProperties != null && info.RoomProperties.PrivateServer;
            _candidateRegion = null;
            try { _candidateRegion = OverlandNetworkRunner.Instance?.ConnectedRegion; } catch { }
        }
        catch { /* bookkeeping only */ }
    }

    internal static void OnQuitToMenu(OverlandNetworkRunner runner, MainMenuContext.Reason reason)
    {
        try
        {
            if (runner == null || runner.IsQuittingToMenu || _quittingGame) return; // the game ignores repeats too
            bool wasClient = _inClientSession || Time.unscaledTime - _clientSessionSeenAt < 5f;
            _inClientSession = false;
            if (wasClient || State == Phase.Joining) LastLeaveReason = ReasonText(reason);

            if (State == Phase.Joining)
            {
                // Our own attempt ended: kicked, banned or a version mismatch means stop for good.
                bool final = reason is MainMenuContext.Reason.PlayerKickedFromSession or MainMenuContext.Reason.PlayerBlacklistedFromSession
                    or MainMenuContext.Reason.IncompatibleGameVersions or MainMenuContext.Reason.PlayerChoice;
                FailAttempt(LastLeaveReason, retry: !final);
                return;
            }
            if (Active && reason == MainMenuContext.Reason.PlayerChoice) { SetIdle("Rejoin cancelled"); return; }
            if (!wasClient || Active) return;
            if (!RejoinFeature.AutoRejoin.Value) return;
            if (!Unexpected(reason)) return;
            if (AutoRejoinLooping(out string loop))
            {
                Plugin.Logger.LogInfo($"[Rejoin] not rejoining automatically ({LastLeaveReason}): {loop}");
                Enter(Phase.Failed, "Auto-rejoin paused: " + loop + ". Rejoin by hand from Servers.");
                _messageUntil = Time.unscaledTime + 12f;
                Menu.SetStatus("Auto-rejoin paused: " + loop);
                return;
            }
            StartAutomatic(LastLeaveReason);
        }
        catch (Exception e)
        {
            if (_loggedQuitError) return;
            _loggedQuitError = true;
            Plugin.Logger.LogWarning("[Rejoin] disconnect hook failed: " + e);
        }
    }

    /// <summary>
    /// The last automatic rejoin worked less than 2 minutes ago, or 3 or more happened in the last 15 minutes: the
    /// connection keeps dropping right after rejoining, so stop retrying on our own (a manual rejoin still works).
    /// </summary>
    private static bool AutoRejoinLooping(out string why)
    {
        why = null;
        float now = Time.unscaledTime;
        AutoRejoinedAt.RemoveAll(t => now - t > RejoinWindow || t > now);
        if (AutoRejoinedAt.Count == 0) return false;
        if (now - AutoRejoinedAt[AutoRejoinedAt.Count - 1] < RecentRejoin)
        {
            why = "disconnected again soon after rejoining";
            return true;
        }
        if (AutoRejoinedAt.Count >= MaxRejoinsInWindow)
        {
            why = $"rejoined {AutoRejoinedAt.Count} times in the last 15 minutes";
            return true;
        }
        return false;
    }

    internal static void OnQuitGame()
    {
        _quittingGame = true;
        State = Phase.Idle;
    }

    /// <summary>Disconnects worth an automatic retry: network trouble, not a kick, a ban or quitting.</summary>
    private static bool Unexpected(MainMenuContext.Reason reason) => reason switch
    {
        MainMenuContext.Reason.Unknown => true,
        MainMenuContext.Reason.ServerDisconnected => true,
        MainMenuContext.Reason.ConnectionLost => true,
        MainMenuContext.Reason.ConnectionTimeout => true,
        MainMenuContext.Reason.PhotonCloudTimeout => true,
        MainMenuContext.Reason.ServerClosedRoom => RejoinFeature.AfterRoomClosed.Value,
        _ => false,
    };

    private static string ReasonText(MainMenuContext.Reason reason) => reason switch
    {
        MainMenuContext.Reason.PlayerChoice => "left",
        MainMenuContext.Reason.Unknown => "unknown error",
        MainMenuContext.Reason.UnableToStartGame => "could not start the game",
        MainMenuContext.Reason.PlayerKickedFromSession => "kicked",
        MainMenuContext.Reason.PlayerBlacklistedFromSession => "banned from the room",
        MainMenuContext.Reason.ServerDisconnected => "disconnected from the host",
        MainMenuContext.Reason.ServerClosedRoom => "the host closed the room",
        MainMenuContext.Reason.ConnectionLost => "connection lost",
        MainMenuContext.Reason.GameIsFull => "room full",
        MainMenuContext.Reason.ConnectionTimeout => "connection timed out",
        MainMenuContext.Reason.PhotonCloudTimeout => "Photon cloud timeout",
        MainMenuContext.Reason.MaxCcuReached => "server capacity reached",
        MainMenuContext.Reason.IncompatibleGameVersions => "different game version",
        MainMenuContext.Reason.GameNotFound => "room not found",
        _ => reason.ToString(),
    };
}

[HarmonyPatch]
internal static class RejoinPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.QuitToMenuAsync))]
    private static void BeforeQuitToMenu(OverlandNetworkRunner __instance, MainMenuContext.Reason reason) =>
        RejoinCore.OnQuitToMenu(__instance, reason);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.QuitGameAsync))]
    private static void BeforeQuitGame() => RejoinCore.OnQuitGame();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.JoinSession))]
    private static void BeforeJoinSession(OverlandSessionInfo sessionToJoin) => RejoinCore.OnJoinSession(sessionToJoin);
}
