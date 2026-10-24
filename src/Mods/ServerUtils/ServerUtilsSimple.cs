using OverTheHillPlus.Features.Public.Autosave;
using OverTheHillPlus.Features.Public.JoinSmoothing;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.ServerUtils;

/// <summary>Host+'s everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class ServerUtilsSimple
{
    private static readonly float[] AutosaveSteps = { 0f, 5f, 10f, 15f, 30f };

    internal static void Draw()
    {
        SimpleMenu.Heading("Smooth hosting");
        if (CarPreloader.PreloadEnabled != null)
            SimpleMenu.Toggle(CarPreloader.PreloadEnabled, "Smooth joins", "Preloads every car so players joining don't freeze your game.");
        if (AutosaveConfig.Enabled != null && AutosaveConfig.IntervalMinutes != null)
        {
            SimpleMenu.FloatChoice("Autosave", "Saves the game every few minutes while you play solo or host.",
                AutosaveConfig.Enabled.Value ? AutosaveConfig.IntervalMinutes.Value : 0f, AutosaveSteps,
                v => v <= 0f ? "Off" : v.ToString("0") + " min",
                v =>
                {
                    AutosaveConfig.Enabled.Value = v > 0f;
                    if (v > 0f) AutosaveConfig.IntervalMinutes.Value = v;
                });
        }
    }
}
