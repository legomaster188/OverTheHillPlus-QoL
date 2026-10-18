using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cars;

/// <summary>
/// A modded car's model and textures, embedded in its mod's DLL (resources "&lt;n&gt;/&lt;n&gt;.mesh",
/// "&lt;n&gt;/&lt;n&gt;_albedo_base.png", _ao, _lights, _mask, and the optional _zones and _icon, n =
/// <see cref="CarDefinition.AssetName"/>). The blob is read and checked at plugin load (pure code); the Unity meshes and
/// textures are made on the main thread when the template is built.
/// </summary>
internal sealed class CarAssets
{
    private readonly RuntimeCar _car;
    private readonly string _n;

    public MeshBlob Blob { get; private set; }

    private Texture2D _albedo, _ao, _lights, _mask, _icon;
    private bool _iconTried;

    public CarAssets(RuntimeCar car)
    {
        _car = car;
        _n = car.Def.AssetName;
    }

    /// <summary>The model's tyre radius (metres; 0 when unknown).</summary>
    public float TyreRadius => CarWheelFit.TyreRadius(Blob);

    /// <summary>Reads and checks the model. Null when it can be used, otherwise why not (logged by the caller).</summary>
    public string Load()
    {
        string mesh = _car.Def.MeshFile;
        byte[] bytes = Read(mesh);
        if (bytes == null) return $"its model ({mesh}) is not in the DLL";
        MeshBlob blob;
        try { blob = MeshBlob.Read(bytes); }
        catch (MeshBlobException e) { return "its model could not be read: " + e.Message; }
        if (blob.IsPlaceholder) return $"the model is not installed yet (this build has the placeholder {mesh})";
        string missing = blob.CheckDrivable();
        if (missing != null) return "its model is incomplete: " + missing;
        foreach (var png in new[] { _car.Def.AssetFile("albedo_base"), _car.Def.AssetFile("ao"), _car.Def.AssetFile("lights"), _car.Def.AssetFile("mask") })
            if (Read(png) == null) return $"its texture {png} is not in the DLL";
        Blob = blob;
        return null;
    }

    private byte[] Read(string name)
    {
        try
        {
            using var s = _car.AssetAssembly?.GetManifestResourceStream(_n + "/" + name);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarAssets.Read"); return null; }
    }

    private string ZonesFile => _car.Def.AssetFile("zones");

    /// <summary>The build has the paint zones (&lt;n&gt;_zones.png): paint colours can be changed. Without it the car keeps
    /// its base albedo (skins still work).</summary>
    public bool HasZones => _hasZones ??= Read(ZonesFile) != null;
    private bool? _hasZones;

    private byte[] _basePixels, _zonePixels;
    private int _paintW, _paintH;
    private string _paintProblem;

    /// <summary>
    /// The base albedo and the zones as RGBA32 bytes (decoded once, kept for the recolouring), or false with why not
    /// (no zones in this build, the two images differ in size, a decode failed). Main thread.
    /// </summary>
    public bool PaintPixels(out byte[] basePixels, out byte[] zonePixels, out int width, out int height, out string problem)
    {
        if (_basePixels == null && _paintProblem == null)
        {
            if (!HasZones) _paintProblem = $"this build has no paint zones ({ZonesFile})";
            else
            {
                try
                {
                    var b = DecodeRgba(_car.Def.AssetFile("albedo_base"), linear: false, out int bw, out int bh);
                    var z = DecodeRgba(ZonesFile, linear: true, out int zw, out int zh);
                    if (bw != zw || bh != zh) _paintProblem = $"the paint zones are {zw}x{zh} but the albedo is {bw}x{bh}";
                    else { _basePixels = b; _zonePixels = z; _paintW = bw; _paintH = bh; }
                }
                catch (Exception e) { DebugLog.Error("core", e, "CarAssets.PaintPixels"); _paintProblem = "the paint textures could not be read: " + e.Message; }
            }
        }
        basePixels = _basePixels;
        zonePixels = _zonePixels;
        width = _paintW;
        height = _paintH;
        problem = _paintProblem;
        return _basePixels != null;
    }

    /// <summary>An embedded PNG's pixels as RGBA32 bytes, bottom row first (Unity's order, as SetPixels32 takes them).</summary>
    private byte[] DecodeRgba(string name, bool linear, out int width, out int height)
    {
        var bytes = Read(name) ?? throw new InvalidOperationException($"{name} is missing");
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
        try
        {
            if (!ImageConversion.LoadImage(t, new Il2CppStructArray<byte>(bytes), false))
                throw new InvalidOperationException($"{name} is not a readable PNG");
            width = t.width;
            height = t.height;
            var px = t.GetPixels32();
            return System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
        }
        finally { Object.Destroy(t); }
    }

    // ---------------------------------------------------------------- Unity objects (main thread)

    public Texture2D Albedo => _albedo ??= Texture(_car.Def.AssetFile("albedo_base"), linear: false);
    public Texture2D Ao => _ao ??= Texture(_car.Def.AssetFile("ao"), linear: true);
    public Texture2D Lights => _lights ??= Texture(_car.Def.AssetFile("lights"), linear: true);
    public Texture2D Mask => _mask ??= Texture(_car.Def.AssetFile("mask"), linear: true);

    /// <summary>The garage icon (&lt;n&gt;_icon.png, optional: null when the build has none).</summary>
    public Texture2D Icon
    {
        get
        {
            if (_iconTried) return _icon;
            _iconTried = true;
            string f = _car.Def.AssetFile("icon");
            if (Read(f) != null) _icon = Texture(f, linear: false);
            return _icon;
        }
    }

    private Texture2D Texture(string name, bool linear)
    {
        var bytes = Read(name) ?? throw new InvalidOperationException($"{name} is missing");
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = _car.Def.CarName + "_" + Path.GetFileNameWithoutExtension(name) };
        // Non-readable: the pixels live on the GPU only (nothing reads them back), which halves the memory they take.
        if (!ImageConversion.LoadImage(t, new Il2CppStructArray<byte>(bytes), true))
            throw new InvalidOperationException($"{name} is not a readable PNG");
        t.wrapMode = TextureWrapMode.Clamp;
        t.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(t);
        return t;
    }

    // ---------------------------------------------------------------- mesh helpers (shared by every car)

    /// <summary>
    /// A Unity mesh of the given parts, with each vertex mapped through <paramref name="toLocal"/> (car root space ->
    /// the renderer's local space) after the part's own transform. Null when the parts have no triangles.
    /// </summary>
    public static Mesh BuildMesh(string name, IList<MeshPart> parts, Matrix4x4 toLocal)
    {
        int nv = 0, ni = 0;
        foreach (var p in parts) { nv += p.VertexCount; ni += p.Indices.Length; }
        if (nv == 0 || ni == 0) return null;
        var verts = new Vector3[nv];
        var norms = new Vector3[nv];
        var uvs = new Vector2[nv];
        var tris = new int[ni];
        int vo = 0, io = 0;
        foreach (var p in parts)
        {
            var m = toLocal * PartMatrix(p);
            for (int i = 0; i < p.VertexCount; i++)
            {
                verts[vo + i] = m.MultiplyPoint3x4(new Vector3(p.Positions[i * 3], p.Positions[i * 3 + 1], p.Positions[i * 3 + 2]));
                var n = m.MultiplyVector(new Vector3(p.Normals[i * 3], p.Normals[i * 3 + 1], p.Normals[i * 3 + 2]));
                norms[vo + i] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
                uvs[vo + i] = new Vector2(p.Uv0[i * 2], p.Uv0[i * 2 + 1]);
            }
            for (int i = 0; i < p.Indices.Length; i++) tris[io + i] = p.Indices[i] + vo;
            vo += p.VertexCount;
            io += p.Indices.Length;
        }
        var mesh = new Mesh { name = name };
        if (nv > 65000) mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = verts;
        mesh.normals = norms;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(mesh);
        return mesh;
    }

    /// <summary>The part's matrix as drawn (<see cref="MeshFrames.PartMatrix"/>: rims and tyres without their
    /// translation, since they are drawn in the hub frame).</summary>
    public static Matrix4x4 PartMatrix(MeshPart p)
    {
        var f = MeshFrames.PartMatrix(p);
        var m = new Matrix4x4();
        for (int c = 0; c < 4; c++)
            for (int r = 0; r < 4; r++)
                m[r, c] = f[c * 4 + r];
        return m;
    }

    public static Vector3 V(MeshVec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Bounds of the parts in car root space (after each part's transform).</summary>
    public static Bounds RootBounds(IEnumerable<MeshPart> parts)
    {
        bool any = false;
        var b = new Bounds();
        foreach (var p in parts)
        {
            var m = PartMatrix(p);
            for (int i = 0; i + 2 < p.Positions.Length; i += 3)
            {
                var v = m.MultiplyPoint3x4(new Vector3(p.Positions[i], p.Positions[i + 1], p.Positions[i + 2]));
                if (!any) { b = new Bounds(v, Vector3.zero); any = true; }
                else b.Encapsulate(v);
            }
        }
        return b;
    }
}
