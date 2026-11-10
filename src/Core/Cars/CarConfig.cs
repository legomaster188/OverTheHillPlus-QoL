using System;
using OverTheHillPlus.Config;

namespace OverTheHillPlus.Cars;

/// <summary>
/// A modded car's settings in its mod's config file (BepInEx/config/oth.&lt;id&gt;.cfg). Section [&lt;CarName&gt;]: Donor,
/// GarageBase, ShowInGarage; section [Look]: Paint, PaintCustom, Trim, TrimCustom, Skin, EngineSound. For the Vera these
/// are the keys oth.vera.cfg had before the framework.
/// </summary>
internal sealed class CarConfig
{
    private readonly RuntimeCar _car;

    public ConfigEntry<string> Donor;
    public ConfigEntry<string> InfoBase;
    public ConfigEntry<bool> ShowInGarage;
    public ConfigEntry<string> Paint, PaintCustom, Trim, TrimCustom, Skin, EngineSound;

    public ConfigFile File { get; }

    public CarConfig(RuntimeCar car, ConfigFile cfg)
    {
        _car = car;
        File = cfg;
        // The entries are bound by CarConfigBinder (pure, so the installer's settings export runs the very same code).
        var e = CarConfigBinder.Bind(car.Def, cfg);
        Donor = e.Donor; InfoBase = e.InfoBase; ShowInGarage = e.ShowInGarage;
        Paint = e.Paint; PaintCustom = e.PaintCustom; Trim = e.Trim; TrimCustom = e.TrimCustom; Skin = e.Skin; EngineSound = e.EngineSound;
    }

    /// <summary>The garage base as a CarInfo name ("Car_TrailBurner" and "TrailBurner" both mean the TrailBurner).</summary>
    public string GarageBaseName => _car.Def.GarageBaseName(InfoBase?.Value);

    /// <summary>What every peer's template must agree on beyond the guid: in core's join hash (<see cref="CarDefinition.LayoutFor"/>).</summary>
    public string Layout => _car.Def.LayoutFor(Donor?.Value, InfoBase?.Value);
}
