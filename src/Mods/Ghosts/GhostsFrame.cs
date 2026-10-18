using System;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Ghosts;

/// <summary>
/// Ghost Replays' per-frame work on core's hooks: the recorder and the player after the features' ticks, the record
/// hotkey, and a small on-screen line while recording or playing (plus short notices such as "New best ghost").
/// </summary>
internal static class GhostsFrame
{
    private static bool _loggedError;
    private static string _notice;
    private static float _noticeUntil;

    internal static void Install()
    {
        CoreBehaviour.AfterFeatureTicks += Tick;
        CoreBehaviour.Hotkeys += Hotkeys;
        CoreBehaviour.AfterGuis += Draw;
    }

    /// <summary>A line on screen for a few seconds (and in the menu's status bar).</summary>
    internal static void Notice(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _notice = text;
        _noticeUntil = Time.unscaledTime + 4f;
        Menu.SetStatus(text);
    }

    /// <summary>Starts a free-drive recording, or stops and saves the one running.</summary>
    internal static void ToggleRecording()
    {
        if (GhostRecorder.Recording)
        {
            SaveDrive(GhostRecorder.Stop(), null);
            return;
        }
        string why = GhostRecorder.Start();
        Notice(why ?? "Recording your drive.");
    }

    /// <summary>Saves a free drive as "Drive date time"; anything under a second is dropped.</summary>
    internal static void SaveDrive(GhostRecording rec, string reason)
    {
        if (rec == null) return;
        if (rec.Duration < 1f) { Notice((reason != null ? reason + " " : "") + "Too short to keep."); return; }
        string name = GhostLibrary.Save(rec, "Drive " + DateTime.Now.ToString("yyyy-MM-dd HH.mm"));
        Notice((reason != null ? reason + " " : "") + (name != null ? $"Saved \"{name}\" ({GhostTime.Clock(rec.Duration)})." : "Couldn't save it (see the log)."));
    }

    private static void Tick()
    {
        // Caught here: a throw would otherwise skip the other mods' hooks this frame.
        try
        {
            var done = GhostRecorder.Tick(out string why);
            if (done != null)
            {
                // A race recording that ends by itself (left the car) isn't a finished race: dropped.
                if (string.IsNullOrEmpty(done.TrackId)) SaveDrive(done, why);
                else Notice(why);
            }
            string ended = GhostPlayer.Tick(out bool raceGhost);
            if (ended != null && !raceGhost) Notice(ended); // a race's ghost ends quietly
        }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            GhostsPlugin.Logger.LogError("Ghosts tick failed (further errors suppressed): " + e);
        }
    }

    private static void Hotkeys(Keyboard kb)
    {
        if (GhostsConfig.RecordKey != null && CoreBehaviour.Pressed(kb, GhostsConfig.RecordKey.Value)) ToggleRecording();
    }

    private static void Draw()
    {
        if (CoreBehaviour.GuiHidden || CoreBehaviour.CleanView || Event.current == null || Event.current.type != EventType.Repaint) return;
        try
        {
            string line = null;
            if (GhostRecorder.Recording) line = $"● REC {GhostTime.Clock(GhostRecorder.Seconds)}";
            if (GhostPlayer.Playing != null)
            {
                string g = $"Ghost {GhostTime.Clock(GhostPlayer.Seconds)} / {GhostTime.Clock(GhostPlayer.Duration)}";
                line = line == null ? g : line + "    " + g;
            }
            float y = Screen.height * 0.08f;
            if (line != null) y += OverlayDraw.Label(Screen.width * 0.5f, y, line, 16, new Color(1f, 0.45f, 0.4f, 0.95f)) + 4f;
            if (_notice != null && Time.unscaledTime < _noticeUntil)
                OverlayDraw.Label(Screen.width * 0.5f, y, _notice, 16, new Color(0.75f, 0.92f, 1f, 0.95f));
        }
        catch (Exception ex) { DebugLog.Error("ghosts", ex, "GhostsFrame.Draw"); }
    }
}
