using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using OverTheHillPlus.UI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.Autosave;

/// <summary>
/// Periodic and on-demand saves through the game's own save path. Nothing here writes a file itself:
///
/// - In a session the scene has a <c>GameSessionAutoSaver</c>; the game sets its <c>autoSaveQueued</c> flag when you
///   reach a point of interest and its Update then calls <c>OverlandSaver.SaveGame()</c> (serialises the profile in
///   memory and writes it). The mod sets the same flag, so its save is exactly the game's autosave.
/// - If that object isn't processing the flag (disabled, missing), the mod calls <c>OverlandSaver.SaveGame()</c> once.
/// - Before quitting, the game's <c>SaveSessionOnExit</c> saves when its <c>sessionSavePending</c> flag is set; the
///   quit patches only save when that flag is off, so there is never a second exit save.
///
/// Every <c>SaveGame()</c> call (the game's or ours) is seen by a postfix and restarts the countdown, so the mod
/// doesn't add a save right after one the game just made.
/// </summary>
internal static class AutosaveRuntime
{
    private const float QueueTimeout = 5f;

    private static GameSessionAutoSaver _saver;
    private static float _nextSaverScan;
    private static IntPtr _sessionPtr;
    private static float _sessionStartedAt = -1f;
    private static bool _sawWorld;

    private static string _pending;          // reason of a save the mod queued, until it has happened
    private static float _pendingAt;
    private static bool _pendingFellBack;

    private static float _nextTick;
    private static float _lastQuitSaveAt = -100f;
    private static readonly HashSet<string> Logged = new();

    private static int _savePatched = -1;

    /// <summary>Whether the SaveGame postfix is installed (then saves are seen directly). Checked once.</summary>
    private static bool SavePatched
    {
        get
        {
            if (_savePatched >= 0) return _savePatched == 1;
            bool yes = false;
            try
            {
                var m = HarmonyLib.AccessTools.Method(typeof(OverlandSaver), nameof(OverlandSaver.SaveGame));
                var info = m == null ? null : HarmonyLib.Harmony.GetPatchInfo(m);
                if (info != null)
                    foreach (var p in info.Postfixes)
                        if (p.PatchMethod?.DeclaringType == typeof(AutosaveSaveGamePatch)) yes = true;
            }
            catch (Exception e) { LogOnce("patchinfo", "checking the save patch: " + e.Message); }
            _savePatched = yes ? 1 : 0;
            return yes;
        }
    }

    /// <summary>Time (unscaled) of the last save seen this game run, any source; negative = none yet.</summary>
    internal static float LastSaveAt { get; private set; } = -1f;
    internal static string LastSaveWhat { get; private set; } = "";
    internal static int SavesThisRun { get; private set; }

    // ---- toast ----
    private static string _toast = "";
    private static string _toastSub = "";
    private static bool _toastBad;
    private static float _toastAt = -100f;
    private const float ToastSeconds = 3f;

    // ---- state the menu reads ----

    internal static bool InSession
    {
        get
        {
            var r = GameState.Runner;
            if (r == null) return false;
            try { return r.IsRunning; } catch { return false; }
        }
    }

    internal static bool IsGuest => InSession && !GameState.IsAuthority;

    /// <summary>Saving is allowed by policy here: solo/host always, guests only with the opt-in.</summary>
    internal static bool PolicyAllows => InSession && (GameState.IsAuthority || AutosaveConfig.GuestSaves.Value);

    /// <summary>The session's world is loaded (the game's autosaver object exists).</summary>
    internal static bool InWorld => Saver != null;

    internal static bool Busy => _pending != null;

    /// <summary>Why the mod can't save right now, or null when it can.</summary>
    internal static string BlockReason()
    {
        if (!InSession) return "Not in a session";
        if (!PolicyAllows) return "Guest saves are off";
        if (!InWorld) return "World not loaded yet";
        if (OverlandSaverProfile() == null) return "No profile loaded";
        if (Transitioning(out string why)) return why;
        return null;
    }

    /// <summary>Seconds until the next periodic autosave, or -1 when none is scheduled.</summary>
    internal static float SecondsUntilNext()
    {
        if (!AutosaveConfig.Enabled.Value || !PolicyAllows || !_sawWorld) return -1f;
        return Mathf.Max(0f, NextDueAt() - Time.unscaledTime);
    }

    private static float Interval => Mathf.Clamp(AutosaveConfig.IntervalMinutes.Value, 2f, 60f) * 60f;

    private static float NextDueAt()
    {
        // Count from the last save in this session, or from when the world loaded.
        float from = Mathf.Max(LastSaveAt, _sessionStartedAt);
        return from + Interval;
    }

    // ---- per frame ----

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (_pending != null) WatchPending(now);
        if (now < _nextTick) return;
        _nextTick = now + 1f;

        TrackSession(now);
        if (!AutosaveConfig.Enabled.Value || _pending != null || !_sawWorld) return;
        if (now < NextDueAt()) return;
        if (BlockReason() != null) return; // try again next second; a late save beats a save during a transition
        Request("Autosaved", false);
    }

    private static void TrackSession(float now)
    {
        IntPtr ptr = IntPtr.Zero;
        try { ptr = InSession ? GameState.Runner.Pointer : IntPtr.Zero; } catch { }
        if (ptr != _sessionPtr)
        {
            _sessionPtr = ptr;
            _sawWorld = false;
            _sessionStartedAt = -1f;
            _saver = null;
            _nextSaverScan = 0f;
            _pending = null;
        }
        if (ptr == IntPtr.Zero) return;
        if (!_sawWorld && Saver != null)
        {
            _sawWorld = true;
            _sessionStartedAt = now; // first autosave one interval after the world loaded, not right away
        }
    }

    private static GameSessionAutoSaver Saver
    {
        get
        {
            if (_saver != null) return _saver; // Unity null check: a destroyed saver reads as null
            _saver = null;
            float now = Time.unscaledTime;
            if (now < _nextSaverScan || !InSession) return null;
            _nextSaverScan = now + 3f;
            try
            {
                var o = Object.FindObjectOfType(Il2CppType.Of<GameSessionAutoSaver>());
                _saver = o == null ? null : o.TryCast<GameSessionAutoSaver>();
            }
            catch (Exception e) { LogOnce("scan", "finding the game's autosaver: " + e.Message); }
            return _saver;
        }
    }

    private static ProfileSave OverlandSaverProfile()
    {
        try { return OverlandSaver.CurrentProfileSave; }
        catch (Exception e) { LogOnce("profile", "reading the current profile: " + e.Message); return null; }
    }

    /// <summary>Quitting, host migration or a portal scene change: the world is being torn down or rebuilt.</summary>
    private static bool Transitioning(out string why)
    {
        why = null;
        try
        {
            var onr = OverlandNetworkRunner.Instance;
            if (onr != null)
            {
                if (onr.IsQuittingToMenu) { why = "Leaving the session"; return true; }
                if (onr.IsMigratingHost) { why = "Host migration in progress"; return true; }
            }
        }
        catch (Exception e) { LogOnce("onr", "reading the session state: " + e.Message); }
        try
        {
            if (PortalNetworkManager.Instance != null && PortalNetworkManager.IsPortalTransitionPending) { why = "Travelling through a portal"; return true; }
        }
        catch (Exception e) { LogOnce("portal", "reading the portal state: " + e.Message); }
        return false;
    }

    // ---- saving ----

    /// <summary>Asks the game to save (queued through its autosaver). Returns a status line for the menu.</summary>
    internal static string Request(string reason, bool manual)
    {
        string blocked = BlockReason();
        if (blocked != null) return "Blocked: " + blocked.ToLowerInvariant();
        if (_pending != null) return "Saving…";
        var saver = Saver;
        try
        {
            saver.autoSaveQueued = true;
            _pending = reason;
            _pendingAt = Time.unscaledTime;
            _pendingFellBack = false;
            if (manual) Plugin.Logger.LogInfo("[Autosave] save requested from the menu");
            return "Saving…";
        }
        catch (Exception e)
        {
            LogOnce("queue", "queueing the game's save failed, saving directly: " + e.Message);
            return SaveDirect(reason) ? "Saved" : "Failed: the game's save threw (see the log)";
        }
    }

    private static void WatchPending(float now)
    {
        var saver = _saver;
        if (saver == null) { _pending = null; return; } // world unloaded before the save ran
        bool stillQueued;
        try { stillQueued = saver.autoSaveQueued; }
        catch { stillQueued = false; }

        if (!stillQueued)
        {
            // The SaveGame postfix normally reports the save (and clears _pending) the moment it happens. Without the
            // patch, the flag going back to false is the only sign the game's Update ran the save.
            if (_pending != null && !SavePatched) OnSaved(_pending);
            _pending = null;
            return;
        }
        if (_pendingFellBack || now - _pendingAt < QueueTimeout) return;

        // The autosaver isn't running its Update (disabled object?): save once directly and drop the queued flag so it
        // can't save a second time later.
        _pendingFellBack = true;
        string reason = _pending;
        try { saver.autoSaveQueued = false; } catch { }
        _pending = null;
        if (BlockReason() == null) SaveDirect(reason);
    }

    private static bool SaveDirect(string reason)
    {
        _pending = reason;
        try
        {
            OverlandSaver.SaveGame();
            if (_pending != null) OnSaved(reason); // postfix missing: report it here
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("[Autosave] the game's save failed: " + e.Message);
            Toast("Save failed", "See the BepInEx log", true);
            return false;
        }
        finally { _pending = null; }
    }

    /// <summary>Called by the SaveGame postfix for every save, the game's own included.</summary>
    internal static void OnGameSaveFinished()
    {
        string reason = _pending;
        _pending = null;
        OnSaved(reason);
    }

    private static void OnSaved(string reason)
    {
        LastSaveAt = Time.unscaledTime;
        LastSaveWhat = reason ?? "by the game";
        SavesThisRun++;
        if (reason != null)
        {
            if (AutosaveConfig.ShowToast.Value) Toast("Game saved", ToastSub(reason), false);
        }
        else if (AutosaveConfig.ToastGameSaves.Value) Toast("Game saved", "Saved by the game", false);
    }

    private static string ToastSub(string reason)
    {
        if (!AutosaveConfig.Enabled.Value || !PolicyAllows) return reason;
        int min = Mathf.RoundToInt(Interval / 60f);
        return $"{reason}  ·  next in {min} min";
    }

    /// <summary>
    /// Quit to menu / desktop: the game saves on exit when its sessionSavePending flag is set. Save here only when it
    /// won't, so there is exactly one exit save. Runs synchronously before the session is torn down.
    /// </summary>
    internal static void BeforeQuit(OverlandNetworkRunner runner, string what)
    {
        if (!AutosaveConfig.SaveBeforeQuit.Value) return;
        // A repeated quit (a kick then a disconnect, or quitting the game while the quit to menu runs): the game has
        // already handled its exit (QuitToMenuAsync sets this flag and runs SaveSessionOnExit only the first time),
        // and the world is being torn down. The same for a host migration or a portal transition.
        if (Transitioning(out _)) return;
        float now = Time.unscaledTime;
        if (now - _lastQuitSaveAt < 5f) return; // the quit can be requested more than once
        if (!_sawWorld || !PolicyAllows) return;
        try
        {
            if (runner != null && runner.IsQuittingToMenu) return; // already leaving: the game made its exit save
            if (runner != null && runner.sessionSavePending) return; // the game's own exit save will run
            if (runner != null && runner.IsMigratingHost) return;
        }
        catch (Exception e) { LogOnce("quitflag", "reading the exit-save flag: " + e.Message); return; }
        if (OverlandSaverProfile() == null) return;
        _lastQuitSaveAt = now;
        Plugin.Logger.LogInfo($"[Autosave] the game would skip its exit save ({what}): saving first");
        SaveDirect("Saved before " + what);
    }

    // ---- toast overlay ----

    private static void Toast(string title, string sub, bool bad)
    {
        _toast = title;
        _toastSub = sub ?? "";
        _toastBad = bad;
        _toastAt = Time.unscaledTime;
    }

    internal static void DrawToast()
    {
        float age = Time.unscaledTime - _toastAt;
        if (age < 0f || age > ToastSeconds || string.IsNullOrEmpty(_toast)) return;
        if (Photo.PhotoCapture.Capturing) return; // never in a screenshot
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        Theme.Ensure();
        float scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        float sw = Screen.width / scale;
        var r = new Rect(sw - 300f, 20f, 280f, 50f);
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(ToastSeconds - age)); // fades over the last second
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.9f), 10);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), _toastBad ? Theme.Bad : Theme.Good, 2);
        GUI.Label(new Rect(r.x + 16f, r.y + 6f, r.width - 24f, 20f), _toast, Theme.Label);
        GUI.Label(new Rect(r.x + 16f, r.y + 26f, r.width - 24f, 18f), _toastSub, Theme.Small);
    }

    internal static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) Plugin.Logger.LogWarning("[Autosave] " + message);
    }
}
