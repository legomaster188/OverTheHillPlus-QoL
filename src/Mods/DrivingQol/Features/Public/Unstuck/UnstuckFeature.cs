using System;
using BepInEx.Configuration;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;
using OverTheHillPlus.DrivingQol;

namespace OverTheHillPlus.Features.Public.Unstuck;

/// <summary>PLUS > QoL "Unstuck" card, its hotkey and a small on-screen notice (see <see cref="Unstuck"/>).</summary>
internal sealed class UnstuckFeature : IFeature
{
    internal const string Section = "Unstuck";

    internal static ConfigEntry<Key> UnstuckKey;
    internal static ConfigEntry<bool> AcceptRequests, ShowNotice;

    private static GUIStyle _toastStyle;

    public void Register()
    {
        var cfg = DrivingQolConfig.File;
        UnstuckKey = cfg.Bind(Section, "UnstuckKey", Key.None,
            "Hotkey: put your car back on its wheels (solo/host), or ask a modded host to (guest). 10 s cooldown. None = no hotkey.");
        AcceptRequests = cfg.Bind(Section, "AcceptRequests", true,
            "When hosting: unstuck guests' cars when they ask (guests running this mod with the mod network on). 10 s cooldown per guest.");
        ShowNotice = cfg.Bind(Section, "ShowNotice", true, "Show a short notice at the top of the screen after using unstuck.");

        Unstuck.RegisterChannel();
        Registry.OnTick(Tick);
        Registry.OnGui(DrawNotice);
        Registry.Section(Registry.PlusMode, "QoL", DrawCard, 5);
    }

    private static void Tick()
    {
        Unstuck.Tick();
        var key = UnstuckKey.Value;
        if (key == Key.None) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        // Not while a key is being rebound or text is being typed in the menu.
        if (MenuUx.HotkeysCaptured || KeybindEditor.Capturing || MenuUx.Typing) return;
        if (GUIUtility.keyboardControl != 0) return;
        bool down;
        try { down = kb[key].wasPressedThisFrame; } catch (Exception ex) { DebugLog.Error("drivingqol", ex, "UnstuckFeature.Tick"); return; }
        if (down) Unstuck.Use();
    }

    private static void DrawCard()
    {
        Menu.Card("Unstuck", "Puts your car back on its wheels, a little above the ground under it. Solo or hosting it's instant; as a guest the host's game does it for you if the host runs this mod. 10 s cooldown.", () =>
        {
            float left = Unstuck.CooldownLeft;
            string label = Unstuck.Waiting ? "Waiting for the host..." : left > 0f ? $"Unstuck ({Mathf.CeilToInt(left)} s)" : "Unstuck my car";
            Menu.Chips(1, (label, () => Unstuck.Use(), left <= 0f && !Unstuck.Waiting));
            Menu.KeyValue("Hotkey", UnstuckKey.Value == Key.None ? "none (set one under Keybinds)" : UnstuckKey.Value.ToString());
            Menu.KeyValue("Here", Where());

            Menu.Switch(AcceptRequests, "Help guests who ask (host)", "Guests with this mod can ask your game to unstuck their car, once per 10 s each", true);
            Menu.Switch(Net.Enabled, "Mod network", "Needed to ask a host, or to hear guests ask (Network tab)", true);
            Menu.Switch(ShowNotice, "On-screen notice", null, true);
            if (Unstuck.ServedForGuests > 0) Menu.KeyValue("Helped guests", Unstuck.ServedForGuests.ToString());
        });
    }

    /// <summary>What the button will do in the current session.</summary>
    private static string Where()
    {
        try
        {
            var r = GameState.Runner;
            if (r == null || !r.IsRunning) return GameState.LocalCar != null ? "unstucks your car" : "load into a world first";
            if (r.IsSinglePlayer) return "solo: unstucks your car";
            if (GameState.IsAuthority)
                return "hosting: unstucks your car" + (AcceptRequests.Value && Net.Enabled.Value ? "; guests with the mod can ask" : "");
            if (!Net.Enabled.Value) return "guest: turn on the mod network to ask the host";
            if (Net.HostStatus != Net.HostLink.Ready) return "guest: " + Net.Describe();
            return Net.HostHas(Net.Channels.Unstuck) ? "guest: asks the host (it accepts requests)" : "guest: the host doesn't accept requests";
        }
        catch (Exception ex) { DebugLog.Error("drivingqol", ex, "UnstuckFeature.Where"); return "?"; }
    }

    private static void DrawNotice()
    {
        if (!ShowNotice.Value) return;
        string msg = Unstuck.LastMessage;
        float age = Time.unscaledTime - Unstuck.LastMessageAt;
        if (string.IsNullOrEmpty(msg) || age > 3f || age < 0f) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        Theme.Ensure();
        if (_toastStyle == null)
        {
            _toastStyle = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleCenter, richText = false };
            _toastStyle.normal.textColor = Theme.Text;
        }
        var c = msg.StartsWith("Failed", StringComparison.Ordinal) ? Theme.Bad
            : msg.StartsWith("Blocked", StringComparison.Ordinal) ? Theme.Warn
            : msg.StartsWith("Asked", StringComparison.Ordinal) ? Theme.Info
            : Theme.Good;
        float alpha = Mathf.Clamp01(3f - age); // fades over the last second
        var prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        var size = _toastStyle.CalcSize(new GUIContent(msg));
        var r = new Rect((Screen.width - size.x) / 2f - 14f, 40f, size.x + 28f, 34f);
        Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.88f), 8);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), c, 2);
        GUI.Label(r, msg, _toastStyle);
        GUI.color = prev;
    }
}
