using OverTheHillPlus.Config;

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// The Meerkat's build in free play (oth.meerkat.cfg, section Build): what the car's page switches. In a career the
/// career's bought upgrades decide instead. Free play costs nothing: the career's prices only apply inside a career.
/// </summary>
internal static class MeerkatConfig
{
    public static ConfigEntry<int> Lift, Tyre, Top, Engine;
    public static ConfigEntry<bool> Skid, Rack, Flares, Flaps, Hook, Snorkel, Spare;
    public static ConfigEntry<bool> PopUps, PricesInFreePlay;
    public static ConfigEntry<string> Owned;
    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        var d = MeerkatBuild.Safari;
        Lift = cfg.Bind("Build", "Lift", (int)d.Lift, "Free play: 0 stock height (14 in wheels), 1 a 2 in lift, 2 the 3 in lift it comes with, 3 a 4 in lift (bigger tyres).");
        Tyre = cfg.Bind("Build", "Tyres", (int)d.Tyre, "Free play: 0 road tyres, 1 all-terrains (as it comes), 2 mud-terrains.");
        Top = cfg.Bind("Build", "Top", (int)d.Top, "Free play: 0 the hardtop, 1 the soft top, 2 no top.");
        Engine = cfg.Bind("Build", "EngineStage", (int)d.Engine, "Free play: 0 stock, 1 intake and exhaust, 2 cams and a turbo kit.");
        Skid = cfg.Bind("Build", "SkidPlate", d.Has(Addons.Skid), "Free play: the skid plate under the nose.");
        Rack = cfg.Bind("Build", "RackAndLamps", d.Has(Addons.Rack), "Free play: the roof rack and driving lamps (a lamp bar over the screen without the hardtop).");
        Flares = cfg.Bind("Build", "FenderFlares", d.Has(Addons.Flares), "Free play: the fender flares.");
        Flaps = cfg.Bind("Build", "MudFlaps", d.Has(Addons.Flaps), "Free play: the mud flaps.");
        Hook = cfg.Bind("Build", "TowHook", d.Has(Addons.Hook), "Free play: the front tow eye.");
        Snorkel = cfg.Bind("Build", "Snorkel", d.Has(Addons.Snorkel), "Free play: the raised air intake up the right pillar.");
        Spare = cfg.Bind("Build", "SpareCarrier", d.Has(Addons.Spare), "Free play: the spare wheel on the trunk lid.");
        PopUps = cfg.Bind("Look", "PopUpHeadlamps", true, "The headlamps pop up when the lights come on (off: they stay up).");
        PricesInFreePlay = cfg.Bind("Prices", "PricesInFreePlay", false, "The build's choices cost their price in free play too (once each, in the game's pebbles). Off: free outside a career; in a career they are career upgrades and always cost money.");
        Owned = cfg.Bind("Prices", "Owned", "", "Free play with prices on: the choices bought so far (kept by the mod).");
    }

    /// <summary>The free-play build as the config has it.</summary>
    public static MeerkatBuild FreeBuild
    {
        get
        {
            if (Lift == null) return MeerkatBuild.Safari;
            var parts = Addons.None;
            if (Skid.Value) parts |= Addons.Skid;
            if (Rack.Value) parts |= Addons.Rack;
            if (Flares.Value) parts |= Addons.Flares;
            if (Flaps.Value) parts |= Addons.Flaps;
            if (Hook.Value) parts |= Addons.Hook;
            if (Snorkel.Value) parts |= Addons.Snorkel;
            if (Spare.Value) parts |= Addons.Spare;
            return new MeerkatBuild((LiftStage)Clamp(Lift.Value, 3), (TyreKind)Clamp(Tyre.Value, 2), (TopKind)Clamp(Top.Value, 2), parts,
                (byte)Clamp(Engine.Value, MeerkatBuild.MaxEngine));
        }
    }

    private static int Clamp(int v, int max) => v < 0 ? 0 : v > max ? max : v;
}
