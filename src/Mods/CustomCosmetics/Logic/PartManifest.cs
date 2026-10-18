using System;
using System.Collections.Generic;
using System.Text.Json;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// One custom part's <c>part.json</c> (the contract is Liveries/cosmetics/FORMAT.md): its name, the game's cosmetic type
/// (which garage tab it is in), the donor slot it belongs to, its price and where it sits on each car it fits. Pure
/// (unit-tested); every problem makes the part unusable and says why, nothing throws. A part whose <c>fits</c> is still
/// empty (the model is not placed on any car yet) is valid and simply offered on no car.
/// </summary>
public sealed class PartManifest
{
    /// <summary>The CosmeticType names a part may use (the game's, without Hidden): the garage tabs already exist.</summary>
    public static readonly string[] CosmeticTypes =
    {
        "FrontSection", "RearSection", "SideSection", "FrontBumper", "RearBumper", "Hood", "Roof", "Bed", "RoofLights",
        "Fenders", "SideStep", "Antenna", "BushCables", "Horns",
    };

    public string Id;
    public string Name;
    public string CosmeticType;
    /// <summary>The donor slot the part belongs to ("Visual/Roof_Bar"), "" when the manifest has none.</summary>
    public string Slot = "";
    public int Price;
    public bool Paintable;
    public readonly Dictionary<string, PartFit> Fits = new(StringComparer.OrdinalIgnoreCase);

    public const int MaxPrice = 10_000_000;

    /// <summary>Reads a manifest. Null with <paramref name="error"/> when it can't be used. <paramref name="folderId"/> is the
    /// part's folder, which the manifest's id must match.</summary>
    public static PartManifest Parse(string json, string folderId, out string error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "", new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "part.json is not an object"; return null; }
            var m = new PartManifest();
            m.Id = Str(root, "id");
            if (!CosmeticIds.ValidPartId(m.Id)) { error = $"id \"{m.Id}\" is not a valid part id (lowercase letters, digits, underscores)"; return null; }
            if (folderId != null && !string.Equals(m.Id, folderId, StringComparison.Ordinal)) { error = $"id \"{m.Id}\" differs from its folder \"{folderId}\""; return null; }
            m.Name = Str(root, "name");
            if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 60) { error = "name is missing or longer than 60 characters"; return null; }
            m.Name = m.Name.Trim();
            m.CosmeticType = Str(root, "cosmeticType");
            if (Array.IndexOf(CosmeticTypes, m.CosmeticType) < 0) { error = $"cosmeticType \"{m.CosmeticType}\" is not one of {string.Join(", ", CosmeticTypes)}"; return null; }
            m.Slot = Str(root, "slot") ?? "";
            if (m.Slot.Length > 0 && (m.Slot.Split('/').Length != 2 || m.Slot.Contains(".."))) { error = $"slot \"{m.Slot}\" is not <group>/<slot>"; return null; }
            if (root.TryGetProperty("price", out var price))
            {
                if (price.ValueKind != JsonValueKind.Number || !price.TryGetInt32(out m.Price) || m.Price < 0 || m.Price > MaxPrice)
                { error = "price is not a whole number from 0 to " + MaxPrice; return null; }
            }
            if (root.TryGetProperty("paintable", out var paint)) m.Paintable = paint.ValueKind == JsonValueKind.True;
            if (!root.TryGetProperty("fits", out var fits) || fits.ValueKind != JsonValueKind.Object) { error = "fits is missing"; return null; }
            foreach (var car in fits.EnumerateObject())
            {
                var fit = ReadFit(car.Value, out string why);
                if (fit == null) { error = $"fits.{car.Name}: {why}"; return null; }
                m.Fits[car.Name] = fit;
            }
            return m;
        }
        catch (JsonException e) { error = "part.json is not valid JSON: " + e.Message; return null; }
        catch (Exception e) { error = "part.json could not be read: " + e.Message; return null; }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static PartFit ReadFit(JsonElement e, out string why)
    {
        why = null;
        if (e.ValueKind != JsonValueKind.Object) { why = "not an object"; return null; }
        var fit = new PartFit();
        if (!Vec(e, "pos", fit.Pos, 20f, out why)) return null;
        if (e.TryGetProperty("rot", out _) && !Vec(e, "rot", fit.Rot, 360f, out why)) return null;
        if (e.TryGetProperty("scale", out var s))
        {
            if (s.ValueKind != JsonValueKind.Number || !s.TryGetSingle(out fit.Scale) || fit.Scale < 0.1f || fit.Scale > 10f || float.IsNaN(fit.Scale))
            { why = "scale is not a number from 0.1 to 10"; return null; }
        }
        return fit;
    }

    private static bool Vec(JsonElement e, string name, float[] into, float limit, out string why)
    {
        why = null;
        if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 3)
        { why = name + " is not three numbers"; return false; }
        int i = 0;
        foreach (var n in a.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetSingle(out float f) || float.IsNaN(f) || float.IsInfinity(f) || Math.Abs(f) > limit)
            { why = $"{name}[{i}] is not a number within +-{limit}"; return false; }
            into[i++] = f;
        }
        return true;
    }
}
