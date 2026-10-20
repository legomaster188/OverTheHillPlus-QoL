using OverTheHillPlus.Features.Public;
using OverTheHillPlus.UI;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Flares;

/// <summary>Player Flares' MODS page: the flare, the auto flare and the far name tags.</summary>
internal static class FlaresSimple
{
    private static readonly float[] Distances = { 150f, 300f, 600f, 1000f, 1500f, 2000f, 3000f };
    private static readonly float[] Sizes = { 0.3f, 0.45f, 0.55f, 0.7f, 0.85f, 1f };
    private static readonly float[] Cooldowns = { 5f, 10f, 15f, 20f, 30f, 60f };
    private static readonly float[] StuckAfter = { 5f, 10f, 15f, 30f, 60f, 120f };

    private static string Seconds(float v) => v.ToString("0") + " s";

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (FlaresConfig.AutoFlare == null) return;

        SimpleMenu.Heading("Flare");
        SimpleMenu.Actions("Fire a flare",
            ("Fire now", () => FlaresCore.Fire(false), FlaresCore.Wait <= 0f));
        SimpleMenu.FloatChoice("Cooldown", "Seconds between your flares.", FlaresConfig.Cooldown.Value, Cooldowns, Seconds, v => FlaresConfig.Cooldown.Value = v);
        SimpleMenu.Toggle(FlaresConfig.ShowFlares, "See other players' flares", "Flares fired by players with this mod, from far away.");
        SimpleMenu.Toggle(FlaresConfig.OnMap, "Ping on the maps", "A flare also shows on the map, the compass and the minimap for a while.");

        SimpleMenu.Heading("Auto flare");
        SimpleMenu.Toggle(FlaresConfig.AutoFlare, "Fire when stuck", "A flare goes up by itself when your car lies on its side or roof and does not move.");
        SimpleMenu.FloatChoice("Stuck for", "Seconds before it goes up.", FlaresConfig.AutoAfter.Value, StuckAfter, Seconds,
            v => FlaresConfig.AutoAfter.Value = v, FlaresConfig.AutoFlare.Value);

        SimpleMenu.Heading("Name tags");
        SimpleMenu.Toggle(FlaresConfig.TagsFar, "See name tags from far away", "The tags above other players' cars stay up much farther away.");
        bool on = FlaresConfig.TagsFar.Value;
        SimpleMenu.FloatChoice("Distance", "Where a tag is gone.", FlaresConfig.TagDistance.Value, Distances, v => v.ToString("0") + " m",
            v => FlaresConfig.TagDistance.Value = v, on);
        SimpleMenu.FloatChoice("Smallest size", "Far tags shrink to this size (100% = same size at any distance).", FlaresConfig.TagMinSize.Value, Sizes,
            v => (v * 100f).ToString("0") + "%", v => FlaresConfig.TagMinSize.Value = v, on);
        SimpleMenu.Toggle(FlaresConfig.TagMetres, "Show the distance", "Metres to the car, under the name.", on);
        SimpleMenu.Toggle(FlaresConfig.TagOcclusion, "Hide behind obstacles", "A tag fades out while terrain or buildings are in the way.", on);

        if (GameState.IsAuthority && GameState.Runner != null)
            SimpleMenu.Toggle(FlaresConfig.HostRelay, "Pass on players' flares", "Players with this mod send their flares through you; you check each one and show it to everyone.");
        SimpleMenu.Note($"Fire a flare with {KeyName(FlaresConfig.FlareKey.Value)}. Only players with Player Flares see your flare; it carries your position and colour, nothing else.");
    }

    private static string KeyName(Key key) => key == Key.None ? "no key set" : KeybindEditor.KeyName(key);
}
