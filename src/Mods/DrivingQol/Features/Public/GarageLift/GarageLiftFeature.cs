using BepInEx.Configuration;
using OverTheHillPlus.DrivingQol;

namespace OverTheHillPlus.Features.Public.GarageLift;

/// <summary>Garage lift preview: its settings and per-frame work (see <see cref="GarageLift"/>).</summary>
internal sealed class GarageLiftFeature : IFeature
{
    internal const string Section = "GarageLift";

    internal static ConfigEntry<bool> Preview, ShowText;
    internal static ConfigEntry<float> EaseSeconds;

    public void Register()
    {
        var cfg = DrivingQolConfig.File;
        Preview = cfg.Bind(Section, "Preview", true,
            "In the garage, the car's body rises or drops to the suspension lift stage you highlight (and goes back to the fitted one when you leave it). " +
            "Only the garage model moves; nothing is changed on your car, saved or sent to other players.");
        ShowText = cfg.Bind(Section, "ShowText", true,
            "Add a line such as \"Ride height: +8 cm\" under the lift stage's description in the garage.");
        EaseSeconds = cfg.Bind(Section, "EaseSeconds", LiftEase.DefaultSeconds,
            new ConfigDescription("How long the car takes to rise or drop to the highlighted stage, in seconds.",
                new AcceptableValueRange<float>(LiftEase.MinSeconds, LiftEase.MaxSeconds)));

        Registry.OnTick(GarageLift.Tick);
        Registry.OnGui(GarageLift.Gui);
    }
}
