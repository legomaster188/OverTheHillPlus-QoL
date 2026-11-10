namespace OverTheHillPlus.Cars;

/// <summary>The game's mounted-parts set as four 64-bit words (see UpgradeBits).</summary>
public static class UpgradeWords
{
    public const int Count = 4, Bits = 64 * Count;

    public static bool IsSet(ulong[] words, int bit) =>
        words != null && bit >= 0 && bit < Bits && (bit >> 6) < words.Length && (words[bit >> 6] >> (bit & 63) & 1UL) != 0UL;
}
