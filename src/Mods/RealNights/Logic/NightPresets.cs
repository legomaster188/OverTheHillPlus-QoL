using System;

namespace OverTheHillPlus.RealNights;

/// <summary>The look presets on the MODS page: each is a set of the sliders, so a changed slider shows as "Custom".</summary>
internal static class NightPresets
{
    internal sealed class Preset
    {
        public string Name;
        public bool DarkNights;
        public float Darkness, StormDarkness, TwilightKeep, StarBrightness, MilkyWay, HeadlightBoost;
    }

    public static readonly Preset[] All =
    {
        // The game's own night; the stars and lamps still work.
        new Preset { Name = "Game", DarkNights = false, Darkness = 0f, StormDarkness = 0f, TwilightKeep = 0.5f, StarBrightness = 1.5f, MilkyWay = 1.2f, HeadlightBoost = 1f },
        // Dark enough to need your lights, light enough to see the road shape and the horizon.
        new Preset { Name = "Playable", DarkNights = true, Darkness = 0.4f, StormDarkness = 0.5f, TwilightKeep = 0.5f, StarBrightness = 1.5f, MilkyWay = 1.2f, HeadlightBoost = 1.25f },
        new Preset { Name = "Real", DarkNights = true, Darkness = 0.75f, StormDarkness = 0.85f, TwilightKeep = 0.5f, StarBrightness = 1.5f, MilkyWay = 1.2f, HeadlightBoost = 1.25f },
        new Preset { Name = "Pitch black", DarkNights = true, Darkness = 1f, StormDarkness = 1f, TwilightKeep = 0.5f, StarBrightness = 1.5f, MilkyWay = 1.2f, HeadlightBoost = 1.5f },
    };

    public static string[] Names
    {
        get
        {
            var names = new string[All.Length];
            for (int i = 0; i < All.Length; i++) names[i] = All[i].Name;
            return names;
        }
    }

    /// <summary>Index of the preset whose values these are, or -1 (custom).</summary>
    public static int Find(bool darkNights, float darkness, float storm, float twilight, float stars, float milky, float headlights)
    {
        for (int i = 0; i < All.Length; i++)
        {
            var p = All[i];
            if (p.DarkNights == darkNights && Same(p.Darkness, darkness) && Same(p.StormDarkness, storm) && Same(p.TwilightKeep, twilight)
                && Same(p.StarBrightness, stars) && Same(p.MilkyWay, milky) && Same(p.HeadlightBoost, headlights))
                return i;
        }
        return -1;
    }

    private static bool Same(float a, float b) => Math.Abs(a - b) < 0.005f;
}
