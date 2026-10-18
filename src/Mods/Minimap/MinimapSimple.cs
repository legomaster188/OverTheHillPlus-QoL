using OverTheHillPlus.UI;

namespace OverTheHillPlus.Minimap;

/// <summary>The minimap's everyday settings, first on its MODS page (the keys are in its Advanced fold).</summary>
internal static class MinimapSimple
{
    private static readonly float[] Zooms = { 150f, 250f, 400f, 600f, 800f, 1200f, 1600f, 2400f, 3200f, 4000f };
    private static readonly float[] Sizes = { 160f, 200f, 240f, 280f, 320f, 380f, 440f };
    private static readonly float[] Opacities = { 0.4f, 0.55f, 0.7f, 0.8f, 0.9f, 1f };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (MinimapConfig.Show == null) return;
        SimpleMenu.Heading("Minimap");
        SimpleMenu.Toggle(MinimapConfig.Show, "Show the minimap", "North up, centred on your car. Undiscovered areas stay dark, as on the game's map.");
        bool on = MinimapConfig.Show.Value;
        SimpleMenu.Choice("Corner", null, (int)MinimapConfig.Corner.Value, HudLayout.CornerNames, i => MinimapConfig.Corner.Value = HudLayout.Corner(i), on);
        SimpleMenu.FloatChoice("Zoom", "How much of the map it shows across.", MinimapConfig.Meters.Value, Zooms,
            v => v >= 1000f ? (v / 1000f).ToString("0.#") + " km" : v.ToString("0") + " m", v => MinimapConfig.Meters.Value = v, on);
        SimpleMenu.FloatChoice("Size", null, MinimapConfig.Size.Value, Sizes, v => v.ToString("0") + " px", v => MinimapConfig.Size.Value = v, on);
        SimpleMenu.FloatChoice("Opacity", null, MinimapConfig.Opacity.Value, Opacities, v => (v * 100f).ToString("0") + "%",
            v => MinimapConfig.Opacity.Value = v, on);
        SimpleMenu.Toggle(MinimapConfig.ShowNames, "Player names", "Names next to the other players' markers.", on);
        SimpleMenu.Toggle(MinimapConfig.ShowPins, "Map pins", "Everyone's map pins, in their colour.", on);
        SimpleMenu.Toggle(MinimapConfig.ShowRoute, "Route to my pin", "A dotted line from your car to your own map pin.", on);
    }
}
