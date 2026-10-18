using System;
using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public;
using OverTheHillPlus.Shared;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Waypoints;

/// <summary>Waypoints+ wiring: the mod network channel, per-frame work, the hotkey, the toast and the Advanced card.</summary>
internal sealed class WaypointsFeature : IFeature
{
    private static string _toast = "";
    private static float _toastAt = -100f;
    private static GUIStyle _toastStyle;
    private static bool _loggedError;

    public void Register()
    {
        SessionPins.RegisterChannel();
        SessionPins.Report += Toast;
        SessionPins.Pushed += OnPushed;
        Registry.OnTick(Tick);
        Registry.OnGui(DrawGui);
        Registry.Section(Registry.PlusMode, "Waypoints+", DrawAdvanced, 10);
    }

    private static void OnPushed(BoardPin p)
    {
        if (p == null) return;
        if (WaypointsConfig.FollowHostPush.Value) WaypointsRoute.FollowShared(p);
        if (!Net.IsHost) Toast($"The host marked \"{p.Name}\"" + (WaypointsConfig.FollowHostPush.Value ? ": follow the route" : ""));
    }

    internal static void Toast(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _toast = text;
        _toastAt = Time.unscaledTime;
        try { Menu.SetStatus(text); } catch (Exception ex) { DebugLog.Error("waypoints", ex, "WaypointsFeature.Toast"); }
    }

    private static void Tick()
    {
        try
        {
            SessionPins.Tick();
            WaypointsRoute.Tick();
            HotKey();
        }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            WaypointsPlugin.Logger.LogError("[Waypoints] tick failed (further errors suppressed): " + e);
        }
    }

    private static void HotKey()
    {
        var key = WaypointsConfig.SaveKey.Value;
        if (key == Key.None) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        // Not while a key is being rebound or text is being typed.
        if (MenuUx.HotkeysCaptured || KeybindEditor.Capturing || MenuUx.Typing || GUIUtility.keyboardControl != 0) return;
        bool down;
        try { down = kb[key].wasPressedThisFrame; } catch (Exception ex) { DebugLog.Error("waypoints", ex, "WaypointsFeature.HotKey"); return; }
        if (down) Toast(SaveHere(null));
    }

    /// <summary>Saves your map pin, or your car's spot when there is no map pin.</summary>
    internal static string SaveHere(string name)
    {
        string area = GameMap.AreaKey;
        if (GameMap.TryGetGamePin(out var pin)) return PinStore.Add(area, name, pin, WaypointsConfig.NewPinColor.Value);
        if (GameMap.TryCarPosition(out var car)) return PinStore.Add(area, name, car, WaypointsConfig.NewPinColor.Value);
        return "Failed: no map pin and no car (load into a world first).";
    }

    private static void DrawGui()
    {
        try
        {
            WaypointsRoute.DrawWorld();
            DrawToast();
        }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            WaypointsPlugin.Logger.LogError("[Waypoints] drawing failed (further errors suppressed): " + e);
        }
    }

    private static void DrawToast()
    {
        float age = Time.unscaledTime - _toastAt;
        if (_toast.Length == 0 || age > 3f || age < 0f) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        Theme.Ensure();
        if (_toastStyle == null)
        {
            _toastStyle = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleCenter, richText = false };
            _toastStyle.normal.textColor = Theme.Text;
        }
        var c = _toast.StartsWith("Failed", StringComparison.Ordinal) ? Theme.Bad
            : _toast.StartsWith("Blocked", StringComparison.Ordinal) ? Theme.Warn
            : _toast.StartsWith("Asked", StringComparison.Ordinal) ? Theme.Info : Theme.Good;
        var prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(3f - age));
        var size = _toastStyle.CalcSize(new GUIContent(_toast));
        var r = new Rect((Screen.width - size.x) / 2f - 14f, 84f, size.x + 28f, 34f);
        Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.88f), 8);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), c, 2);
        GUI.Label(r, _toast, _toastStyle);
        GUI.color = prev;
    }

    private static void DrawAdvanced()
    {
        Menu.Card("Route and markers", "The dotted line runs straight toward the pin, laid on the ground: it shows the way, not the roads.", () =>
        {
            Menu.Switch(WaypointsConfig.ShowRoute, "Dotted route on the ground", null, true);
            Menu.SliderRow(WaypointsConfig.RouteLength, "Route length", 50f, 1000f, "0", " m", WaypointsConfig.ShowRoute.Value);
            Menu.Switch(WaypointsConfig.ShowMarkers, "Pins in the world", "Saved and shared pins nearby, with their distance", true);
            Menu.SliderRow(WaypointsConfig.MarkerRange, "Pin marker range", 100f, 5000f, "0", " m", WaypointsConfig.ShowMarkers.Value);
            Menu.Switch(WaypointsConfig.FollowGamePin, "Route to my map pin", "When you follow no pin of your own", true);
            Menu.SliderRow(WaypointsConfig.ArriveDistance, "Arrived within", 5f, 100f, "0", " m", true);
            Menu.Switch(WaypointsConfig.ClearOnArrival, "Stop following on arrival", null, true);
        });
        Menu.Card("Map screen", null, () =>
        {
            Menu.Switch(WaypointsConfig.ShowSavedOnMap, "My saved pins", null, true);
            Menu.Switch(WaypointsConfig.ShowSharedOnMap, "Shared pins", null, true);
            Menu.Switch(WaypointsConfig.ShowRouteOnMap, "Route line", null, true);
        });
        Menu.Card("Session (host)", "Guests need Waypoints+ too. Pins of a guest who leaves are removed.", () =>
        {
            Menu.Switch(WaypointsConfig.AcceptGuestPins, "Guests may share pins", null, true);
            float max = Menu.RawSlider("Shared pins per guest", WaypointsConfig.MaxPinsPerGuest.Value, 1f, 12f,
                WaypointsConfig.MaxPinsPerGuest.Value.ToString(), WaypointsConfig.AcceptGuestPins.Value);
            int maxInt = Mathf.Clamp(Mathf.RoundToInt(max), 1, 12);
            if (maxInt != WaypointsConfig.MaxPinsPerGuest.Value) WaypointsConfig.MaxPinsPerGuest.Value = maxInt;
            Menu.Switch(WaypointsConfig.AnnouncePush, "Notice for everyone on push", "Players without the mod see \"Meet at ...\"", true);
            Menu.Switch(WaypointsConfig.FollowHostPush, "Follow the host's pushed pin (as guest)", null, true);
            Menu.KeyValue("Now", SessionPins.Status);
        });
        Menu.Card("Hotkey", null, () =>
        {
            Menu.KeyValue("Save pin", WaypointsConfig.SaveKey.Value == Key.None ? "none (set one under Keybinds)" : WaypointsConfig.SaveKey.Value.ToString());
            Menu.Small("Saves your map pin as a named pin; without a map pin, your car's spot.");
        });
    }
}
