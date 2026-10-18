using System;
using BepInEx.Configuration;
using OverTheHillPlus.CheatsPlus;
using UnityEngine;
using UnityEngine.InputSystem;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.Features.Cheats.Fun;

/// <summary>
/// MODS > Cheats+ > Fun: physics toys for your own car. Moon gravity, wall-climb (sticky), drift mode,
/// bouncy car, unflippable car and slow motion.
///
/// Policy: every car change touches only the LOCAL car's rigidbody and only where this game simulates it
/// (solo or hosting, <see cref="GameState.PhysicsAllowed"/>). Gravity is a per-car extra force, never the global
/// Physics.gravity, so other players' cars (which the host also simulates) are untouched. Slow motion changes the
/// simulation clock for everyone in the session, so it is limited to single-player.
/// The physics itself runs in <see cref="FunPhysicsStep"/> (car physics step) and the patches next to it.
/// </summary>
internal sealed class FunPhysics : IFeature
{
    internal const string Tab = "Fun";

    // Moon / low gravity
    internal static ConfigEntry<bool> Moon;
    internal static ConfigEntry<float> GravityScale;
    // Wall-climb / sticky
    internal static ConfigEntry<bool> WallClimb;
    internal static ConfigEntry<float> StickStrength;
    internal static ConfigEntry<float> StickReach;
    internal static ConfigEntry<bool> ClimbAssist;
    // Drift
    internal static ConfigEntry<bool> Drift;
    internal static ConfigEntry<float> DriftRearGrip;
    internal static ConfigEntry<float> DriftFrontGrip;
    internal static ConfigEntry<Key> DriftHoldKey;
    // Bouncy
    internal static ConfigEntry<bool> Bouncy;
    internal static ConfigEntry<float> Bounciness;
    internal static ConfigEntry<bool> BouncyLandings;
    // Unflippable
    internal static ConfigEntry<bool> Unflippable;
    internal static ConfigEntry<float> UprightStrength;
    internal static ConfigEntry<bool> AutoRecover;
    // Slow motion
    internal static ConfigEntry<bool> SlowMo;
    internal static ConfigEntry<float> SlowMoScale;
    internal static ConfigEntry<Key> SlowMoKey;
    // Misc
    internal static ConfigEntry<bool> ShowOverlay;
    internal static ConfigEntry<Key> AllOffKey;

    /// <summary>True while the drift hold key (if any) is held. Read from the physics step.</summary>
    internal static bool DriftKeyHeld = true;

    public void Register()
    {
        var f = CheatsConfig.File;
        const string s = "Fun";
        Moon = f.Bind(s, "MoonGravity", false, "Low gravity for your own car only (solo/host).");
        GravityScale = f.Bind(s, "GravityScale", 0.17f, new ConfigDescription("Gravity felt by your car (1 = normal, 0.17 = moon).", new AcceptableValueRange<float>(0f, 3f)));
        WallClimb = f.Bind(s, "WallClimb", false, "Sticky tyres: the car is pulled onto whatever surface it drives on, walls and ceilings included.");
        StickStrength = f.Bind(s, "StickStrength", 1.6f, new ConfigDescription("How hard the car is pressed onto the surface, in g.", new AcceptableValueRange<float>(0.3f, 5f)));
        StickReach = f.Bind(s, "StickReach", 3f, new ConfigDescription("How far below the car (metres) a surface still pulls it in while airborne.", new AcceptableValueRange<float>(0.5f, 8f)));
        ClimbAssist = f.Bind(s, "ClimbAssist", true, "Tilts the car onto a wall it is driving into so it can drive up it.");
        Drift = f.Bind(s, "DriftMode", false, "Lowers rear tyre grip so the tail slides out.");
        DriftRearGrip = f.Bind(s, "DriftRearGrip", 0.45f, new ConfigDescription("Rear tyre grip while drifting (1 = normal).", new AcceptableValueRange<float>(0.05f, 1f)));
        DriftFrontGrip = f.Bind(s, "DriftFrontGrip", 1.1f, new ConfigDescription("Front tyre grip while drifting (1 = normal).", new AcceptableValueRange<float>(0.5f, 2f)));
        DriftHoldKey = f.Bind(s, "DriftHoldKey", Key.None, "If set, drift mode is only active while this key is held (None = always on).");
        Bouncy = f.Bind(s, "Bouncy", false, "The car bounces off whatever it hits.");
        Bounciness = f.Bind(s, "Bounciness", 0.8f, new ConfigDescription("Fraction of impact speed returned as bounce.", new AcceptableValueRange<float>(0.1f, 1.6f)));
        BouncyLandings = f.Bind(s, "BouncyLandings", true, "Also bounce when landing on the wheels after a jump.");
        Unflippable = f.Bind(s, "Unflippable", false, "Keeps the car wheels-down: rights itself in the air and on its side.");
        UprightStrength = f.Bind(s, "UprightStrength", 1f, new ConfigDescription("How hard the car is turned back upright.", new AcceptableValueRange<float>(0.2f, 4f)));
        AutoRecover = f.Bind(s, "AutoRecover", true, "If the car still ends up on its roof and stops, put it back on its wheels after a moment.");
        SlowMo = f.Bind(s, "SlowMotion", false, "Slow motion (single-player only: it slows the whole simulation).");
        SlowMoScale = f.Bind(s, "SlowMotionScale", 0.4f, new ConfigDescription("Game speed in slow motion.", new AcceptableValueRange<float>(0.05f, 1f)));
        SlowMoKey = f.Bind(s, "SlowMotionKey", Key.None, "Hotkey that toggles slow motion (None = no hotkey).");
        ShowOverlay = f.Bind(s, "ShowOverlay", true, "Shows a small line at the top of the screen listing the active fun modes.");
        AllOffKey = f.Bind(s, "AllOffKey", Key.None, "Hotkey that turns every fun mode off (None = no hotkey).");

        Registry.Section(Registry.PlusMode, Tab, DrawMenu, 0);
        Registry.OnTick(Tick);
        Registry.OnGui(DrawOverlay);
    }

    // ---- state helpers ----

    internal static bool AnyCarModeOn =>
        Moon.Value || WallClimb.Value || Drift.Value || Bouncy.Value || Unflippable.Value;

    internal static void AllOff()
    {
        Moon.Value = false;
        WallClimb.Value = false;
        Drift.Value = false;
        Bouncy.Value = false;
        Unflippable.Value = false;
        SlowMo.Value = false;
    }

    private static bool Pressed(Keyboard kb, Key key)
    {
        if (key == Key.None) return false;
        try { return kb[key].wasPressedThisFrame; } catch (Exception ex) { DebugLog.Error("cheats", ex, "FunPhysics.Pressed"); return false; }
    }

    private static bool Held(Keyboard kb, Key key)
    {
        try { return kb[key].isPressed; } catch (Exception ex) { DebugLog.Error("cheats", ex, "FunPhysics.Held"); return false; }
    }

    // ---- per frame ----

    private static void Tick()
    {
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (Pressed(kb, SlowMoKey.Value)) SlowMo.Value = !SlowMo.Value;
            if (Pressed(kb, AllOffKey.Value)) AllOff();
            DriftKeyHeld = DriftHoldKey.Value == Key.None || Held(kb, DriftHoldKey.Value);
        }
        else DriftKeyHeld = DriftHoldKey.Value == Key.None;

        SlowMotion.Tick();
        FunPhysicsStep.RunQueued();
    }

    // ---- menu ----

    private static void DrawMenu()
    {
        bool phys = GameState.PhysicsAllowed;
        if (!phys)
            Menu.Banner("Physics fun only works where your game simulates your car: solo or hosting. As a guest the host's simulation overrides it.", Theme.Warn);
        else if (GameState.LocalCar == null)
            Menu.Banner("No car yet. Settings apply as soon as you are driving.", Theme.Info);

        Menu.Card("Moon gravity", "Extra force on your car only. Other players and props keep normal gravity.", () =>
        {
            Menu.Switch(Moon, "Low gravity", "Floaty jumps and long airtime", phys);
            Menu.SliderRow(GravityScale, "Gravity", 0f, 3f, "0.00", " g", phys && Moon.Value);
            Menu.Chips(5,
                ("Zero", () => SetGravity(0f), phys),
                ("Moon", () => SetGravity(0.17f), phys),
                ("Mars", () => SetGravity(0.38f), phys),
                ("Earth", () => SetGravity(1f), phys),
                ("Heavy", () => SetGravity(2f), phys));
        });

        Menu.Card("Wall-climb", "Tyres stick to any surface. Gravity is cancelled while stuck, so you can drive up walls and onto ceilings.", () =>
        {
            Menu.Switch(WallClimb, "Sticky tyres", "Drive straight at a wall to climb it", phys);
            Menu.SliderRow(StickStrength, "Stick force", 0.3f, 5f, "0.0", " g", phys && WallClimb.Value);
            Menu.SliderRow(StickReach, "Reach in the air", 0.5f, 8f, "0.0", " m", phys && WallClimb.Value);
            Menu.Switch(ClimbAssist, "Climb assist", "Tilts the car onto a wall in front of it", phys && WallClimb.Value);
            Menu.KeyValue("Status", !WallClimb.Value ? "off" : FunPhysicsStep.Stuck ? $"stuck ({FunPhysicsStep.StuckWheels} wheels on surface)" : "free (no surface in reach)");
        });

        Menu.Card("Drift mode", "Lowers rear tyre grip (stacks with Driving > grip).", () =>
        {
            Menu.Switch(Drift, "Drift mode", DriftHoldKey.Value == Key.None ? "Always on while enabled" : $"Active while holding {DriftHoldKey.Value}", phys);
            Menu.SliderRow(DriftRearGrip, "Rear grip", 0.05f, 1f, "0.00", "", phys && Drift.Value, percent: true);
            Menu.SliderRow(DriftFrontGrip, "Front grip", 0.5f, 2f, "0.00", "", phys && Drift.Value, percent: true);
            Menu.Chips(3,
                ("Gentle", () => { DriftRearGrip.Value = 0.7f; DriftFrontGrip.Value = 1f; }, phys),
                ("Sideways", () => { DriftRearGrip.Value = 0.45f; DriftFrontGrip.Value = 1.1f; }, phys),
                ("Ice rink", () => { DriftRearGrip.Value = 0.15f; DriftFrontGrip.Value = 0.8f; }, phys));
            Menu.KeyValue("Rear wheels", FunPhysicsStep.RearWheelSummary);
        });

        Menu.Card("Bouncy car", "Hits and hard landings throw the car back off the surface.", () =>
        {
            Menu.Switch(Bouncy, "Bouncy", "Rubber-ball car", phys);
            Menu.SliderRow(Bounciness, "Bounciness", 0.1f, 1.6f, "0.00", "", phys && Bouncy.Value, percent: true);
            Menu.Switch(BouncyLandings, "Bouncy landings", "Bounce on the wheels after a jump too", phys && Bouncy.Value);
        });

        Menu.Card("Unflippable", "Rights the car in the air and when it tips past its side. Paused while wall-climb holds the car.", () =>
        {
            Menu.Switch(Unflippable, "Unflippable", "Always lands on its wheels", phys);
            Menu.SliderRow(UprightStrength, "Strength", 0.2f, 4f, "0.0", "x", phys && Unflippable.Value);
            Menu.Switch(AutoRecover, "Auto recover", "Flips it back if it still stops on its roof", phys && Unflippable.Value);
            Menu.KeyValue("Tilt", GameState.LocalCar == null ? "-" : $"{FunPhysicsStep.TiltDegrees:0} deg");
        });

        bool solo = SlowMotion.IsSolo;
        Menu.Card("Slow motion", "Single-player only: slows the whole game, so it is not offered in multiplayer.", () =>
        {
            if (!solo) Menu.Small("Not in a single-player game right now.");
            Menu.Switch(SlowMo, "Slow motion", SlowMoKey.Value == Key.None ? "Set SlowMotionKey in the config for a hotkey" : $"Hotkey: {SlowMoKey.Value}", solo);
            Menu.SliderRow(SlowMoScale, "Game speed", 0.05f, 1f, "0.00", "x", solo);
            Menu.Chips(4,
                ("0.1x", () => SlowMoScale.Value = 0.1f, solo),
                ("0.25x", () => SlowMoScale.Value = 0.25f, solo),
                ("0.5x", () => SlowMoScale.Value = 0.5f, solo),
                ("0.75x", () => SlowMoScale.Value = 0.75f, solo));
            Menu.KeyValue("Applied", SlowMotion.Status);
        });

        Menu.Card("Fun overlay and reset", null, () =>
        {
            Menu.Switch(ShowOverlay, "Show active modes", "Small line at the top of the screen", true);
            Menu.Chips(1, ("Turn every fun mode off", () => { AllOff(); Menu.SetStatus("All fun modes off."); }, true));
        });
    }

    private static void SetGravity(float g)
    {
        GravityScale.Value = g;
        Moon.Value = true;
        Menu.SetStatus($"Gravity set to {g:0.00} g for your car.");
    }

    // ---- overlay ----

    private static GUIStyle _overlayStyle;

    private static void DrawOverlay()
    {
        if (!ShowOverlay.Value) return;
        bool slow = SlowMotion.Applied;
        if (!slow && !AnyCarModeOn) return;
        if (GameState.LocalCar == null) return;

        var sb = new System.Text.StringBuilder();
        bool phys = GameState.PhysicsAllowed;
        if (phys)
        {
            if (Moon.Value) sb.Append($"Gravity {GravityScale.Value:0.00}g   ");
            if (WallClimb.Value) sb.Append(FunPhysicsStep.Stuck ? "Sticky (stuck)   " : "Sticky   ");
            if (Drift.Value) sb.Append(DriftKeyHeld ? "Drift   " : "Drift (hold key)   ");
            if (Bouncy.Value) sb.Append("Bouncy   ");
            if (Unflippable.Value) sb.Append("Unflippable   ");
        }
        else if (AnyCarModeOn) sb.Append("Car fun modes paused (guest)   ");
        if (slow) sb.Append($"Slow-mo {SlowMoScale.Value:0.00}x");
        string text = sb.ToString().TrimEnd();
        if (text.Length == 0) return;

        Theme.Ensure();
        if (_overlayStyle == null)
        {
            _overlayStyle = new GUIStyle(Theme.Small);
            _overlayStyle.alignment = TextAnchor.MiddleCenter;
            _overlayStyle.normal.textColor = Theme.Text;
        }
        float scale = Mathf.Max(1f, Screen.height / 1080f);
        var prevMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        float w = Mathf.Min(_overlayStyle.CalcSize(new GUIContent("FUN  " + text)).x + 28f, Screen.width / scale - 32f);
        var r = new Rect((Screen.width / scale - w) / 2f, 8f, w, 24f);
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.78f), 12);
        GUI.Label(r, $"<b><color=#F2B33D>FUN</color></b>  {text}", _overlayStyle);
        GUI.matrix = prevMatrix;
    }
}
