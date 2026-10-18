using System.Collections.Generic;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Which of a modded car's upgrade objects (the game's VehicleUpgradeObject: one GameObject per cosmetic or upgrade
/// model, pointing at its part) can never be shown on the car and must be switched off when the car is built. Pure code
/// (unit-tested).
/// <para>Why: the game registers a car's upgrade objects from its CarInfo's part list only
/// (VehicleCustomizationController.InitVehicleUpgrades, LocalVehicleCustomizationController.InitParts) and only ever
/// switches registered objects on or off. A modded car's list is the donor's minus what CarCatalog leaves out
/// (liveries, protection and inventory upgrades, bed cosmetics, ...), so the objects of the parts left out were never
/// registered and kept the state they have in the donor's prefab: on. They showed on the car whatever was mounted,
/// and nothing in the garage could take them off.</para>
/// </summary>
public static class CarPartObjects
{
    /// <summary>
    /// The objects to switch off. <paramref name="objectParts"/> holds, per object, the item ids of the parts its upgrade
    /// components point at (0 for a component without a part). An object goes off when one of its parts is on the
    /// donor's list but not on the car's (<paramref name="donorParts"/> minus <paramref name="carParts"/>), and none of
    /// its parts is on the car's list (the game shows and hides that one itself). Objects whose parts the donor doesn't
    /// list either are left alone: the stock game never switches those, so they are part of the donor's look.
    /// </summary>
    public static List<int> ToHide(IReadOnlyList<uint[]> objectParts, ICollection<uint> donorParts, ICollection<uint> carParts)
    {
        var hide = new List<int>();
        if (objectParts == null || donorParts == null || carParts == null) return hide;
        for (int i = 0; i < objectParts.Count; i++)
        {
            var parts = objectParts[i];
            if (parts == null || parts.Length == 0) continue;
            bool kept = false, dropped = false;
            foreach (uint id in parts)
            {
                if (id == 0) continue;
                if (carParts.Contains(id)) kept = true;
                else if (donorParts.Contains(id)) dropped = true;
            }
            if (dropped && !kept) hide.Add(i);
        }
        return hide;
    }
}
