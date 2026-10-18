using OverTheHillPlus.UI;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>Custom Cosmetics' page: what loaded, what was left out, and the one setting.</summary>
internal static class CustomCosmeticsSimple
{
    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (CustomCosmeticsConfig.StockCars == null) return;
        SimpleMenu.Heading("Parts");
        SimpleMenu.Note($"{PartLibrary.Parts.Count} part(s) in this build; {PartObjects.CarsDone} car list(s) extended so far.");
        foreach (var p in PartLibrary.Parts)
            SimpleMenu.Note($"{p.Manifest.Name}: {p.Manifest.CosmeticType}, {(p.Manifest.Price > 0 ? "$" + p.Manifest.Price : "free")}, fits {string.Join(", ", p.Manifest.Fits.Keys)}");
        foreach (var why in PartLibrary.Problems) SimpleMenu.Note("Left out: " + why);
        if (CustomCosmeticsPlugin.Problem != null) SimpleMenu.Note("Switched off: " + CustomCosmeticsPlugin.Problem);
        else if (PartObjects.Off) SimpleMenu.Note("Switched off for this session after repeated errors (see the log).");
        SimpleMenu.Heading("Cars");
        SimpleMenu.Toggle(CustomCosmeticsConfig.StockCars, "Also on the game's cars",
            "Adds the parts that list a game car to that car too (restart the game to be sure). The Mod Manager's own cars always get them.");
        SimpleMenu.Note("Parts you buy are kept in this mod's own file, not in the game's save. Players without the mod " +
                        "don't see your parts; nothing breaks for them.");
    }
}
