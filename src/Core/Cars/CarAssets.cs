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
        // read ahead by core while the mods load (AssetPrefetch); here if it wasn't, or it failed and is to say why
        MeshBlob blob = AssetPrefetch.TakeModel(_car.AssetAssembly, _n + "/" + mesh);
        if (blob == null)
        {
            byte[] bytes = Read(mesh);
            if (bytes == null) return $"its model ({mesh}) is not in the DLL";
            try { blob = MeshBlob.Read(bytes); }
            catch (MeshBlobException e) { return "its model could not be read: " + e.Message; }
        }
        if (blob.IsPlaceholder) return $"the model is not installed yet (this build has the placeholder {mesh})";
        string missing = blob.CheckDrivable();
        if (missing != null) return "its model is incomplete: " + missing;
        foreach (var png in new[] { _car.Def.AssetFile("albedo_base"), _car.Def.AssetFile("ao"), _car.Def.AssetFile("lights"), _car.Def.AssetFile("mask") })
            if (!Has(png)) return $"its texture {png} is not in the DLL";
        Blob = blob;
        Prefetch();
        return null;
    }

    // The textures the car's build needs, being decoded on core's background thread.
    private readonly Dictionary<string, TextureJob> _ahead = new(StringComparer.Ordinal);

    private void Prefetch()
    {
        if (!Logging.DebugConfig.Get(Logging.DebugConfig.TexturesAhead, true)) return;
        foreach (var kind in new[] { "albedo_base", "ao", "lights", "mask" })
        {
            string file = _car.Def.AssetFile(kind);
            _ahead[file] = AssetPrefetch.Add(_car.AssetAssembly, _n + "/" + file, _car.Def.ItemId);
        }
    }

    /// <summary>The texture of this kind is ready to be made without waiting for the decoder. Asking moves the car's
    /// textures to the front of the queue. Main thread.</summary>
    public bool TextureReady(string kind)
    {
        if (!_ahead.TryGetValue(_car.Def.AssetFile(kind), out var job) || job.Done) return true;
        foreach (var j in _ahead.Values) if (!j.Done) AssetPrefetch.Want(j);
        return false;
    }

    /// <summary>An embedded asset's bytes ("&lt;n&gt;/&lt;name&gt;"), or null when the DLL has none. Any thread.</summary>
    public byte[] ReadAsset(string name) => Read(name);

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

    /// <summary>The DLL has the embedded asset (by name: nothing is read; at start every car read each of its 2048 px
    /// PNGs in full just to see that it was there). Any thread.</summary>
    private bool Has(string name)
    {
        try { return _car.AssetAssembly?.GetManifestResourceInfo(_n + "/" + name) != null; }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarAssets.Has"); return false; }
    }

    private string ZonesFile => _car.Def.AssetFile("zones");

    /// <summary>The build has the paint zones (&lt;n&gt;_zones.png): paint colours can be changed. Without it the car keeps
    /// its base albedo (skins still work).</summary>
    public bool HasZones => _hasZones ??= Has(ZonesFile);
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

    public Texture2D Albedo => _albedo ??= Texture(_car.Def.AssetFile("albedo_base"), linear: false, full: true);
    public Texture2D Ao => _ao ??= Texture(_car.Def.AssetFile("ao"), linear: true);
    public Texture2D Lights => _lights ??= Texture(_car.Def.AssetFile("lights"), linear: true, full: true);
    public Texture2D Mask => _mask ??= Texture(_car.Def.AssetFile("mask"), linear: true);

    /// <summary>Anisotropy of the car textures: the lamps, grille bars and trim are seen at grazing angles.</summary>
    public const int Aniso = 8;

    /// <summary>
    /// How every car texture is sampled: trilinear (no hard mip steps across a panel) with <see cref="Aniso"/>.
    /// <paramref name="full"/>: the albedo and its looks, and the lights: kept at full size whatever the game's texture
    /// quality (QualitySettings.globalTextureMipmapLimit), because at half size the lamp lenses and small trim of the
    /// 2048 atlas are a few texels across. AO and the mask follow the game's setting.
    /// </summary>
    public static void Sampling(Texture2D t, bool full)
    {
        t.filterMode = FilterMode.Trilinear;
        t.anisoLevel = Aniso;
        t.wrapMode = TextureWrapMode.Clamp;
        // Unity refuses to toggle it on a texture whose pixels already live on the GPU only (a UnityException that
        // stopped the car from being built): the loaders set it while the texture is still readable.
        if (full && !t.ignoreMipmapLimit && t.isReadable) t.ignoreMipmapLimit = true;
    }

    /// <summary>The garage icon (&lt;n&gt;_icon.png, optional: null when the build has none).</summary>
    public Texture2D Icon
    {
        get
        {
            if (_iconTried) return _icon;
            _iconTried = true;
            string f = _car.Def.AssetFile("icon");
            if (Has(f)) _icon = Texture(f, linear: false);
            return _icon;
        }
    }

    /// <summary>
    /// The car's picture in the garage and the car lists (&lt;n&gt;_picture.png: a shaded side view in the default paint,
    /// 512 × 512, from Liveries/tools/vera/render_picture.py; optional: null when the build has none, and the line
    /// drawing <see cref="Icon"/> is used instead).
    /// </summary>
    public Texture2D Picture
    {
        get
        {
            if (_pictureTried) return _picture;
            _pictureTried = true;
            string f = _car.Def.AssetFile(CarDefinition.PictureSuffix);
            if (Has(f)) _picture = Texture(f, linear: false);
            return _picture;
        }
    }

    private Texture2D _picture;
    private bool _pictureTried;

    private Texture2D Texture(string name, bool linear, bool full = false)
    {
        var t = Decoded(name, linear, full);
        if (t != null) return t;
        var bytes = Read(name) ?? throw new InvalidOperationException($"{name} is missing");
        t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = _car.Def.CarName + "_" + Path.GetFileNameWithoutExtension(name) };
        // Loaded readable first: LoadImage may reset ignoreMipmapLimit, and it can only be set again while the texture is
        // readable. Then Apply makes it non-readable: the pixels live on the GPU only (nothing reads them back), which
        // halves the memory they take.
        if (!ImageConversion.LoadImage(t, new Il2CppStructArray<byte>(bytes), false))
            throw new InvalidOperationException($"{name} is not a readable PNG");
        if (full && !t.ignoreMipmapLimit) t.ignoreMipmapLimit = true;
        Sampling(t, full);
        t.Apply(false, true);
        t.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(t);
        return t;
    }

    /// <summary>
    /// The texture from the pixels decoded ahead: the same object <see cref="Texture"/> makes with the game's decoder, in the
    /// same order of steps, without decoding on the main thread. Null when it wasn't decoded ahead.
    /// </summary>
    private Texture2D Decoded(string name, bool linear, bool full)
    {
        if (!_ahead.TryGetValue(name, out var job)) return null;
        _ahead.Remove(name);
        if (!job.Done) AssetPrefetch.Want(job);
        while (!job.Done) System.Threading.Thread.Sleep(1);
        var px = job.Pixels;
        job.Pixels = null;
        AssetPrefetch.Taken(job, px);
        if (px == null) return null;
        var t = new Texture2D(job.Width, job.Height, TextureFormat.RGBA32, true, linear) { name = _car.Def.CarName + "_" + Path.GetFileNameWithoutExtension(name) };
        if (full && !t.ignoreMipmapLimit) t.ignoreMipmapLimit = true;
        Upload(t, px);
        Sampling(t, full);
        t.Apply(false, true);
        t.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(t);
        return t;
    }

    /// <summary>Hands raw pixels to a texture straight from managed memory: the byte[] overload would copy them into a new
    /// array on the game's heap first (21 MB for a car's texture, for its collector to sweep).</summary>
    internal static unsafe void Upload(Texture2D t, byte[] pixels)
    {
        fixed (byte* p = pixels) t.LoadRawTextureData((IntPtr)p, pixels.Length);
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
        // Plain floats straight into the arrays: Unity's vector and matrix calls cost a trip into the game per vertex here.
        var vf = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float>(verts.AsSpan());
        var nf = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float>(norms.AsSpan());
        var uf = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector2, float>(uvs.AsSpan());
        foreach (var p in parts)
        {
            MeshMath.Transform(Rows(toLocal * PartMatrix(p)), p.Positions, p.Normals, p.VertexCount, vf.Slice(vo * 3), nf.Slice(vo * 3));
            p.Uv0.AsSpan(0, p.VertexCount * 2).CopyTo(uf.Slice(vo * 2));
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

    /// <summary>The top three rows of a matrix, for <see cref="MeshMath"/>.</summary>
    internal static float[] Rows(Matrix4x4 m) => new[] { m.m00, m.m01, m.m02, m.m03, m.m10, m.m11, m.m12, m.m13, m.m20, m.m21, m.m22, m.m23 };

    public static Vector3 V(MeshVec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Bounds of the parts in car root space (after each part's transform).</summary>
    public static Bounds RootBounds(IEnumerable<MeshPart> parts)
    {
        bool any = false;
        var box = new float[6];
        foreach (var p in parts) any = MeshMath.Encapsulate(Rows(PartMatrix(p)), p.Positions, box, any);
        var b = new Bounds();
        if (any) b.SetMinMax(new Vector3(box[0], box[1], box[2]), new Vector3(box[3], box[4], box[5]));
        return b;
    }
}
