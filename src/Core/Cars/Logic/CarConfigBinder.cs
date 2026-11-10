using OverTheHillPlus.Config;

namespace OverTheHillPlus.Cars;

/// <summary>The settings entries of one modded car (see <see cref="CarConfig"/>).</summary>
internal sealed class CarConfigEntries
{
    public ConfigEntry<string> Donor, InfoBase;
    public ConfigEntry<bool> ShowInGarage;
    public ConfigEntry<string> Paint, PaintCustom, Trim, TrimCustom, Skin, EngineSound;
}

/// <summary>
/// Binds a modded car's settings: section [&lt;CarName&gt;] (Donor, GarageBase, ShowInGarage) and [Look] (Paint,
/// PaintCustom, Trim, TrimCustom, Skin, EngineSound). Pure (no game types), so the installer's settings export runs
/// this very code to learn every car's keys, defaults and descriptions.
/// </summary>
internal static class CarConfigBinder
{
    public static CarConfigEntries Bind(CarDefinition d, ConfigFile cfg)
    {
        var e = new CarConfigEntries();
        string n = d.CarName, section = d.CarName;
        e.Donor = cfg.Bind(section, "Donor", d.DefaultDonor,
            $"Fusion prefab the {n}'s network and physics skeleton is copied from (its body, wheels and cosmetics are replaced), " +
            $"e.g. {d.DefaultDonor}. Everyone in a session must use the same one: players with another donor " +
            "are refused when joining. Applies after a restart.");
        e.InfoBase = cfg.Bind(section, "GarageBase", d.DefaultGarageBase,
            $"Stock car whose garage data the {n} copies: vehicle class, upgrade lists (liveries left out) and garage display model. " +
            "Everyone in a session must use the same one. Applies after a restart.");
        e.ShowInGarage = cfg.Bind(section, "ShowInGarage", true,
            $"List the {n} in the garage (it is yours for free). Off: drive it from the MODS page only.");
        var paintNames = new string[d.Paints.Length];
        for (int i = 0; i < paintNames.Length; i++) paintNames[i] = d.Paints[i].Name;
        var trimNames = new string[d.Trims.Length];
        for (int i = 0; i < trimNames.Length; i++) trimNames[i] = d.Trims[i].Name;
        e.Paint = cfg.Bind("Look", "Paint", d.Paints[0].Name,
            "Body colour: " + string.Join(", ", paintNames) + ", or Custom (PaintCustom). Other players with this mod see it too.");
        e.PaintCustom = cfg.Bind("Look", "PaintCustom", d.PaintCustomDefault, "The body colour when Paint is Custom (#RRGGBB).");
        e.Trim = cfg.Bind("Look", "Trim", d.Trims[0].Name,
            d.TrimWhat + " colour: " + string.Join(", ", trimNames) + ", or Custom (TrimCustom).");
        e.TrimCustom = cfg.Bind("Look", "TrimCustom", d.TrimCustomDefault, "The trim colour when Trim is Custom (#RRGGBB).");
        e.Skin = cfg.Bind("Look", "Skin", "",
            "A skin from a livery pack (\"<pack folder>/<livery name>\", see the MODS page), or empty for the paint colours. " +
            "A skin replaces the whole paint; players who have the same pack see it, the others see your colours.");
        var labels = new string[d.EngineSounds.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = d.EngineSounds[i].Label;
        e.EngineSound = cfg.Bind("Look", "EngineSound", d.EngineLabel(d.DefaultEngine),
            $"The stock engine sound your {n} uses: " + string.Join(", ", labels) + ". Other players with this mod hear the one you picked.");
        return e;
    }
}
