namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// What a custom part costs and who owns it, in free play and in a career (core's Api.Career). In free play the parts are
/// free unless the player asks for prices, and ownership is the mod's sidecar. In a career a part costs its price and is a
/// career item (<c>Career.Items</c>, kind "cosmetic", id <see cref="ItemKey"/>): bought into the inventory, taken out of it
/// when it goes on a car and put back when it comes off, so it can be sold on the market like the game's cosmetics. A part
/// mounted on a car is that car's whatever the inventory says. Pure (unit-tested).
/// </summary>
public static class PartPricing
{
    public const string ItemPrefix = "cc.";

    /// <summary>The career item id of a part ("cc.light_pods").</summary>
    public static string ItemKey(string partId) => ItemPrefix + partId;

    /// <summary>The part id of a career item id, null when it isn't one of ours.</summary>
    public static string PartOf(string key) =>
        key != null && key.Length > ItemPrefix.Length && key.StartsWith(ItemPrefix, System.StringComparison.Ordinal) ? key.Substring(ItemPrefix.Length) : null;

    /// <summary>The price the garage asks now.</summary>
    public static int PriceNow(int price, bool career, bool pricesInFreePlay) => career || pricesInFreePlay ? System.Math.Max(0, price) : 0;

    /// <summary>Is the part this car's to mount? Free ones always; in a career one in the inventory or already on the car;
    /// in free play what the sidecar says.</summary>
    public static bool Owned(int priceNow, bool career, int inInventory, bool sidecarOwns, bool onThisCar) =>
        priceNow <= 0 || onThisCar || (career ? inInventory > 0 : sidecarOwns);

    /// <summary>What a career's inventory does when a part goes on (+1: put back; -1: taken out; 0: nothing): a part comes
    /// out of the inventory when it is mounted and goes back when it is taken off; buying straight onto a car never
    /// passes through it.</summary>
    public static int InventoryChange(bool mounting, bool wasOnThisCar, int inInventory)
    {
        if (mounting) return !wasOnThisCar && inInventory > 0 ? -1 : 0;
        return wasOnThisCar ? 1 : 0;
    }
}
