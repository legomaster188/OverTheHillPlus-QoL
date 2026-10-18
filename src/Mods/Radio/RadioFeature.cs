using System;
using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public;
using OverTheHillPlus.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Radio;

/// <summary>Radio's per-frame work (player and keys), its "now playing" notice and the Advanced card.</summary>
internal sealed class RadioFeature : IFeature
{
    private static GUIStyle _toastStyle;
    private static bool _loggedError;

    public void Register()
    {
        Registry.OnTick(Tick);
        Registry.OnGui(DrawNotice);
        Registry.Section(Registry.PlusMode, "Radio", DrawCard);
        CoreBehaviour.OnQuit += RadioPlayer.StopTrack;
    }

    private static void Tick()
    {
        try
        {
            RadioPlayer.Tick();
            Keys();
        }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            RadioPlugin.Logger.LogError("Radio tick failed (further errors suppressed): " + e);
        }
    }

    private static void Keys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        // Not while a key is being rebound, text is typed (menu, chat) or an IMGUI text box has the keyboard.
        if (MenuUx.HotkeysCaptured || KeybindEditor.Capturing || MenuUx.Typing || GUIUtility.keyboardControl != 0) return;
        if (!InWorld()) return;
        if (CoreBehaviour.Pressed(kb, RadioConfig.PowerKey.Value)) RadioPlayer.TogglePower();
        else if (!RadioConfig.Power.Value) return;
        else if (CoreBehaviour.Pressed(kb, RadioConfig.NextKey.Value)) RadioPlayer.NextTrack();
        else if (CoreBehaviour.Pressed(kb, RadioConfig.PrevKey.Value)) RadioPlayer.PrevTrack();
        else if (CoreBehaviour.Pressed(kb, RadioConfig.NextStationKey.Value)) RadioPlayer.NextStation();
        else if (CoreBehaviour.Pressed(kb, RadioConfig.PrevStationKey.Value)) RadioPlayer.PrevStation();
        else if (CoreBehaviour.Pressed(kb, RadioConfig.VolumeUpKey.Value)) RadioPlayer.StepVolume(0.05f);
        else if (CoreBehaviour.Pressed(kb, RadioConfig.VolumeDownKey.Value)) RadioPlayer.StepVolume(-0.05f);
    }

    /// <summary>
    /// Driving in a world, with no menu up: not on the MODS page, not in one of the game's own menus (they free the
    /// cursor; driving keeps it locked), and no game text field (its chat, a name box) has the keyboard.
    /// </summary>
    private static bool InWorld()
    {
        if (GameState.LocalCar == null || CoreBehaviour.MenuOpen) return false;
        if (Cursor.visible && Cursor.lockState != CursorLockMode.Locked) return false;
        try
        {
            var go = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (go != null)
            {
                var tmp = go.GetComponent<TMP_InputField>();
                if (tmp != null && tmp.isFocused) return false;
                var ui = go.GetComponent<UnityEngine.UI.InputField>();
                if (ui != null && ui.isFocused) return false;
            }
        }
        catch (Exception ex) { DebugLog.Error("radio", ex, "RadioFeature.InWorld"); /* no EventSystem right now: nothing is typing */ }
        return true;
    }

    private static void DrawCard()
    {
        Menu.Card("Radio", "Your own music while you drive: mp3, ogg and wav files in BepInEx/config/oth.radio, one station per sub-folder. Only you hear it.", () =>
        {
            Menu.KeyValue("Now", RadioPlayer.Status);
            Menu.KeyValue("Folder", RadioPlayer.Root ?? "?");
            Menu.KeyValue("Library", $"{RadioPlayer.Playlist?.Stations.Count ?? 0} station(s), {RadioPlayer.TrackCount} track(s)");
            Menu.Switch(RadioConfig.Power, "Radio on", null, true);
            Menu.SliderRow(RadioConfig.Volume, "Volume", 0f, 1f, "0", "%", true, percent: true);
            Menu.Switch(RadioConfig.Shuffle, "Shuffle", "Each station in a random order", true);
            Menu.Switch(RadioConfig.DuckForGameMusic, "Turn down for the game's music", null, true);
            Menu.SliderRow(RadioConfig.DuckLevel, "Volume while the game's music plays", 0f, 1f, "0", "%", RadioConfig.DuckForGameMusic.Value, percent: true);
            Menu.Switch(RadioConfig.FollowMasterVolume, "Follow the game's master volume", null, true);
            Menu.Switch(RadioConfig.ShowNowPlaying, "\"Now playing\" notice", null, true);
            Menu.Chips(2, ("Read the folder again", () => RadioPlayer.Rescan(announce: true), true),
                ("Next track", RadioPlayer.NextTrack, RadioConfig.Power.Value));
        });
        // The shuffle switch writes the setting; the playlist follows it here.
        if (RadioPlayer.Playlist != null && RadioPlayer.Playlist.Shuffle != RadioConfig.Shuffle.Value) RadioPlayer.SetShuffle(RadioConfig.Shuffle.Value);
    }

    private static void DrawNotice()
    {
        string msg = RadioPlayer.Notice;
        float age = Time.unscaledTime - RadioPlayer.NoticeAt;
        if (string.IsNullOrEmpty(msg) || age > 3.5f || age < 0f) return;
        if (CoreBehaviour.CleanView) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        Theme.Ensure();
        if (_toastStyle == null)
        {
            // Plain text: file names never get to apply rich-text tags.
            _toastStyle = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleCenter, richText = false, wordWrap = false, clipping = TextClipping.Clip };
            _toastStyle.normal.textColor = Theme.Text;
        }
        float alpha = Mathf.Clamp01(3.5f - age);
        var prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        var content = new GUIContent(msg);
        var size = _toastStyle.CalcSize(content);
        float w = Mathf.Min(size.x + 28f, Screen.width * 0.6f);
        var r = new Rect((Screen.width - w) / 2f, Screen.height - 120f, w, 34f);
        Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.88f), 8);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), Theme.Accent, 2);
        GUI.Label(r, content, _toastStyle);
        GUI.color = prev;
    }
}
