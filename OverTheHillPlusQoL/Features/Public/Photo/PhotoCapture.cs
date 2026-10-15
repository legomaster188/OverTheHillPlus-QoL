using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.UI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.Photo;

/// <summary>
/// High-resolution screenshots (ScreenCapture with a supersize factor) with every overlay hidden for the shot,
/// plus "clean view" (game HUD and the mod's driving HUD hidden while you line up a picture).
///
/// A shot is a small state machine run from the per-frame tick: hide UI, let one frame render without it,
/// ask Unity to capture at the end of the next frame, restore the UI two frames later, then wait for the file.
/// Everything that is hidden is restored exactly (only what we hid), and a watchdog makes sure the
/// mod's own IMGUI can never stay suppressed if something goes wrong.
/// </summary>
internal static class PhotoCapture
{
    private enum Phase { Idle, Requested, Hidden, Captured, Verifying }

    private static Phase _phase;
    private static int _phaseFrame;
    private static float _captureStartedAt = -100f;
    private static float _verifyUntil;
    private static string _pendingPath;
    private static int _pendingW, _pendingH, _pendingFactor;

    private static readonly List<Canvas> HiddenCanvases = new();
    private static IntPtr _shotHudPtr;
    private static bool _excludedHidden;

    // Clean view: one ShowHUD(false) request held on the current HUDManager.
    private static IntPtr _cleanHudPtr;
    private static float _nextCleanCheck;

    private static string _folder;
    private static bool _loggedFolder;

    public static string LastFile { get; private set; }
    public static string LastInfo { get; private set; }
    public static float LastAt { get; private set; } = -100f;
    public static bool LastFailed { get; private set; }

    /// <summary>
    /// True for the few frames of a capture: the mod draws no IMGUI then (menu, HUD, overlays).
    /// Self-expires after 3 s so a failure can never leave the menu invisible.
    /// </summary>
    public static bool Capturing => (_phase is Phase.Requested or Phase.Hidden or Phase.Captured)
                                    && Time.unscaledTime - _captureStartedAt < 3f;

    public static bool Busy => _phase != Phase.Idle;

    /// <summary>Clean view is on: the mod's driving HUD is hidden as well.</summary>
    public static bool CleanViewActive => PhotoConfig.CleanView != null && PhotoConfig.CleanView.Value;

    /// <summary>Where shots go: the game's own screenshot folder (Documents/My Games/over the hill/Screenshots).</summary>
    public static string Folder
    {
        get
        {
            if (!string.IsNullOrEmpty(_folder)) return _folder;
            string dir = null;
            try { dir = FileSystemUtils.ScreenshotLocation(false); }
            catch (Exception e) { Plugin.Logger.LogDebug("Photo: game screenshot folder unavailable: " + e.Message); }
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "My Games", "over the hill", "Screenshots");
            }
            _folder = dir;
            if (!_loggedFolder) { _loggedFolder = true; Plugin.Logger.LogInfo("Photo: screenshots go to " + dir); }
            return _folder;
        }
    }

    public const int MaxFactor = 4;
    /// <summary>Largest output in pixels: ~8K for the game's own capture, ~33 MP when re-rendering the camera (which
    /// holds a render texture, a CPU copy and the PNG at once). Bigger shots risk running out of video memory, which
    /// crashes the game outright instead of throwing.</summary>
    private const long MaxPixels = 64_000_000, MaxPixelsReRender = 33_000_000;

    /// <summary>The supersize factor that will actually be used (kept under the 16384 px side limit and the pixel cap).</summary>
    public static int EffectiveFactor
    {
        get
        {
            int n = Mathf.Clamp(PhotoConfig.SuperSize.Value, 1, MaxFactor);
            long w = Mathf.Max(1, Screen.width), h = Mathf.Max(1, Screen.height);
            long cap = PhotoConfig.ReRenderCamera.Value ? MaxPixelsReRender : MaxPixels;
            while (n > 1 && (w * n > 16384 || h * n > 16384 || w * n * h * n > cap)) n--;
            return n;
        }
    }

    public static string ResolutionText(int n) => $"{Screen.width * n}×{Screen.height * n}";

    public static void Request()
    {
        if (_phase != Phase.Idle)
        {
            Menu.SetStatus("Blocked: a screenshot is already being taken");
            return;
        }
        _phase = Phase.Requested;
        _captureStartedAt = Time.unscaledTime;
    }

    public static void OpenFolder()
    {
        try
        {
            string dir = Folder;
            Directory.CreateDirectory(dir);
            Application.OpenURL("file:///" + dir.Replace('\\', '/'));
        }
        catch (Exception e) { Menu.SetStatus("Failed to open the folder: " + e.Message); }
    }

    // ---- per frame ----

    public static void Tick()
    {
        try { TickCapture(); }
        catch (Exception e)
        {
            Plugin.Logger.LogError("Photo capture: " + e);
            SafeRestore();
            Fail("Failed to take the screenshot: " + e.Message);
        }
        try { TickCleanView(); }
        catch (Exception e) { Plugin.Logger.LogDebug("Photo clean view: " + e.Message); }
    }

    private static void TickCapture()
    {
        switch (_phase)
        {
            case Phase.Idle:
                return;

            case Phase.Requested:
                HideForShot();
                _phase = Phase.Hidden;
                _phaseFrame = Time.frameCount;
                return;

            case Phase.Hidden:
                // Let one whole frame render without UI first (canvas rebuilds, alpha changes).
                if (Time.frameCount <= _phaseFrame) return;
                Shoot();
                _phase = Phase.Captured;
                _phaseFrame = Time.frameCount;
                return;

            case Phase.Captured:
                // The capture happens at the end of the frame it was requested in; give it one more for safety.
                if (Time.frameCount < _phaseFrame + 2) return;
                SafeRestore();
                _phase = Phase.Verifying;
                _verifyUntil = Time.unscaledTime + 8f;
                return;

            case Phase.Verifying:
                if (_pendingPath != null && File.Exists(_pendingPath) && TryReadPngSize(_pendingPath, out int w, out int h))
                {
                    LastFile = _pendingPath;
                    LastAt = Time.unscaledTime;
                    LastFailed = false;
                    if (w == _pendingW && h == _pendingH || _pendingFactor == 1)
                        LastInfo = $"{w}×{h} ({_pendingFactor}x)";
                    else
                    {
                        // Some render setups ignore Unity's supersize; say so instead of pretending.
                        LastInfo = $"{w}×{h}: supersize was ignored, try the 'Re-render camera' method";
                        Plugin.Logger.LogWarning($"Photo: asked for {_pendingW}×{_pendingH}, Unity wrote {w}×{h}");
                    }
                    Menu.SetStatus($"Saved {Path.GetFileName(_pendingPath)} · {LastInfo}");
                    Plugin.Logger.LogInfo($"Photo: saved {_pendingPath} ({LastInfo})");
                    _pendingPath = null;
                    _phase = Phase.Idle;
                }
                else if (Time.unscaledTime > _verifyUntil)
                {
                    Fail("Failed: the screenshot file did not appear (try a smaller size)");
                }
                return;
        }
    }

    private static void Fail(string message)
    {
        LastFailed = true;
        LastInfo = message;
        LastAt = Time.unscaledTime;
        Menu.SetStatus(message);
        Plugin.Logger.LogWarning("Photo: " + message);
        _pendingPath = null;
        _phase = Phase.Idle;
    }

    private static void Shoot()
    {
        string dir = Folder;
        Directory.CreateDirectory(dir);
        int n = EffectiveFactor;
        string name = $"OverTheHillPlus_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{n}x.png";
        _pendingPath = Path.Combine(dir, name);
        _pendingFactor = n;
        _pendingW = Screen.width * n;
        _pendingH = Screen.height * n;
        if (PhotoConfig.ReRenderCamera.Value) ShootCamera(_pendingPath, n);
        else ScreenCapture.CaptureScreenshot(_pendingPath, n, ScreenCapture.StereoScreenCaptureMode.LeftEye);
    }

    /// <summary>
    /// Experimental alternative: renders the main camera once into a big render texture and encodes it ourselves.
    /// Only the 3D view (no screen-space UI, which is hidden for shots anyway). Synchronous: the game hitches once.
    /// </summary>
    private static void ShootCamera(string path, int n)
    {
        var cam = Camera.main;
        if (cam == null) throw new InvalidOperationException("no main camera to render");
        int w = Screen.width * n, h = Screen.height * n;
        var prevTarget = cam.targetTexture;
        var prevActive = RenderTexture.active;
        RenderTexture rt = null;
        Texture2D tex = null;
        try
        {
            rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
            tex.Apply(false);
            // Free the render texture before encoding, so the GPU copy and the PNG buffers are never alive together.
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            rt.Release();
            Object.Destroy(rt);
            rt = null;
            var png = ImageConversion.EncodeToPNG(tex);
            Object.Destroy(tex);
            tex = null;
            if (png == null || png.Length == 0) throw new InvalidOperationException("PNG encoding returned nothing");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            fs.Write(png.AsSpan());
        }
        finally
        {
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            if (rt != null) { rt.Release(); Object.Destroy(rt); }
            if (tex != null) Object.Destroy(tex);
        }
    }

    /// <summary>Reads width and height from a PNG's IHDR chunk (bytes 16..23, big-endian).</summary>
    private static bool TryReadPngSize(string path, out int w, out int h)
    {
        w = h = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var b = new byte[24];
            int read = 0;
            while (read < 24)
            {
                int r = fs.Read(b, read, 24 - read);
                if (r <= 0) return false;
                read += r;
            }
            if (b[0] != 0x89 || b[1] != (byte)'P' || b[12] != (byte)'I' || b[13] != (byte)'H') return false;
            w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            return w > 0 && h > 0;
        }
        catch { return false; } // still being written: try again next frame
    }

    // ---- hiding UI for the shot ----

    private static void HideForShot()
    {
        HiddenCanvases.Clear();
        _shotHudPtr = IntPtr.Zero;
        _excludedHidden = false;
        if (!PhotoConfig.HideGameUiInShots.Value) return;

        try
        {
            var hud = HUDManager.Instance;
            if (hud != null)
            {
                hud.ShowHUD(false, new Il2CppReferenceArray<CanvasGroup>(0));
                _shotHudPtr = hud.Pointer;
            }
        }
        catch (Exception e) { Plugin.Logger.LogDebug("Photo: hide HUD: " + e.Message); }

        // The game's own "hide for screenshot" elements (what its photo mode hides when it saves a picture).
        try { ExcludedFromScreenshotElement.Invoke(false); _excludedHidden = true; }
        catch (Exception e) { Plugin.Logger.LogDebug("Photo: excluded elements: " + e.Message); }

        try
        {
            bool worldToo = PhotoConfig.HideNameTagsInShots.Value;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Canvas>()))
            {
                var c = o.TryCast<Canvas>();
                if (c == null || !c.enabled || !c.isRootCanvas) continue;
                if (c.renderMode == RenderMode.WorldSpace && !worldToo) continue;
                c.enabled = false;
                HiddenCanvases.Add(c);
            }
        }
        catch (Exception e) { Plugin.Logger.LogDebug("Photo: hide canvases: " + e.Message); }
    }

    private static void SafeRestore()
    {
        for (int i = 0; i < HiddenCanvases.Count; i++)
        {
            try
            {
                var c = HiddenCanvases[i];
                if (c != null) c.enabled = true;
            }
            catch { }
        }
        HiddenCanvases.Clear();

        if (_excludedHidden)
        {
            _excludedHidden = false;
            try { ExcludedFromScreenshotElement.Invoke(true); }
            catch (Exception e) { Plugin.Logger.LogDebug("Photo: restore excluded elements: " + e.Message); }
        }

        if (_shotHudPtr != IntPtr.Zero)
        {
            try
            {
                var hud = HUDManager.Instance;
                // Only undo our request on the same HUD instance (a new scene starts with a fresh counter).
                if (hud != null && hud.Pointer == _shotHudPtr) hud.ShowHUD(true, new Il2CppReferenceArray<CanvasGroup>(0));
            }
            catch (Exception e) { Plugin.Logger.LogDebug("Photo: restore HUD: " + e.Message); }
            _shotHudPtr = IntPtr.Zero;
        }
    }

    // ---- clean view ----

    private static void TickCleanView()
    {
        bool want = PhotoConfig.CleanView.Value;
        if (!want && _cleanHudPtr == IntPtr.Zero) return;
        // Cheap enough per change, but the HUD instance itself is only re-checked twice a second.
        bool changed = want != (_cleanHudPtr != IntPtr.Zero);
        if (!changed && Time.unscaledTime < _nextCleanCheck) return;
        _nextCleanCheck = Time.unscaledTime + 0.5f;

        var hud = HUDManager.Instance;
        IntPtr ptr = hud == null ? IntPtr.Zero : hud.Pointer;
        var none = new Il2CppReferenceArray<CanvasGroup>(0);

        if (want)
        {
            if (ptr == IntPtr.Zero || ptr == _cleanHudPtr) return;
            // New HUD (first time, or after a scene change: the old one and its counter are gone).
            hud.ShowHUD(false, none);
            _cleanHudPtr = ptr;
        }
        else
        {
            if (ptr != IntPtr.Zero && ptr == _cleanHudPtr) hud.ShowHUD(true, none);
            _cleanHudPtr = IntPtr.Zero;
        }
    }

    // ---- overlays (never drawn into a shot: the mod draws no IMGUI while Capturing) ----

    public static void DrawOverlays()
    {
        if (Capturing) return;
        bool grid = PhotoConfig.ThirdsGrid.Value;
        bool toast = PhotoConfig.ShowSavedToast.Value && Time.unscaledTime - LastAt < 4f && !string.IsNullOrEmpty(LastInfo);
        if (!grid && !toast) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        var prevMatrix = GUI.matrix;
        var prevColor = GUI.color;
        GUI.matrix = Matrix4x4.identity;
        try
        {
            if (grid) DrawGrid();
            if (toast) DrawToast();
        }
        finally
        {
            GUI.matrix = prevMatrix;
            GUI.color = prevColor;
        }
    }

    private static void DrawGrid()
    {
        float w = Screen.width, h = Screen.height;
        float t = Mathf.Max(1f, Mathf.Round(h / 1080f));
        GUI.color = new Color(1f, 1f, 1f, 0.35f);
        var tex = Texture2D.whiteTexture;
        for (int i = 1; i <= 2; i++)
        {
            GUI.DrawTexture(new Rect(w * i / 3f - t / 2f, 0f, t, h), tex);
            GUI.DrawTexture(new Rect(0f, h * i / 3f - t / 2f, w, t), tex);
        }
    }

    private static void DrawToast()
    {
        Theme.Ensure();
        float scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        float sw = Screen.width / scale, sh = Screen.height / scale;
        string title = LastFailed ? "Screenshot failed" : "Screenshot saved";
        string sub = LastFailed ? LastInfo : $"{LastInfo}  ·  {Path.GetFileName(LastFile ?? "")}";
        float age = Time.unscaledTime - LastAt;
        float alpha = Mathf.Clamp01(4f - age); // fades over the last second
        var r = new Rect(sw / 2f - 230f, sh - 110f, 460f, 52f);
        GUI.color = new Color(1f, 1f, 1f, alpha);
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.9f), 10);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), LastFailed ? Theme.Bad : Theme.Good, 2);
        GUI.Label(new Rect(r.x + 16f, r.y + 7f, r.width - 24f, 20f), title, Theme.Label);
        GUI.Label(new Rect(r.x + 16f, r.y + 27f, r.width - 24f, 18f), sub, Theme.Small);
    }
}
