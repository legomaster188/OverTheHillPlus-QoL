using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.Cars;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Trailers;

/// <summary>One trailer's model, read and checked at plugin load, with its Unity meshes (made on the main thread).</summary>
internal sealed class TrailerModel
{
    public TrailerSpec Spec;
    public MeshBlob Blob;
    public Mesh Body, Wheel;
    public readonly List<Mesh> Colliders = new();
    /// <summary>Wheel anchors in axle order (wheel_L1, wheel_R1, ...).</summary>
    public List<MeshAnchor> Wheels;
    public float WheelRadius;

    public Vector3 Anchor(string name, Vector3 fallback)
    {
        var a = Blob.Anchor(name);
        return a == null ? fallback : TrailerAssets.V(a.Position);
    }
}

/// <summary>
/// The four models and their shared texture, embedded in this DLL (src/Mods/Trailers/Assets, made by
/// Liveries/tools/trailers/build_trailers.py in the Vera's mesh format).
/// </summary>
internal static class TrailerAssets
{
    private const string TextureFile = "trailers_albedo.png";

    private static readonly Dictionary<TrailerKind, TrailerModel> Models = new();
    private static Texture2D _albedo, _white;

    /// <summary>Reads and checks every model. Null when all can be used, otherwise why not.</summary>
    public static string Load()
    {
        foreach (var spec in TrailerSpecs.All)
        {
            byte[] bytes = Read(spec.MeshFile);
            if (bytes == null) return $"its model {spec.MeshFile} is not in the DLL";
            MeshBlob blob;
            try { blob = MeshBlob.Read(bytes); }
            catch (MeshBlobException e) { return $"its model {spec.MeshFile} could not be read: {e.Message}"; }
            string why = TrailerSpecs.CheckModel(blob);
            if (why != null) return $"its model {spec.MeshFile} is incomplete: {why}";
            var m = new TrailerModel { Spec = spec, Blob = blob };
            m.Wheels = new List<MeshAnchor>();
            foreach (var n in TrailerSpecs.WheelAnchors(n => blob.Anchor(n) != null)) m.Wheels.Add(blob.Anchor(n));
            blob.First(MeshPartKind.Tyre).Bounds(out var lo, out var hi);
            m.WheelRadius = Math.Max(hi.Y - lo.Y, hi.Z - lo.Z) * 0.5f;
            Models[spec.Kind] = m;
        }
        if (Read(TextureFile) == null) return $"its texture {TextureFile} is not in the DLL";
        DebugLog.Info(TrailersPlugin.ModId, $"{Models.Count} trailer models read and checked from the DLL");
        return null;
    }

    public static TrailerModel Model(TrailerKind kind) => Models.TryGetValue(kind, out var m) ? m : null;

    private static byte[] Read(string name)
    {
        try
        {
            using var s = typeof(TrailerAssets).Assembly.GetManifestResourceStream("trailers/" + name);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerAssets.Read"); return null; }
    }

    // ---------------------------------------------------------------- Unity objects (main thread)

    /// <summary>Makes the model's meshes (once).</summary>
    public static void EnsureMeshes(TrailerModel m)
    {
        if (m.Body != null) return;
        var name = "Trailer_" + m.Spec.Kind;
        m.Body = BuildMesh(name + "_Body", m.Blob.PartsOf(MeshPartKind.Body), false);
        var wheelParts = m.Blob.PartsOf(MeshPartKind.Wheel);
        wheelParts.AddRange(m.Blob.PartsOf(MeshPartKind.Tyre));
        m.Wheel = BuildMesh(name + "_Wheel", wheelParts, true);
        foreach (var c in m.Blob.PartsOf(MeshPartKind.Collider))
            m.Colliders.Add(BuildMesh(name + "_" + c.Name, new List<MeshPart> { c }, false));
    }

    public static Texture2D Albedo => _albedo ??= Texture(TextureFile);

    /// <summary>A plain white texture (no baked occlusion: the prop shader's AO slot).</summary>
    public static Texture2D White
    {
        get
        {
            if (_white != null) return _white;
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "Trailer_White" };
            var px = new Il2CppStructArray<Color32>(16);
            for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
            t.SetPixels32(px);
            t.Apply(false, true);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Object.DontDestroyOnLoad(t);
            return _white = t;
        }
    }

    private static Texture2D Texture(string name)
    {
        var bytes = Read(name) ?? throw new InvalidOperationException($"{name} is missing");
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { name = "Trailer_" + Path.GetFileNameWithoutExtension(name) };
        if (!ImageConversion.LoadImage(t, new Il2CppStructArray<byte>(bytes), true))
            throw new InvalidOperationException($"{name} is not a readable PNG");
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Bilinear;
        t.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(t);
        return t;
    }

    /// <summary>A Unity mesh of the parts. Body and collider parts go through their transform (trailer space); rims
    /// and tyres stay in their hub frame (<paramref name="hubFrame"/>: rotation and scale only).</summary>
    private static Mesh BuildMesh(string name, List<MeshPart> parts, bool hubFrame)
    {
        int nv = 0, ni = 0;
        foreach (var p in parts) { nv += p.VertexCount; ni += p.Indices.Length; }
        var verts = new Il2CppStructArray<Vector3>(nv);
        var norms = new Il2CppStructArray<Vector3>(nv);
        var uvs = new Il2CppStructArray<Vector2>(nv);
        var tris = new Il2CppStructArray<int>(ni);
        int vo = 0, io = 0;
        foreach (var p in parts)
        {
            var m = Matrix4x4.TRS(hubFrame ? Vector3.zero : V(p.Position),
                                  new Quaternion(p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W), V(p.Scale));
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
        if (nv > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = verts;
        mesh.normals = norms;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(mesh);
        return mesh;
    }

    public static Vector3 V(MeshVec3 v) => new(v.X, v.Y, v.Z);
    public static Vector3 V3(V3 v) => new(v.X, v.Y, v.Z);
    public static V3 ToV3(Vector3 v) => new(v.x, v.y, v.z);
}
