using System;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.WorldTools;

/// <summary>World Tools+'s MODS page: gravity, game speed, day length and the weather lock.</summary>
internal static class WorldToolsSimple
{
    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (WorldToolsConfig.Gravity == null) return;
        string blocked = WorldControl.BlockReason;
        if (blocked != null) SimpleMenu.Note(blocked + " The settings below are kept and apply when you host.");

        SimpleMenu.Heading("Gravity");
        float g = WorldToolsConfig.Gravity.Value;
        SimpleMenu.FloatChoice("Gravity", "For the whole world: every car and loose object the host simulates.",
            g, WorldMath.GravitySteps, WorldMath.GravityLabel, v => WorldToolsConfig.Gravity.Value = v);
        bool moon = WorldMath.Near(WorldMath.ClampGravity(g), WorldMath.Moon);
        SimpleMenu.Button("Moon mode", moon ? "On" : "Off", "Moon gravity (0.17 g): long, floaty jumps for everyone.",
            () => WorldToolsConfig.Gravity.Value = moon ? WorldMath.Earth : WorldMath.Moon);

        SimpleMenu.Heading("Time");
        SimpleMenu.FloatChoice("Game speed", "Slow motion or faster play for everyone, through the game's own networked time scale.",
            WorldToolsConfig.TimeScale.Value, WorldMath.TimeScaleSteps, WorldMath.TimeScaleLabel, v => WorldToolsConfig.TimeScale.Value = v);
        if (!WorldMath.Near(WorldMath.ClampTimeScale(WorldToolsConfig.TimeScale.Value), 1f))
            SimpleMenu.Note("A guest without World Tools+ who leaves while the game speed is changed keeps that speed in their " +
                            "menus (the game never resets it) until they restart the game. Set Normal before they leave if you can.");
        SimpleMenu.FloatChoice("Day length", "How fast the clock runs. Every player's clock follows the host's.",
            WorldToolsConfig.DaySpeed.Value, WorldMath.DaySpeedSteps, WorldMath.DaySpeedLabel, v => WorldToolsConfig.DaySpeed.Value = v);

        SimpleMenu.Heading("Weather");
        var weathers = WorldControl.Weathers;
        if (weathers.Count == 0)
        {
            SimpleMenu.Note("The weather list shows once a world is loaded.");
            SimpleMenu.Toggle(WorldToolsConfig.LockWeather, "Lock the weather", "Keeps the chosen weather.");
            return;
        }
        var labels = new string[weathers.Count];
        int index = -1;
        for (int i = 0; i < weathers.Count; i++)
        {
            labels[i] = weathers[i].name;
            if (weathers[i].id == WorldToolsConfig.LockedWeatherId.Value) index = i;
        }
        SimpleMenu.Choice("Weather", "The weather to set and keep.", index, labels, i =>
        {
            WorldToolsConfig.LockedWeatherId.Value = weathers[i].id;
            if (blocked == null) Menu.SetStatus(WorldControl.SetWeather(weathers[i].id));
        }, shown: index < 0 ? "Choose" : null);
        SimpleMenu.Toggle(WorldToolsConfig.LockWeather, "Lock the weather", "Puts the chosen weather back whenever the game changes it.");
        SimpleMenu.Note($"Now: {WorldControl.WeatherName(WorldControl.CurrentWeatherId)}. Game speed now: {WorldControl.TimeScaleNow:0.##}×.");
        SimpleMenu.Note("Achievements stay locked for the session once any of these is changed from normal.");
    }

    /// <summary>The Advanced fold: back to normal in one press.</summary>
    internal static void DrawAdvanced()
    {
        if (WorldToolsConfig.Gravity == null) return;
        Menu.Card("Reset", "Everything back to the game's own values", () =>
        {
            Menu.Chips(1, ("Back to normal", (Action)ResetAll, true));
            Menu.Small("Gravity, game speed and day length are put back to normal and the weather lock is switched off.");
        });
    }

    private static void ResetAll()
    {
        WorldToolsConfig.Gravity.Value = 1f;
        WorldToolsConfig.TimeScale.Value = 1f;
        WorldToolsConfig.DaySpeed.Value = 1f;
        WorldToolsConfig.LockWeather.Value = false;
        Menu.SetStatus("World Tools+: everything back to normal.");
    }
}
