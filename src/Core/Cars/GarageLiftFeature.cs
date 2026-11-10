using OverTheHillPlus.Config;
using OverTheHillPlus.Features;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Garage lift preview: its settings (core's file, [GarageLift]) and per-frame work (see <see cref="GarageLift"/>).
/// Part of core so it runs for every stock and modded car wherever core does (the MelonLoader car pack too). It was
/// Driving+'s before; Driving+ hands over its old [GarageLift] values once (<see cref="TakeOver"/>) and no longer has one.
/// </summary>
internal sealed class GarageLiftFeature : IFeature
{
    internal const string Section = "GarageLift";

    internal static ConfigEntry<bool> Preview, ShowText;
    internal static ConfigEntry<float> EaseSeconds;

    public void Register()
    {
        var cfg = CoreConfig.File;
        Preview = cfg.Bind(Section, "Preview", true,
            "In the garage, the car rises or drops to the suspension stage you highlight, and goes back to the fitted one after. " +
            "Only the garage model moves: nothing is changed, saved or sent.");
        ShowText = cfg.Bind(Section, "ShowText", true,
            "Adds a line like \"Ride height: +8 cm\" under the stage's description.");
        EaseSeconds = cfg.Bind(Section, "EaseSeconds", LiftEase.DefaultSeconds,
            new ConfigDescription("How long the car takes to rise or drop, in seconds.",
                new AcceptableValueRange<float>(LiftEase.MinSeconds, LiftEase.MaxSeconds)));

        Registry.OnTick(GarageLift.Tick);
        Registry.OnGui(GarageLift.Gui, wants: GarageLift.GuiWanted, repaintOnly: true);
        Registry.Section(Registry.PlusMode, "Garage", DrawSection, 40);
    }

    /// <summary>Core's own page (Advanced, Garage): for a game without Air Ride, whose page has the same two rows.</summary>
    private static void DrawSection()
    {
        if (Preview == null) return;
        Menu.Card("Lift preview", "The garage car shows the suspension stage you highlight.", () =>
        {
            Menu.Switch(Preview, "Lift preview", "The car rises or drops to the stage you highlight. Only the garage model moves.", true);
            Menu.Switch(ShowText, "Ride height text", "A line like \"Ride height: +8 cm\" under the stage.", true);
        });
    }

    /// <summary>The two rows for a mod's own page (Air Ride's Suspension group; Driving+ without Air Ride).</summary>
    internal static void DrawRows()
    {
        if (Preview == null) return;
        UI.SimpleMenu.Toggle(Preview, "Garage lift preview", "In the garage, the car rises or drops to the suspension stage you highlight. Only the garage model moves.");
        UI.SimpleMenu.Toggle(ShowText, "Ride height text", "A line like \"Ride height: +8 cm\" under the stage.", Preview.Value);
    }

    /// <summary>
    /// A value an older Driving+ kept in its own file ([GarageLift] there): it goes to core's entry of the same key,
    /// once (Driving+ drops its copy after). Unknown keys and values that don't parse are ignored.
    /// </summary>
    internal static void TakeOver(string key, string serialized)
    {
        ConfigEntryBase entry = key switch
        {
            "Preview" => Preview,
            "ShowText" => ShowText,
            "EaseSeconds" => EaseSeconds,
            _ => null,
        };
        if (entry == null || serialized == null) return;
        try { entry.SetSerializedValue(serialized); }
        catch (System.Exception e) { DebugLog.Error("core", e, "GarageLiftFeature.TakeOver"); }
    }
}
