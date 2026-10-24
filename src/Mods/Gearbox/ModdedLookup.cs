namespace OverTheHillPlus.GearboxPlus;

/// <summary>What a modded car says about its real gearbox and differentials (core's car framework keeps its definition). Looked up by the
/// car's name as the game has it ("Vera"); null for a car that is not modded.</summary>
internal static class ModdedLookup
{
    internal static string GearSetOf(string name) => OverTheHillPlus.Cars.ModdedCars.ByCarName(name)?.Def.GearSet;

    internal static byte[] DiffsOf(string name) => OverTheHillPlus.Cars.ModdedCars.ByCarName(name)?.Def.Diffs;
}
