using System;
using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Race;

/// <summary>Race Mode wiring: the mod network channel, per-frame work, the checkpoint hotkey and the Advanced card.</summary>
internal sealed class RaceFeature : IFeature
{
    private static bool _loggedTick, _loggedGui;
    private static bool _joinWas;

    public void Register()
    {
        RaceNet.RegisterChannel();
        _joinWas = RaceConfig.JoinRaces.Value;
        Registry.OnTick(Tick);
        Registry.OnGui(Gui);
        Registry.Section(Registry.PlusMode, "Race Mode", DrawAdvanced, 10);
    }

    private static void Tick()
    {
        try
        {
            RaceHost.Tick();
            RaceView.Tick();
            RaceHud.Tick();
            if (_joinWas != RaceConfig.JoinRaces.Value)
            {
                _joinWas = RaceConfig.JoinRaces.Value;
                RaceNet.SendJoin();
            }
            HotKey();
        }
        catch (Exception e)
        {
            if (_loggedTick) return;
            _loggedTick = true;
            RacePlugin.Logger.LogError("[Race] tick failed (further errors suppressed): " + e);
        }
    }

    private static void HotKey()
    {
        var key = RaceConfig.DropKey.Value;
        if (key == Key.None || !RaceNet.IsHostSide) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        if (MenuUx.HotkeysCaptured || KeybindEditor.Capturing || MenuUx.Typing || GUIUtility.keyboardControl != 0) return;
        bool down;
        try { down = kb[key].wasPressedThisFrame; } catch (Exception ex) { DebugLog.Error("race", ex, "RaceFeature.HotKey"); return; }
        if (down) RaceView.Tell(RaceHost.AddGateAtCar());
    }

    private static void Gui()
    {
        try { RaceHud.Draw(); }
        catch (Exception e)
        {
            if (_loggedGui) return;
            _loggedGui = true;
            RacePlugin.Logger.LogError("[Race] drawing failed (further errors suppressed): " + e);
        }
    }

    private static void DrawAdvanced()
    {
        Menu.Card("Rules (host)", "Be at the start at GO. Resets and the unstuck button are fine; a car that moves too far at once (more than 40 m at over 540 km/h) is out.", () =>
        {
            Menu.SliderRow(RaceConfig.TimeLimitMinutes, "Time limit", 1f, 60f, "0", " min", true);
            Menu.SliderRow(RaceConfig.FinishWindowSeconds, "After the first finisher", 10f, 300f, "0", " s", true);
            Menu.SliderRow(RaceConfig.StartZone, "Start zone at GO (0 = off)", 0f, 200f, "0", " m", true);
            Menu.Switch(RaceConfig.HostRaces, "Race myself as host", "Off: you only run the race for your guests", true);
            Menu.Switch(RaceConfig.AnnounceResults, "Notice for everyone with the winner", "Players without the mod see it too", true);
        });
        Menu.Card("On screen", null, () =>
        {
            Menu.Switch(RaceConfig.ShowHud, "Race HUD", "Countdown, timer, checkpoints, splits, results", true);
            Menu.Switch(RaceConfig.ShowTrackIdle, "Show the track when no race is on", null, RaceConfig.ShowHud.Value);
            Menu.Switch(RaceConfig.ShowRoute, "Dotted way to the next checkpoint", null, RaceConfig.ShowHud.Value);
            Menu.Switch(RaceConfig.ShowOnMap, "Checkpoints on the map screen", null, true);
        });
        Menu.Card("Hotkey", null, () =>
        {
            Menu.KeyValue("Add checkpoint", RaceConfig.DropKey.Value == Key.None ? "none (set one under Keybinds)" : RaceConfig.DropKey.Value.ToString());
            Menu.Small("Host or solo: adds a checkpoint where your car is (the first one is the start).");
        });
    }
}
