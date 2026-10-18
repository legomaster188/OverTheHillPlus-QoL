using System;
using BepInEx.Configuration;

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
        var d = car.Def;
        string n = d.CarName, section = d.CarName;
        Donor = cfg.Bind(section, "Donor", d.DefaultDonor,
            $"Fusion prefab the {n}'s network and physics skeleton is copied from (its body, wheels and cosmetics are replaced), " +
            $"e.g. {d.DefaultDonor}. Everyone in a session must use the same one: players with another donor " +
            "are refused when joining. Applies after a restart.");
        InfoBase = cfg.Bind(section, "GarageBase", d.DefaultGarageBase,
            $"Stock car whose garage data the {n} copies: vehicle class, upgrade lists (liveries left out) and garage display model. " +
            "Everyone in a session must use the same one. Applies after a restart.");
        ShowInGarage = cfg.Bind(section, "ShowInGarage", true,
            $"List the {n} in the garage (it is yours for free). Off: drive it from the MODS page only.");
        var paintNames = new string[d.Paints.Length];
        for (int i = 0; i < paintNames.Length; i++) paintNames[i] = d.Paints[i].Name;
        var trimNames = new string[d.Trims.Length];
        for (int i = 0; i < trimNames.Length; i++) trimNames[i] = d.Trims[i].Name;
        Paint = cfg.Bind("Look", "Paint", d.Paints[0].Name,
            "Body colour: " + string.Join(", ", paintNames) + ", or Custom (PaintCustom). Other players with this mod see it too.");
        PaintCustom = cfg.Bind("Look", "PaintCustom", d.PaintCustomDefault, "The body colour when Paint is Custom (#RRGGBB).");
        Trim = cfg.Bind("Look", "Trim", d.Trims[0].Name,
            d.TrimWhat + " colour: " + string.Join(", ", trimNames) + ", or Custom (TrimCustom).");
        TrimCustom = cfg.Bind("Look", "TrimCustom", d.TrimCustomDefault, "The trim colour when Trim is Custom (#RRGGBB).");
        Skin = cfg.Bind("Look", "Skin", "",
            "A skin from a livery pack (\"<pack folder>/<livery name>\", see the MODS page), or empty for the paint colours. " +
            "A skin replaces the whole paint; players who have the same pack see it, the others see your colours.");
        var labels = new string[d.EngineSounds.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = d.EngineSounds[i].Label;
        EngineSound = cfg.Bind("Look", "EngineSound", d.EngineLabel(d.DefaultEngine),
            $"The stock engine sound your {n} uses: " + string.Join(", ", labels) + ". Other players with this mod hear the one you picked.");
    }

    /// <summary>The garage base as a CarInfo name ("Car_TrailBurner" and "TrailBurner" both mean the TrailBurner).</summary>
    public string GarageBaseName => _car.Def.GarageBaseName(InfoBase?.Value);

    /// <summary>What every peer's template must agree on beyond the guid: in core's join hash (<see cref="CarDefinition.LayoutFor"/>).</summary>
    public string Layout => _car.Def.LayoutFor(Donor?.Value, InfoBase?.Value);
}
