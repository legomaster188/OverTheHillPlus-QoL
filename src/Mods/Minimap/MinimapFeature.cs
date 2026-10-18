using BepInEx.Configuration;
using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public;
using UnityEngine.InputSystem;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.Minimap;

/// <summary>The minimap's advanced card: its keys, and what it found of the game's map.</summary>
internal sealed class MinimapFeature : IFeature
{
    public void Register() => Registry.Section(Registry.PlusMode, "Minimap", DrawAdvanced);

    private static void DrawAdvanced()
    {
        Card("Keys", null, () =>
        {
            KeyRow("Show / hide", MinimapConfig.ToggleKey);
            KeyRow("Zoom in", MinimapConfig.ZoomInKey);
            KeyRow("Zoom out", MinimapConfig.ZoomOutKey);
            Chips(2, ("Zoom in", () => MinimapFrame.Zoom(0.8f), true), ("Zoom out", () => MinimapFrame.Zoom(1.25f), true));
        });
        Card("Map", null, () =>
        {
            KeyValue("Map picture", MinimapSource.HasMap ? $"{MinimapSource.MapWidth:0} m across" : "not loaded");
            KeyValue("Fog of war", !MinimapSource.HasFog ? "not loaded (no map picture without it)"
                : MinimapSource.FogCoversMap ? "found" : "does not match the map (no map picture)");
            KeyValue("Other players", MinimapSource.Others.Count.ToString());
            KeyValue("Pins", MinimapSource.Pins.Count.ToString());
        });
    }

    private static void KeyRow(string label, ConfigEntry<Key> entry)
    {
        var key = entry.Value;
        KeyValue(label, key == Key.None ? "none" : KeybindEditor.KeyName(key));
        Chips(1, ($"Change \"{label}\"…", () => KeybindEditor.StartCapture(entry), true));
    }
}
