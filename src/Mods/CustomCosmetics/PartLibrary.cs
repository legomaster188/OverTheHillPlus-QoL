using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OverTheHillPlus.Loader;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>One custom part read from the DLL's resources (the Unity objects are made later, on the main thread).</summary>
internal sealed class CustomPart
{
    public PartManifest Manifest;
    /// <summary>The mesh file's resource name ("&lt;folder&gt;/&lt;file&gt;", gzip when it ends in .gz); read from the DLL
    /// and parsed on first use.</summary>
    public string MeshName;
    /// <summary>The mesh file's bytes as embedded, read from the DLL when asked (nothing of it stays in memory).</summary>
    public byte[] MeshBytes => PartLibrary.ReadFile(MeshName);
    public uint ItemId;

    /// <summary>Parsed blobs by mesh file: parts that share one (the colours of a strip) parse it once.</summary>
    private static readonly Dictionary<string, MeshBlob> Parsed = new(StringComparer.Ordinal);

    /// <summary>The part's mesh blob, parsed the first time a car needs it (a part no car in the session uses never
    /// takes the memory).</summary>
    public MeshBlob Blob
    {
        get
        {
            if (Parsed.TryGetValue(MeshName, out var b)) return b;
            b = MeshBlob.Read(PartLibrary.Unpack(MeshName, MeshBytes));
            Parsed[MeshName] = b;
            return b;
        }
    }

    public MeshPart Shape => PartShape.Pick(Blob);

    // The textures' PNG bytes are read from the DLL when the part's textures are made (the callers keep what they make
    // from them), not at start: the 50 parts held some 185 MB of files for the whole session. A read is shared while
    // anything still holds it.
    public byte[] Albedo => Png("albedo.png");
    public byte[] Mask => Png("mask.png");
    public byte[] Lights => Png("lights.png");
    public byte[] Ao => Png("ao.png");
    public byte[] Icon => Png("icon.png");

    private readonly Dictionary<string, WeakReference<byte[]>> _read = new(StringComparer.Ordinal);

    private byte[] Png(string file)
    {
        if (_read.TryGetValue(file, out var w) && w.TryGetTarget(out var have)) return have;
        var bytes = PartLibrary.ReadFile(Manifest.Id + "/" + file);
        if (bytes != null) _read[file] = new WeakReference<byte[]>(bytes);
        return bytes;
    }

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
/// <para>The check of every part (its manifest, and its mesh unpacked and parsed) runs on a worker thread from the
/// plugin's Load (it took 14 s on the main thread at start); everything that asks for the parts takes the result,
/// waiting for the rest if it isn't done. The set of parts is the same either way. Textures and meshes are read from the
/// DLL only when a car uses the part.</para>
/// </summary>
internal static class PartLibrary
{
    private const string Prefix = "cosmetics/";
    private const int MaxParts = 120;

    private static readonly List<CustomPart> All = new();
    private static readonly Dictionary<uint, CustomPart> ById = new();
    private static readonly List<string> ProblemList = new();

    private static Assembly _asm;
    /// <summary>Normalised resource name -> real one (written once in <see cref="Load"/>, then only read, from any thread).</summary>
    private static Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private static BackgroundJob<List<PartScan.Row<CustomPart>>> _job;
    private static Func<uint, bool> _taken;
    private static ModLog _log;
    private static readonly object ReadErrorsLock = new();
    private static readonly List<string> ReadErrors = new();

    public static IReadOnlyList<CustomPart> Parts { get { EnsureLoaded(); return All; } }

    public static List<string> Problems { get { EnsureLoaded(); return ProblemList; } }

    /// <summary>Does any part take the look of a game part (<see cref="PartManifest.Replaces"/>)?</summary>
    public static bool HasLooks
    {
        get
        {
            foreach (var p in Parts)
                if (p.Manifest.Replaces.Length > 0) return true;
            return false;
        }
    }

    public static CustomPart ByItemId(uint id) { EnsureLoaded(); return ById.TryGetValue(id, out var p) ? p : null; }

    public static bool IsOurs(uint id) { EnsureLoaded(); return ById.ContainsKey(id); }

    /// <summary>Starts reading the DLL's parts (in the background). <paramref name="taken"/> says whether an item id is
    /// used by the game already (asked on the main thread, when the result is taken).</summary>
    public static void Load(Assembly asm, Func<uint, bool> taken, ModLog log)
    {
        All.Clear();
        ById.Clear();
        ProblemList.Clear();
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
        _asm = asm;
        _names = names;
        _taken = taken;
        _log = log;
        var list = new List<string>(folders);
        _job = new BackgroundJob<List<PartScan.Row<CustomPart>>>(() =>
            PartScan.ReadAll<CustomPart>(list, CosmeticIds.ValidPartId, MaxParts, id =>
            {
                var part = ReadPart(id, out string why);
                return (part, why);
            }));
        log.LogInfo($"Custom Cosmetics: checking {list.Count} part folder(s) in the background.");
    }

    /// <summary>Takes the background check's result (waiting for it if needed) and lists the parts. Main thread.</summary>
    private static void EnsureLoaded()
    {
        var job = _job;
        if (job == null) return;
        _job = null;
        var log = _log;
        List<PartScan.Row<CustomPart>> rows;
        double waited;
        try { rows = job.Result(out waited); }
        catch (Exception e)
        {
            DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "PartLibrary.EnsureLoaded");
            ProblemList.Add("the parts could not be read: " + e.Message);
            log?.LogError("Custom Cosmetics: the parts could not be read: " + e);
            return;
        }
        lock (ReadErrorsLock)
        {
            foreach (var r in ReadErrors) DebugLog.Error(CustomCosmeticsPlugin.ModId, r);
            ReadErrors.Clear();
        }
        var candidates = new List<CustomPart>();
        foreach (var r in rows)
        {
            if (r.Item == null) Fail(log, r.Id, r.Why);
            else candidates.Add(r.Item);
        }
        var problems = new List<string>();
        var ids = CosmeticIds.Assign(candidates.ConvertAll(c => c.Id), _taken, problems);
        foreach (var p in problems) { ProblemList.Add(p); log?.LogWarning("Custom Cosmetics: " + p); }
        foreach (var c in candidates)
        {
            if (!ids.TryGetValue(c.Id, out uint item)) continue;
            c.ItemId = item;
            All.Add(c);
            ById[item] = c;
        }
        foreach (var c in All)
            if (c.Manifest.Fits.Count == 0) log?.LogInfo($"Custom Cosmetics: part {c.Id} lists no car in its fits yet; it is not offered on any car.");
        log?.LogInfo($"Custom Cosmetics: {All.Count} part(s) loaded" + (All.Count > 0 ? ": " + string.Join(", ", All.ConvertAll(p => $"{p.Id} (0x{p.ItemId:X8})")) : "") +
                     (ProblemList.Count > 0 ? $"; {ProblemList.Count} left out (see the log)" : "") +
                     $" (checked in the background in {job.RanMs / 1000.0:0.0} s; the game waited {waited:0} ms for it).");
    }

    private static void Fail(ModLog log, string id, string why)
    {
        string line = $"{id}: {why}";
        ProblemList.Add(line);
        log?.LogWarning("Custom Cosmetics: part " + line);
    }

    /// <summary>A file of the parts ("&lt;folder&gt;/&lt;file&gt;") as embedded, null when there is none. Any thread.</summary>
    internal static byte[] ReadFile(string folderAndFile)
    {
        if (folderAndFile == null || !_names.TryGetValue(Prefix + folderAndFile, out var real)) return null;
        try
        {
            using var s = _asm?.GetManifestResourceStream(real);
            if (s == null) return null;
            using var ms = new MemoryStream(s.CanSeek ? (int)s.Length : 0);
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception e)
        {
            lock (ReadErrorsLock) if (ReadErrors.Count < 64) ReadErrors.Add($"reading {folderAndFile}: {e}");
            return null;
        }
    }

    private static bool Has(string folderAndFile) => _names.ContainsKey(Prefix + folderAndFile);

    /// <summary>A mesh file's bytes as the blob reader takes them: a .gz one unzipped.</summary>
    internal static byte[] Unpack(string name, byte[] bytes)
    {
        if (!name.EndsWith(".gz", StringComparison.Ordinal)) return bytes;
        using var src = new MemoryStream(bytes);
        using var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Checks one part folder. Runs on the worker thread: pure code only (no game, Unity or log calls).</summary>
    private static CustomPart ReadPart(string id, out string why)
    {
        why = null;
        var json = ReadFile(id + "/part.json");
        if (json == null) { why = "part.json is missing"; return null; }
        var manifest = PartManifest.Parse(System.Text.Encoding.UTF8.GetString(json), id, out string error);
        if (manifest == null) { why = error; return null; }
        // the mesh: its own, or the one of the part it shares it with (meshFrom: the colours of one strip), gzipped or not
        string owner = manifest.MeshFrom.Length > 0 ? manifest.MeshFrom : id;
        string meshName = owner + "/" + owner + ".mesh.gz";
        var meshBytes = ReadFile(meshName);
        if (meshBytes == null) { meshName = owner + "/" + owner + ".mesh"; meshBytes = ReadFile(meshName); }
        if (meshBytes == null) { why = $"{owner}.mesh is missing"; return null; }
        MeshBlob blob;
        try { blob = MeshBlob.Read(Unpack(meshName, meshBytes)); }
        catch (MeshBlobException e) { why = "its mesh could not be read: " + e.Message; return null; }
        catch (System.IO.InvalidDataException e) { why = "its mesh could not be unpacked: " + e.Message; return null; }
        if (blob.IsPlaceholder) { why = "its mesh is a stand-in file"; return null; }
        var shape = PartShape.Pick(blob);
        if (shape == null) { why = "its mesh has no part named \"part\" with triangles"; return null; }
        // checked; read and parsed again when a car needs it
        if (!Has(id + "/albedo.png")) { why = "albedo.png is missing"; return null; }
        if (!Has(id + "/mask.png")) { why = "mask.png is missing"; return null; }
        return new CustomPart { Manifest = manifest, MeshName = meshName };
    }
}
