using System;
using OverTheHillPlus.Cars;
using OverTheHillPlus.Features;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// Custom Cosmetics' per-frame work, once a second: as soon as the game's item catalog and the Mod Manager's cars are
/// there, the parts are appended to the cars' lists and put in the catalog (again if the game rebuilds it), and the part
/// names are kept in the string table. Everything else happens in the hooks, when the game asks.
/// </summary>
internal sealed class CosmeticsFeature : IFeature
{
    private static float _next;
    private static IntPtr _catalog;

    public void Register() => Registry.OnTick(Tick);

    /// <summary>Is this item id used by an item of the game? (Asked while the parts load, so before the catalog is there it
    /// says no; <see cref="PartObjects.RegisterAll"/> checks again when it adds the items.)</summary>
    internal static bool IdTaken(uint id)
    {
        try
        {
            var cat = PlayerInventoryManager.InventoryItemsCatalog;
            return cat?.IdToSO != null && cat.IdToSO.ContainsKey(id);
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "CosmeticsFeature.IdTaken"); return false; }
    }

    private static void Tick()
    {
        if (CustomCosmeticsPlugin.Problem != null || PartObjects.Off) return;
        PartText.Tick();
        if (UnityEngine.Time.unscaledTime < _next) return;
        _next = UnityEngine.Time.unscaledTime + 1f;
        try
        {
            if (!CarCatalog.CatalogLoaded) return;
            var cat = PlayerInventoryManager.InventoryItemsCatalog;
            // Modded cars carry their own copy of the part list: add the parts as soon as the car is registered, so the
            // garage lists them without anything having to ask first.
            foreach (var car in ModdedCars.All)
                if (car.Ready) PartObjects.EnsureCar(car.Catalog.Info);
            if (cat != null && cat.Pointer != _catalog && PartObjects.SoCount > 0)
            {
                _catalog = cat.Pointer;
                PartObjects.RegisterAll();
            }
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "CosmeticsFeature.Tick"); }
    }
}
