using OverTheHillPlus.UI;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>Custom Cosmetics' page: what loaded, what was left out, and the one setting.</summary>
internal static class CustomCosmeticsSimple
{
    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (CustomCosmeticsConfig.StockCars == null) return;
        SimpleMenu.Heading(TuneGroups.Looks);
        Menu.KeyValue("In this build", $"{PartLibrary.Parts.Count} part(s) · {PartObjects.CarsDone} car list(s) extended so far");
        foreach (var p in PartLibrary.Parts)
        {
            int now = CosmeticState.PriceNow(p);
            Menu.KeyValue(p.Manifest.Name, $"{p.Manifest.CosmeticType} · {(now > 0 ? "$" + now : p.Manifest.Price > 0 ? "free here ($" + p.Manifest.Price + " in a career)" : "free")} · fits {string.Join(", ", p.Manifest.Fits.Keys)}");
        }
        foreach (var why in PartLibrary.Problems) SimpleMenu.Note("Left out: " + why);
        if (CustomCosmeticsPlugin.Problem != null) SimpleMenu.Note("Switched off: " + CustomCosmeticsPlugin.Problem);
        else if (PartObjects.Off) SimpleMenu.Note("Switched off for this session after repeated errors (see the log).");
        SimpleMenu.Heading("Cars");
        SimpleMenu.Toggle(CustomCosmeticsConfig.StockCars, "Also on the game's cars",
            "Adds the parts that list a game car to that car too (restart the game to be sure). The Mod Manager's own cars always get them.");
        SimpleMenu.Note("Wheels are in the Fenders tab: they change the rims, your tyres stay, and they don't take the mudflaps off. " +
                        "Parts you buy are kept in this mod's own file, not in the game's save; players without the mod see stock wheels.");
        if (CustomCosmeticsConfig.Twinkle != null)
        {
            SimpleMenu.Heading("Lights");
            SimpleMenu.Toggle(CustomCosmeticsConfig.Twinkle, "Twinkling string lights",
                "String lights and Christmas lights fade in three sets in turn while your lamps are on. Off: they just glow.");
        }
        if (CustomCosmeticsConfig.PricesInFreePlay != null)
        {
            SimpleMenu.Heading("Prices");
            SimpleMenu.Toggle(CustomCosmeticsConfig.PricesInFreePlay, "Prices in free play",
                "Parts cost money outside a career too. In a career they always do, and what you buy goes in your career's stuff.");
        }
        SimpleMenu.Heading("Also changes the look");
        SimpleMenu.Links(TuneGroups.LookPages, CustomCosmeticsPlugin.ModId);
    }
}
