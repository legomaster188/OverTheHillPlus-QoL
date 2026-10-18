using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace OverTheHillPlus.AutoShot;

/// <summary>
/// Takes one screenshot with the mods' overlays hidden. ScreenCapture.CaptureScreenshot (asked for in the frame's
/// update) writes the frame at its end, after OnGUI; core draws no mod IMGUI while <see cref="Hiding"/> is true
/// (CoreBehaviour.AddHideGuiCheck), so neither this frame nor the next shows any. The flag expires by itself after
/// 1.5 s so a failure can never leave the mods' HUD hidden.
/// </summary>
internal static class AutoShotCapture
{
    private const int HideFrames = 2;
    private const float HideAtMost = 1.5f;
    private const float WaitForFile = 8f;

    private static bool _busy, _verifying;
    private static float _verifyUntil;
    private static int _frame;
    private static float _startedAt = -100f;
    private static string _pending;

    public static bool Hiding => _busy && !_verifying && Time.unscaledTime - _startedAt < HideAtMost;
    /// <summary>A screenshot is being taken or its file is still being written.</summary>
    public static bool Busy => _busy;

    public static int Count { get; private set; }
    public static string LastFile { get; private set; }
    public static ShotReason LastReason { get; private set; }
    /// <summary>Unscaled time the last screenshot's file was found written, for the on-screen note.</summary>
    public static float LastDoneAt { get; private set; } = -100f;
    public static string LastError { get; private set; }

    public static string FolderPath
    {
        get
        {
            switch (AutoShotConfig.SaveTo.Value)
            {
                case AutoShotConfig.Folder.Pictures:
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Over the Hill");
                case AutoShotConfig.Folder.GameScreenshots:
                    try
                    {
                        string dir = FileSystemUtils.ScreenshotLocation(false);
                        if (!string.IsNullOrEmpty(dir)) return dir;
                    }
                    catch (Exception ex) { DebugLog.Error("autoshot", ex, "AutoShotCapture.FolderPath"); }
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "over the hill", "Screenshots");
                default:
                    return Path.Combine(Paths.ConfigPath, "oth.screenshots");
            }
        }
    }

    /// <summary>Starts a screenshot now; false when one is already being taken or the file can't be prepared.</summary>
    public static bool Take(ShotReason reason)
    {
        if (_busy) return false;
        try
        {
            string dir = FolderPath;
            Directory.CreateDirectory(dir);
            var now = DateTime.Now;
            string path = Path.Combine(dir, ShotRules.FileName(now, reason));
            for (int i = 1; File.Exists(path) && i < 100; i++) path = Path.Combine(dir, ShotRules.FileName(now, reason, i));
            ScreenCapture.CaptureScreenshot(path, 1);
            _busy = true;
            _verifying = false;
            _frame = Time.frameCount;
            _startedAt = Time.unscaledTime;
            _pending = path;
            LastReason = reason;
            LastError = null;
            DebugLog.Verbose(AutoShotPlugin.ModId, () => $"screenshot started ({ShotRules.Describe(reason)}): {System.IO.Path.GetFileName(path)}");
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("autoshot", e, "AutoShotCapture.Take");
            LastError = e.Message;
            AutoShotPlugin.Logger.LogWarning("Auto Screenshot: could not take a screenshot: " + e.Message);
            return false;
        }
    }

    internal static void Tick()
    {
        if (!_busy) return;
        if (!_verifying)
        {
            // Overlays back after the captured frame; then wait for Unity to write the file.
            if (Time.frameCount < _frame + HideFrames && Time.unscaledTime - _startedAt < HideAtMost) return;
            _verifying = true;
            _verifyUntil = Time.unscaledTime + WaitForFile;
        }
        bool written = false;
        try { written = File.Exists(_pending) && new FileInfo(_pending).Length > 0; }
        catch (Exception ex) { DebugLog.Error("autoshot", ex, "AutoShotCapture.Tick"); }
        if (!written)
        {
            if (Time.unscaledTime < _verifyUntil) return;
            LastError = "the screenshot file did not appear: " + _pending;
            DebugLog.Error(AutoShotPlugin.ModId, LastError);
            AutoShotPlugin.Logger.LogWarning("Auto Screenshot: " + LastError);
            _busy = _verifying = false;
            _pending = null;
            return;
        }
        _busy = _verifying = false;
        LastFile = _pending;
        _pending = null;
        Count++;
        LastDoneAt = Time.unscaledTime;
        DebugLog.Info(AutoShotPlugin.ModId, $"screenshot saved ({ShotRules.Describe(LastReason)}): {System.IO.Path.GetFileName(LastFile)}; {Count} this session");
        AutoShotPlugin.Logger.LogInfo($"Auto Screenshot ({ShotRules.Describe(LastReason)}): {LastFile}");
    }

    public static void OpenFolder()
    {
        try
        {
            string dir = FolderPath;
            Directory.CreateDirectory(dir);
            Application.OpenURL("file:///" + dir.Replace('\\', '/'));
        }
        catch (Exception e) { DebugLog.Error("autoshot", e, "AutoShotCapture.OpenFolder"); Menu.SetStatus("Could not open the folder: " + e.Message); }
    }
}
