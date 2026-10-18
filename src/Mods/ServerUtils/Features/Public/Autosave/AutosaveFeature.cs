using System;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using OverTheHillPlus.ServerUtils;

namespace OverTheHillPlus.Features.Public.Autosave;

/// <summary>
/// QoL tab "Autosave": asks the game to save every few minutes while you play solo or host, makes sure a save happens
/// when you quit, a "Save now" button and hotkey, and a small notice when it saved. Only the game's own save code
/// runs (see <see cref="AutosaveRuntime"/>). Guests are off unless they opt in: the host's game saves the world.
/// </summary>
internal sealed class AutosaveFeature : IFeature
{
    private static bool _keyError;

    public void Register()
    {
        AutosaveConfig.Bind(ServerUtilsConfig.File);
        ServerSettings.AddRow(AutosaveServerRow.Create(90));
        Registry.OnTick(Tick);
        Registry.OnGui(AutosaveRuntime.DrawToast);
        Registry.Section(Registry.PlusMode, "QoL", Draw, 20);
    }

    private static void Tick()
    {
        AutosaveRuntime.Tick();
        HandleKey();
    }

    private static void HandleKey()
    {
        var key = AutosaveConfig.SaveKey.Value;
        if (key == Key.None) return;
        try
        {
            var kb = Keyboard.current;
            if (kb == null || MenuUx.Typing || MenuUx.HotkeysCaptured) return;
            if (!kb[key].wasPressedThisFrame) return;
            Menu.SetStatus(AutosaveRuntime.Request("Saved", true));
        }
        catch (Exception e)
        {
            DebugLog.Error("serverutils", e, "AutosaveFeature.HandleKey");
            if (!_keyError) { _keyError = true; ServerUtilsPlugin.Logger.LogWarning("[Autosave] hotkey: " + e.Message); }
        }
    }

    // ---- menu ----

    private static void Draw()
    {
        Menu.Card("Autosave", "Uses the game's own save, the same one it makes at points of interest.", () =>
        {
            bool guest = AutosaveRuntime.IsGuest;
            if (guest && !AutosaveConfig.GuestSaves.Value)
                Menu.Banner("You're a guest: the host's game saves the world, and your game saves your profile when you leave. Turn on guest saves below to also save while you play here.", Theme.Info);

            Menu.Switch(AutosaveConfig.Enabled, "Autosave every few minutes", guest ? "Solo and hosting; as a guest only with guest saves on" : "Solo and hosting", true);
            Menu.SliderRow(AutosaveConfig.IntervalMinutes, "Every", 2f, 60f, "0", " min", AutosaveConfig.Enabled.Value);
            Menu.Switch(AutosaveConfig.SaveBeforeQuit, "Save when quitting", "Quit to menu or desktop; only if the game would skip its own exit save", true);
            Menu.Switch(AutosaveConfig.ShowToast, "Show a notice when saved", null, true);
            Menu.Switch(AutosaveConfig.ToastGameSaves, "Also for the game's own saves", null, AutosaveConfig.ShowToast.Value);
            Menu.Switch(AutosaveConfig.GuestSaves, "Guest saves", "Also save your own profile while in someone else's session", true);

            Menu.KeyValue("Last save", LastSaveText());
            Menu.KeyValue("Next autosave", NextText());

            string blocked = AutosaveRuntime.BlockReason();
            string label = AutosaveRuntime.Busy ? "Saving…"
                : AutosaveConfig.SaveKey.Value == Key.None ? "Save now" : $"Save now ({AutosaveConfig.SaveKey.Value})";
            if (Menu.ChipButton(Menu.NextRow(32f), label, blocked == null && !AutosaveRuntime.Busy))
                Menu.SetStatus(AutosaveRuntime.Request("Saved", true));
            if (blocked != null) Menu.Small("Save now: " + blocked.ToLowerInvariant() + ".");
        });
    }

    private static string LastSaveText()
    {
        float at = AutosaveRuntime.LastSaveAt;
        if (at < 0f) return "none since the game started";
        return $"{Ago(Time.unscaledTime - at)} ({AutosaveRuntime.LastSaveWhat.ToLowerInvariant()})";
    }

    private static string NextText()
    {
        if (!AutosaveConfig.Enabled.Value) return "off";
        if (!AutosaveRuntime.InSession) return "not in a session";
        if (!AutosaveRuntime.PolicyAllows) return "off as a guest";
        float s = AutosaveRuntime.SecondsUntilNext();
        if (s < 0f) return "after the world loads";
        if (s <= 0f)
        {
            string why = AutosaveRuntime.BlockReason();
            return why == null ? "now" : "waiting: " + why.ToLowerInvariant();
        }
        return "in " + Duration(s);
    }

    private static string Ago(float seconds) => seconds < 10f ? "just now" : Duration(seconds) + " ago";

    private static string Duration(float seconds)
    {
        int s = Mathf.Max(0, Mathf.RoundToInt(seconds));
        if (s < 60) return s + " s";
        int m = s / 60;
        if (m < 60) return m + " min";
        return $"{m / 60} h {m % 60} min";
    }
}
