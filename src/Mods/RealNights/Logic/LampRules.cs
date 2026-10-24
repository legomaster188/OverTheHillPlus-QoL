using System;

namespace OverTheHillPlus.RealNights;

internal enum LampKind { None, Forward, Beacon }

internal enum LampTint { White, Amber, Blue, Red, RedBlue }

/// <summary>Which cosmetic items carry lamps, where they sit in the item and how bright they are: pure rules, no game types.</summary>
internal static class LampRules
{
    /// <summary>The prefix of the objects the Custom Cosmetics mod makes (CC_ + the part id).</summary>
    public const string CustomPrefix = "CC_";

    /// <summary>
    /// The lamp kind of a cosmetic item from the slot it sits in and its object name. The game's own items are named in
    /// its slots (Roof_Bar/Lights_BeaconBar_Amber, Front/Bullbar_01_LightsRound); the custom parts hang straight under
    /// Visual as CC_&lt;id&gt;, so those go by their id.
    /// </summary>
    public static LampKind Classify(string slot, string item, out LampTint tint)
    {
        tint = LampTint.White;
        if (string.IsNullOrEmpty(item)) return LampKind.None;
        if (item.StartsWith(CustomPrefix, StringComparison.Ordinal))
        {
            string id = item.Substring(CustomPrefix.Length);
            if (Has(id, "tail") || Has(id, "rear") || Has(id, "brake") || Has(id, "reverse")) return LampKind.None;
            if (Has(id, "beacon")) { tint = TintOf(id, LampTint.Amber); return LampKind.Beacon; }
            return Has(id, "light") || Has(id, "lamp") || Has(id, "spot") || Has(id, "bullbar") || Has(id, "bull_bar") || Has(id, "beam")
                ? LampKind.Forward : LampKind.None;
        }
        if (string.Equals(slot, "Roof_Bar", StringComparison.OrdinalIgnoreCase) && item.StartsWith("Lights_", StringComparison.OrdinalIgnoreCase))
        {
            if (!Has(item, "beacon")) return LampKind.Forward;
            tint = TintOf(item, LampTint.Amber);
            return LampKind.Beacon;
        }
        if (string.Equals(slot, "Front", StringComparison.OrdinalIgnoreCase) && Has(item, "bullbar") && Has(item, "lights"))
            return LampKind.Forward;
        return LampKind.None;
    }

    private static bool Has(string s, string what) => s.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0;

    private static LampTint TintOf(string name, LampTint fallback)
    {
        if (Has(name, "redblue")) return LampTint.RedBlue;
        if (Has(name, "amber")) return LampTint.Amber;
        if (Has(name, "blue")) return LampTint.Blue;
        if (Has(name, "red")) return LampTint.Red;
        return fallback;
    }

    public static (float r, float g, float b) Rgb(LampTint tint) => tint switch
    {
        LampTint.Amber => (1f, 0.62f, 0.12f),
        LampTint.Blue => (0.2f, 0.4f, 1f),
        LampTint.Red => (1f, 0.14f, 0.1f),
        LampTint.RedBlue => (0.8f, 0.2f, 0.8f),
        _ => (1f, 0.95f, 0.85f),
    };

    /// <summary>How many lights an item gets, by its width: a wide bar gets more than a small lamp, never more than <paramref name="cap"/>.</summary>
    public static int CountFor(LampKind kind, float halfWidth, int cap)
    {
        if (kind == LampKind.None || cap < 1) return 0;
        if (kind == LampKind.Beacon) return 1;
        int n = (int)Math.Round(halfWidth * 2f / 0.55f);
        return Math.Max(1, Math.Min(cap, n));
    }

    /// <summary>Sideways positions (metres from the item's centre) for <paramref name="count"/> lights over a bar of this half width.</summary>
    public static float[] Offsets(int count, float halfWidth)
    {
        if (count <= 0) return Array.Empty<float>();
        var xs = new float[count];
        if (count == 1) return xs;
        float span = halfWidth * 0.8f;
        for (int i = 0; i < count; i++) xs[i] = -span + 2f * span * i / (count - 1);
        return xs;
    }

    /// <summary>A lamp's spot angle in degrees: a bar throws wide, a lamp on a bull bar narrower.</summary>
    public static float SpotAngle(LampKind kind, int count) => kind == LampKind.Beacon ? 0f : count > 1 ? 75f : 55f;

    /// <summary>
    /// A lamp's intensity from the car's own headlight intensity (so the game's units carry over), the player's
    /// multiplier and how dark it is. Several lights over one bar share the bar's output.
    /// </summary>
    public static float Intensity(float headlight, float userMul, float need, LampKind kind, int count)
    {
        if (kind == LampKind.None) return 0f;
        float share = kind == LampKind.Beacon ? 0.25f : 0.8f / (float)Math.Sqrt(Math.Max(1, count));
        return Math.Max(0f, headlight * share * Math.Max(0f, userMul) * Math.Max(0f, Math.Min(1f, need)));
    }

    /// <summary>A lamp's range from the car's own headlight range; a beacon is a short, wide glow.</summary>
    public static float Range(float headlight, float userMul, LampKind kind)
    {
        float mul = Math.Max(0f, userMul);
        return kind == LampKind.Beacon ? 14f * mul : headlight * 0.9f * mul;
    }
}
