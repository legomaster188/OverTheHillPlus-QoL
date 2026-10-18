using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>One custom part read from the DLL's resources (the Unity objects are made later, on the main thread).</summary>
internal sealed class CustomPart
{
    public PartManifest Manifest;
    public MeshBlob Blob;
    public MeshPart Shape;
    public uint ItemId;
    public byte[] Albedo, Mask, Lights, Ao, Icon;

    public string Id => Manifest.Id;
    public string NameKey => "OTHP_CC_" + Manifest.Id.ToUpperInvariant() + "_NAME";
    public string DescriptionKey => "OTHP_CC_" + Manifest.Id.ToUpperInvariant() + "_DESCRIPTION";
}

/// <summary>
/// Every custom part the DLL carries: one folder per part under the resources' <c>cosmetics/&lt;part id&gt;/</c> (made from
/// Liveries/cosmetics/&lt;part id&gt;/ by the csproj; the contract is FORMAT.md there): <c>part.json</c>,
/// <c>&lt;part id&gt;.mesh</c>, <c>albedo.png</c>, <c>mask.png</c> and the optional <c>lights.png</c>, <c>ao.png</c>,
/// <c>icon.png</c>. A part with any problem is left out and the problem is logged and listed on the MODS page; the rest
/// load. Dropping a new folder in is all a new part needs.
/// </summary>
internal static class PartLibrary
{
    private const string Prefix = "cosmetics/";
    private const int MaxParts = 120;

    private static readonly List<CustomPart> All = new();
    private static readonly Dictionary<uint, CustomPart> ById = new();
    public static readonly List<string> Problems = new();

    public static IReadOnlyList<CustomPart> Parts => All;

    public static CustomPart ByItemId(uint id) => ById.TryGetValue(id, out var p) ? p : null;

    public static bool IsOurs(uint id) => ById.ContainsKey(id);

    /// <summary>Reads the DLL's parts. <paramref name="taken"/> says whether an item id is used by the game already.</summary>
    public static void Load(Assembly asm, Func<uint, bool> taken, ManualLogSource log)
    {
        All.Clear();
        ById.Clear();
        Problems.Clear();
        var names = new Dictionary<string, string>(StringComparer.Ordinal); // normalised -> real resource name
        foreach (var n in asm.GetManifestResourceNames())
        {
            string norm = n.Replace('\\', '/');
            if (norm.StartsWith(Prefix, StringComparison.Ordinal)) names[norm] = n;
        }
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var norm in names.Keys)
        {
            int slash = norm.IndexOf('/', Prefix.Length);
            if (slash > Prefix.Length) folders.Add(norm.Substring(Prefix.Length, slash - Prefix.Length));
        }

        var candidates = new List<CustomPart>();
        foreach (var id in folders)
        {
            if (!CosmeticIds.ValidPartId(id)) { Fail(log, id, "its folder name is not a valid part id (lowercase letters, digits, underscores)"); continue; }
            if (candidates.Count >= MaxParts) { Fail(log, id, $"more than {MaxParts} parts"); continue; }
            var part = ReadPart(asm, names, id, out string why);
            if (part == null) { Fail(log, id, why); continue; }
            candidates.Add(part);
        }

        var problems = new List<string>();
        var ids = CosmeticIds.Assign(candidates.ConvertAll(c => c.Id), taken, problems);
        foreach (var p in problems) { Problems.Add(p); log.LogWarning("Custom Cosmetics: " + p); }
        foreach (var c in candidates)
        {
            if (!ids.TryGetValue(c.Id, out uint item)) continue;
            c.ItemId = item;
            All.Add(c);
            ById[item] = c;
        }
        foreach (var c in All)
            if (c.Manifest.Fits.Count == 0) log.LogInfo($"Custom Cosmetics: part {c.Id} lists no car in its fits yet; it is not offered on any car.");
        log.LogInfo($"Custom Cosmetics: {All.Count} part(s) loaded" + (All.Count > 0 ? ": " + string.Join(", ", All.ConvertAll(p => $"{p.Id} (0x{p.ItemId:X8})")) : "") +
                    (Problems.Count > 0 ? $"; {Problems.Count} left out (see the log)" : "") + ".");
    }

    private static void Fail(ManualLogSource log, string id, string why)
    {
        string line = $"{id}: {why}";
        Problems.Add(line);
        log.LogWarning("Custom Cosmetics: part " + line);
    }

    private static byte[] Read(Assembly asm, Dictionary<string, string> names, string id, string file)
    {
        if (!names.TryGetValue(Prefix + id + "/" + file, out var real)) return null;
        try
        {
            using var s = asm.GetManifestResourceStream(real);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "PartLibrary.Read"); return null; }
    }

    private static CustomPart ReadPart(Assembly asm, Dictionary<string, string> names, string id, out string why)
    {
        why = null;
        var json = Read(asm, names, id, "part.json");
        if (json == null) { why = "part.json is missing"; return null; }
        var manifest = PartManifest.Parse(System.Text.Encoding.UTF8.GetString(json), id, out string error);
        if (manifest == null) { why = error; return null; }
        var meshBytes = Read(asm, names, id, id + ".mesh");
        if (meshBytes == null) { why = $"{id}.mesh is missing"; return null; }
        MeshBlob blob;
        try { blob = MeshBlob.Read(meshBytes); }
        catch (MeshBlobException e) { why = "its mesh could not be read: " + e.Message; return null; }
        if (blob.IsPlaceholder) { why = "its mesh is a stand-in file"; return null; }
        var shape = PartShape.Pick(blob);
        if (shape == null) { why = "its mesh has no part named \"part\" with triangles"; return null; }
        var albedo = Read(asm, names, id, "albedo.png");
        var mask = Read(asm, names, id, "mask.png");
        if (albedo == null) { why = "albedo.png is missing"; return null; }
        if (mask == null) { why = "mask.png is missing"; return null; }
        return new CustomPart
        {
            Manifest = manifest, Blob = blob, Shape = shape, Albedo = albedo, Mask = mask,
            Lights = Read(asm, names, id, "lights.png"), Ao = Read(asm, names, id, "ao.png"), Icon = Read(asm, names, id, "icon.png"),
        };
    }
}
