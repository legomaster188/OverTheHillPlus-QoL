using System;
using BepInEx.Configuration;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Public.Spectate;

/// <summary>
/// Lobby tab "Spectate": watch another player's car with your own chase camera (cycle players with keys, orbit with
/// the mouse, zoom with the wheel), while your car waits with the handbrake on. Local only: works as host and guest.
/// </summary>
internal sealed class SpectateFeature : IFeature
{
    private const string Section = "Spectate";

    public static ConfigEntry<Key> ToggleKey, NextKey, PrevKey;
    public static ConfigEntry<bool> Handbrake, AutoNext, ShowOverlay;
    public static ConfigEntry<float> CutDistance;

    public void Register()
    {
        var f = ModConfig.File;
        ToggleKey = f.Bind(Section, "ToggleKey", Key.Home, "Starts watching another player / goes back to your car. Key.None disables the hotkey.");
        NextKey = f.Bind(Section, "NextKey", Key.RightBracket, "While watching: next player.");
        PrevKey = f.Bind(Section, "PrevKey", Key.LeftBracket, "While watching: previous player.");
        Handbrake = f.Bind(Section, "Handbrake", true, "Put your car's handbrake on while you watch (released by the game when you give throttle again).");
        AutoNext = f.Bind(Section, "AutoNext", true, "When the watched player leaves, watch the next one instead of going back to your car.");
        ShowOverlay = f.Bind(Section, "ShowOverlay", true, "Show who you are watching at the top of the screen.");
        CutDistance = f.Bind(Section, "CutDistance", 60f, new ConfigDescription(
            "Switching to a car farther than this (metres) cuts the camera instead of gliding over.", new AcceptableValueRange<float>(0f, 2000f)));

        Registry.OnTick(Tick);
        Registry.OnGui(DrawOverlay);
        Registry.Section(Registry.PlusMode, Menu.LobbyTab, DrawCard, 20);
    }

    private static bool _loggedKeyError;

    private static void Tick()
    {
        HandleKeys();
        SpectateCamera.Tick();
    }

    private static void HandleKeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        try
        {
            if (MenuUx.HotkeysCaptured || MenuUx.MenuVisible) return;
            if (Pressed(kb, ToggleKey.Value)) { SpectateCamera.Toggle(); return; }
            if (!SpectateCamera.Active) return;
            int dir = Pressed(kb, NextKey.Value) ? 1 : Pressed(kb, PrevKey.Value) ? -1 : 0;
            // Only while our camera map has the input (not under the pause menu, map screen ...).
            if (dir != 0 && SpectateCamera.HasInputFocus) SpectateCamera.Cycle(dir);
        }
        catch (Exception e)
        {
            if (!_loggedKeyError) { _loggedKeyError = true; Plugin.Logger.LogWarning("Spectate hotkeys: " + e.Message); }
        }
    }

    private static bool Pressed(Keyboard kb, Key key)
    {
        if (key == Key.None) return false;
        try { return kb[key].wasPressedThisFrame; }
        catch { return false; }
    }

    private static string KeyName(Key key) => key switch
    {
        Key.None => "-",
        Key.LeftBracket => "[",
        Key.RightBracket => "]",
        _ => key.ToString(),
    };

    private static string Distance(float m) => m < 0f ? "?" : m >= 1000f ? $"{m / 1000f:0.0} km" : $"{m:0} m";

    // ---- menu ----

    private static void DrawCard()
    {
        Menu.Card("Spectate", "Follow another player's car with your camera. Only your view changes; your car waits with the handbrake on.", () =>
        {
            bool active = SpectateCamera.Active;
            string block = active ? null : SpectateCamera.BlockReason();

            if (active)
            {
                Menu.KeyValue("Watching", SpectateCamera.TargetName);
                Menu.KeyValue("Speed", $"{SpectateCamera.SpeedKmh:0} km/h");
                Menu.KeyValue("From your car", Distance(SpectateCamera.DistanceToOwnCar));
                Menu.Chips(3,
                    ("Previous", () => SpectateCamera.Cycle(-1), true),
                    ("Next", () => SpectateCamera.Cycle(1), true),
                    ("Back to my car", () => SpectateCamera.Stop("back to your car"), true));
            }
            else
            {
                Menu.KeyValue("Status", block ?? "ready");
            }

            var list = SpectateCamera.Candidates();
            if (list.Count > 0)
            {
                var chips = new (string, Action, bool)[Math.Min(list.Count, 16)];
                for (int i = 0; i < chips.Length; i++)
                {
                    var c = list[i];
                    string label = (active && c.Raw == SpectateCamera.TargetRaw ? "> " : "") + Trim(c.Name);
                    chips[i] = (label, () => SpectateCamera.Start(c.Raw), block == null || active);
                }
                Menu.Chips(2, chips);
                if (list.Count > chips.Length) Menu.Small($"{list.Count - chips.Length} more: use {KeyName(NextKey.Value)} / {KeyName(PrevKey.Value)} while watching.");
            }

            Menu.Small($"Keys: {KeyName(ToggleKey.Value)} start / back to your car, {KeyName(PrevKey.Value)} / {KeyName(NextKey.Value)} previous / next player. " +
                       "Mouse orbits, the wheel zooms. Rebind in the keybind list.");
            Menu.Switch(Handbrake, "Handbrake while watching", "Released by the game as soon as you give throttle", true);
            Menu.Switch(AutoNext, "Keep watching when a player leaves", "Otherwise the camera goes back to your car", true);
            Menu.Switch(ShowOverlay, "Show who you are watching", "Small banner at the top of the screen", true);
            Menu.SliderRow(CutDistance, "Cut instead of glide past", 0f, 500f, "0", " m", true);
        });
    }

    private static string Trim(string s)
    {
        if (string.IsNullOrEmpty(s)) return "?";
        return s.Length > 24 ? s.Substring(0, 23) + "…" : s;
    }

    // ---- overlay ----

    private static void DrawOverlay()
    {
        if (!SpectateCamera.Active || !ShowOverlay.Value) return;
        if (Photo.PhotoCapture.CleanViewActive) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        Theme.Ensure();
        float scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        float sw = Screen.width / scale;

        string title = "Watching  " + Trim(SpectateCamera.TargetName);
        string info = $"{SpectateCamera.Index} / {SpectateCamera.Count}   ·   {SpectateCamera.SpeedKmh:0} km/h   ·   {Distance(SpectateCamera.DistanceToOwnCar)} from your car";
        string keys = $"{KeyName(PrevKey.Value)} / {KeyName(NextKey.Value)} switch   ·   {KeyName(ToggleKey.Value)} back to your car   ·   mouse orbits, wheel zooms";

        var r = new Rect(sw / 2f - 260f, 18f, 520f, 70f);
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.82f), 10);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), Theme.Accent, 2);
        GUI.Label(new Rect(r.x + 16f, r.y + 6f, r.width - 24f, 22f), title, Theme.Label);
        GUI.Label(new Rect(r.x + 16f, r.y + 28f, r.width - 24f, 18f), info, Theme.Small);
        GUI.Label(new Rect(r.x + 16f, r.y + 46f, r.width - 24f, 18f), keys, Theme.Small);
    }
}
