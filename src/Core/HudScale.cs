using System;

namespace OverTheHillPlus;

/// <summary>How the mods' HUD panels scale (pure, unit tested): by the screen's width against the 1920 the game's own HUD
/// canvas is made for, so a panel keeps its size next to the game's HUD on any shape of screen.</summary>
internal static class HudScale
{
    /// <summary>The width the HUD units are for.</summary>
    public const float ReferenceWidth = 1920f;

    /// <summary>The GUI scale for a screen <paramref name="width"/> pixels wide (1920 is 1 : 1; never below half).</summary>
    public static float For(int width) => Math.Max(0.5f, width / ReferenceWidth);
}
