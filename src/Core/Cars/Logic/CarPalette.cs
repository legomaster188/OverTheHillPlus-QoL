using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Cars;

/// <summary>A named group of the shared palette.</summary>
public sealed class PaletteGroup
{
    public string Title;
    public PaintPreset[] Paints;

    public PaletteGroup(string title, params PaintPreset[] paints) { Title = title; Paints = paints; }
}

/// <summary>
/// The paints and trims every modded car offers after its own. A paint travels as its RGB (CarLookWire) and is saved by
/// name in the car's config (Look/Paint), so the palette can grow without moving anything: an old save's name still means
/// the same colour, and a car's own preset of the same name wins over the shared one. The albedo is recoloured in sRGB
/// bytes (CarPaint.Recolour), so there is no metal or gloss control: metallics and pearls are the flat colour they look
/// like, not a shader finish. Names are plain words (no brand colour names). Pure (unit-tested).
/// </summary>
public static class CarPalette
{
    private static PaintPreset P(string name, int rgb) => new(name, new CarRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    public static readonly PaletteGroup[] Groups =
    {
        new("Basics",
            P("White", 0xF2F2EE), P("Off-white", 0xE4E1D6), P("Silver", 0xB4B7BA), P("Light grey", 0x9EA2A5),
            P("Mid grey", 0x6E7276), P("Dark grey", 0x3E4145), P("Black", 0x16171A), P("Red", 0xB5201F),
            P("Dark red", 0x6E1517), P("Orange", 0xD9611A), P("Yellow", 0xE3B91C), P("Lime", 0x8FB82B),
            P("Green", 0x2F7D3B), P("Dark green", 0x173F2A), P("Teal", 0x1F7A7A), P("Light blue", 0x6FA6D6),
            P("Blue", 0x1F4E9C), P("Navy", 0x14213F), P("Purple", 0x59308A), P("Pink", 0xE58AA8),
            P("Brown", 0x5C3A21), P("Tan", 0xB28E5E), P("Beige", 0xCDB99A), P("Cream", 0xEFE3C0)),
        new("Metallics and pearls",
            P("Silver metallic", 0xA9AEB3), P("Gunmetal", 0x4A5057), P("Champagne", 0xC9B58A), P("Bronze", 0x8A6234),
            P("Copper", 0xA85A36), P("Candy red", 0x9E1224), P("Metallic blue", 0x2A5C96), P("Metallic green", 0x2F6B52),
            P("Pearl white", 0xEDECE6), P("Graphite", 0x34373B)),
        new("Classic and vintage",
            P("Seafoam", 0x8DC4B0), P("Mint", 0xA8D8BC), P("Sky blue", 0x7FB5DA), P("Harvest gold", 0xC8922A),
            P("Avocado", 0x6B7A33), P("Burnt orange", 0xB5521E), P("Sand", 0xD2BC8C), P("Ivory", 0xF0E8CF),
            P("Maroon", 0x5A1A24), P("Forest", 0x1F4A2C), P("Powder blue", 0xA9C6DC), P("Rose", 0xC98B93),
            P("Burgundy", 0x4F1626)),
        new("Off-road and utility",
            P("Olive drab", 0x555B34), P("Desert tan", 0xB59E75), P("Khaki", 0x9A8E62), P("Safety orange", 0xE8561A),
            P("Hi-vis yellow", 0xD8E21B), P("Fire red", 0xC41A16), P("Rescue green", 0x2E8A4A), P("Rust primer", 0x7A4A38),
            P("Matte black", 0x242527)),
        new("Fun",
            P("Lime green", 0x7CE02B), P("Hot pink", 0xE8318F), P("Electric purple", 0x8A2BE2), P("Turquoise", 0x1FC2B7),
            P("Sunset orange", 0xF47A2B), P("Magenta", 0xC2189A), P("Aqua", 0x3FD0E8), P("Banana yellow", 0xF2DC4A)),
    };

    /// <summary>The shared trims, after a car's own.</summary>
    public static readonly TrimPreset[] Trims =
    {
        new("Black", new CarRgb(26, 26, 28)), new("Chrome", new CarRgb(196, 199, 203)),
        new("Satin black", new CarRgb(48, 49, 52)), TrimPreset.Body(), new("White", new CarRgb(238, 238, 234)),
        new("Gunmetal", new CarRgb(74, 80, 87)), new("Bronze", new CarRgb(138, 98, 52)), new("Red", new CarRgb(181, 32, 31)),
    };

    /// <summary>The shared paints in order, all groups.</summary>
    public static IEnumerable<PaintPreset> AllPaints()
    {
        foreach (var g in Groups)
            foreach (var p in g.Paints) yield return p;
    }

    /// <summary>A shared paint by name (any case), or null.</summary>
    public static PaintPreset FindPaint(string name)
    {
        if (name == null) return null;
        foreach (var p in AllPaints())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>A shared trim by name (any case), or null.</summary>
    public static TrimPreset FindTrim(string name)
    {
        if (name == null) return null;
        foreach (var t in Trims)
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    /// <summary>True when <paramref name="own"/> (a car's presets) has no preset of this name: the shared one shows.</summary>
    public static bool Offered(IReadOnlyList<PaintPreset> own, string name)
    {
        foreach (var p in own)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>The same for trims.</summary>
    public static bool Offered(IReadOnlyList<TrimPreset> own, string name)
    {
        foreach (var t in own)
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
