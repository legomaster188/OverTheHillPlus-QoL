using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.Cars;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// What a part's <c>light</c> adds to the glow of its lenses (lights.png already glows them through the car's
/// PropertyBlockSetter, like the game's own cosmetic lamps): lens flares on its lamps, a few real point lights from a
/// small shared pool, and twinkling bulbs. The lenses are found once per part shape from its mesh and lights.png
/// (<see cref="LampSpots"/>). Everything follows the car's own lamps: the light values the car's PropertyBlockSetter gives
/// the part's material (head lamps for G lenses, tail for R, reversing for B), so a part lights up exactly when its glow
/// does, on every player's car and in the garage. Driven from the feature's tick; any failure switches one part's extras
/// off and is logged, the part itself stays.
/// </summary>
internal static class PartLights
{
    private const string Mod = CustomCosmeticsPlugin.ModId;

    /// <summary>Most point lights the parts light at once (the nearest parts get them): the game's forward renderer
    /// only takes a few lights per object.</summary>
    public const int PoolBudget = 6;

    /// <summary>The flare lamps of one part: lenses this close (metres) are one lamp; at most this many flares.</summary>
    private const float FlareMerge = 0.12f;
    private const int MaxFlares = 8;

    private sealed class Lit
    {
        public GameObject Go;
        public MeshFilter Mf;
        /// <summary>The full-detail mesh it draws (the shape without its bulbs once a twinkling part is set up).</summary>
        public Mesh Main;
        public MeshPart Shape;
        public Transform Root;
        /// <summary>Noted, its extras not made yet (until it is first shown).</summary>
        public bool Pending;
        public Material Material;
        public PropertyBlockSetter Setter;
        public CustomPart Part;
        public readonly List<(Light light, LampChannel ch, float strength)> Pool = new();
        public readonly List<(LensFlareComponentSRP flare, LampChannel ch)> Flares = new();
        public readonly List<(GameObject go, MeshRenderer r, int set)> Sets = new();
        public MaterialPropertyBlock Block;
        public float Near = float.MaxValue;
        public bool PoolOn;
        public bool Broken;
    }

    private static readonly List<Lit> All = new();
    private static readonly Dictionary<string, List<LampSpot>> Spots = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Mesh[]> SetMeshes = new(StringComparer.Ordinal);
    private static readonly List<Vector3> Eyes = new();
    private static GameObject _flareTemplate;
    private static float _flareIntensity = 1f;
    private static float _nextPool;

    private static int[] _copied;
    private static int _livery = -1;

    /// <summary>The car's block values a twinkle set takes over (read once, when first needed: never in a static
    /// initializer, where a failure would take every part down).</summary>
    private static int[] Copied => _copied ??= new[]
    {
        PropertyBlockSetter.DustAmountPropertyID, PropertyBlockSetter.MudAmountPropertyID, PropertyBlockSetter.SnowAmountPropertyID,
        PropertyBlockSetter.SandAmountPropertyID, PropertyBlockSetter.WetAmountPropertyID, PropertyBlockSetter.RainAmountPropertyID,
        PropertyBlockSetter.DamageAmountPropertyID, PropertyBlockSetter.RearlightsIntensityPropertyID,
        PropertyBlockSetter.BrakingRearlightsIntensityPropertyID, PropertyBlockSetter.ReverselightsIntensityPropertyID,
        PropertyBlockSetter.FadeOutPropertyID,
    };

    /// <summary>Decoded textures of the last few parts (lights and albedo), so a part's shapes for several cars decode
    /// its PNGs once.</summary>
    private static readonly List<(string key, byte[] px, int w, int h)> Decoded = new();
    private const int DecodedKeep = 4;

    public static int Count => All.Count;

    // ---------------------------------------------------------------- the lenses of a shape

    /// <summary>The lenses of one of a part's shapes (cached by part and shape).</summary>
    private static List<LampSpot> SpotsOf(CustomPart p, MeshPart shape)
    {
        string key = p.ItemId.ToString("X8") + "|" + shape.Name;
        if (Spots.TryGetValue(key, out var have)) return have;
        var list = new List<LampSpot>();
        Spots[key] = list;
        if (p.Lights == null) return list;
        var lights = Decode(p.Id + "_lights_read", p.Lights, linear: true, out int lw, out int lh);
        if (lights == null) return list;
        int aw = 0, ah = 0;
        var albedo = p.Albedo != null ? Decode(p.Id + "_albedo_read", p.Albedo, linear: false, out aw, out ah) : null;
        LampSpots.Sampler L = (float u, float v, out float r, out float g, out float b) => Sample(lights, lw, lh, u, v, out r, out g, out b);
        LampSpots.Sampler A = albedo == null ? null : (float u, float v, out float r, out float g, out float b) => Sample(albedo, aw, ah, u, v, out r, out g, out b);
        list.AddRange(LampSpots.Find(shape.Positions, shape.Uv0, shape.Indices, L, A));
        // into the drawn mesh's space (the part's own transform, identity for the exported parts)
        var m = CarAssets.PartMatrix(shape);
        foreach (var s in list)
        {
            var at = m.MultiplyPoint3x4(new Vector3(s.X, s.Y, s.Z));
            var n = m.MultiplyVector(new Vector3(s.NX, s.NY, s.NZ));
            s.X = at.x; s.Y = at.y; s.Z = at.z;
            if (n.sqrMagnitude > 1e-8f) { n.Normalize(); s.NX = n.x; s.NY = n.y; s.NZ = n.z; }
        }
        DebugLog.Info(Mod, $"{p.Id} ({shape.Name}): {list.Count} lens(es) found");
        return list;
    }

    /// <summary>An embedded PNG's RGBA32 bytes, bottom row first; null when it can't be read.</summary>
    private static byte[] Decode(string name, byte[] png, bool linear, out int w, out int h)
    {
        foreach (var d in Decoded)
            if (d.key == name) { w = d.w; h = d.h; return d.px; }
        var px = DecodeNow(name, png, linear, out w, out h);
        if (px != null)
        {
            Decoded.Add((name, px, w, h));
            if (Decoded.Count > DecodedKeep) Decoded.RemoveAt(0);
        }
        return px;
    }

    private static byte[] DecodeNow(string name, byte[] png, bool linear, out int w, out int h)
    {
        w = h = 0;
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear) { name = name };
        try
        {
            if (!ImageConversion.LoadImage(t, new Il2CppStructArray<byte>(png), false)) return null;
            w = t.width;
            h = t.height;
            var px = t.GetPixels32();
            return System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
        }
        finally { Object.Destroy(t); }
    }

    private static void Sample(byte[] px, int w, int h, float u, float v, out float r, out float g, out float b)
    {
        int x = Mathf.Clamp((int)(u * w), 0, w - 1), y = Mathf.Clamp((int)(v * h), 0, h - 1);
        int i = (y * w + x) * 4;
        r = px[i] / 255f;
        g = px[i + 1] / 255f;
        b = px[i + 2] / 255f;
    }

    // ---------------------------------------------------------------- set-up (when a part object is made)

    /// <summary>The three twinkle sets' meshes and the shape without them ([0..2] sets, [3] the rest); null when the
    /// shape has no head-lamp lenses.</summary>
    private static Mesh[] SetsFor(CustomPart p, MeshPart shape)
    {
        string key = p.ItemId.ToString("X8") + "|" + shape.Name;
        if (SetMeshes.TryGetValue(key, out var have)) return have;
        SetMeshes[key] = null;
        var spots = SpotsOf(p, shape);
        var bySet = new List<int>[] { new(), new(), new() };
        var bulb = new HashSet<int>();
        foreach (var s in spots)
        {
            if (s.Channel != LampChannel.Head) continue;
            bySet[s.Set].AddRange(s.Triangles);
            foreach (int t in s.Triangles) bulb.Add(t);
        }
        if (bulb.Count == 0) return null;
        var rest = new List<int>();
        int nt = shape.Indices.Length / 3;
        for (int t = 0; t < nt; t++) if (!bulb.Contains(t)) rest.Add(t);
        var meshes = new Mesh[4];
        for (int k = 0; k < 3; k++) meshes[k] = Build($"cc_{p.Id}_{shape.Name}_twinkle{k}", shape, bySet[k]);
        meshes[3] = Build($"cc_{p.Id}_{shape.Name}_body", shape, rest);
        if (meshes[3] == null) return null;
        SetMeshes[key] = meshes;
        return meshes;
    }

    /// <summary>A mesh of some of a shape's triangles (its vertices compacted).</summary>
    private static Mesh Build(string name, MeshPart shape, List<int> tris)
    {
        if (tris.Count == 0) return null;
        var map = new Dictionary<int, int>();
        var idx = new int[tris.Count * 3];
        int k = 0;
        foreach (int t in tris)
            for (int c = 0; c < 3; c++)
            {
                int v = shape.Indices[t * 3 + c];
                if (!map.TryGetValue(v, out int nv)) map[v] = nv = map.Count;
                idx[k++] = nv;
            }
        var sub = new MeshPart
        {
            Name = shape.Name, Kind = shape.Kind, Position = shape.Position, Rotation = shape.Rotation, Scale = shape.Scale,
            Positions = new float[map.Count * 3], Normals = new float[map.Count * 3], Uv0 = new float[map.Count * 2], Indices = idx,
        };
        foreach (var kv in map)
        {
            Array.Copy(shape.Positions, kv.Key * 3, sub.Positions, kv.Value * 3, 3);
            Array.Copy(shape.Normals, kv.Key * 3, sub.Normals, kv.Value * 3, 3);
            Array.Copy(shape.Uv0, kv.Key * 2, sub.Uv0, kv.Value * 2, 2);
        }
        var mesh = CarAssets.BuildMesh(name, new List<MeshPart> { sub }, Matrix4x4.identity);
        if (mesh != null)
        {
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Object.DontDestroyOnLoad(mesh);
        }
        return mesh;
    }

    /// <summary>Notes a new part object for its extras (point lights, flares, twinkle sets): they are made the first time
    /// the part is shown (a car instance builds every part it could carry, most never mounted), one part per frame.
    /// <paramref name="full"/> is the full-detail mesh the object draws. No-op for a part without <c>light</c>.</summary>
    public static void Add(GameObject go, MeshFilter mf, CustomPart p, MeshPart shape, Mesh full, Material material, PropertyBlockSetter setter, Transform carRoot)
    {
        var cfg = p?.Manifest.Light;
        if (cfg == null || !cfg.Any || go == null || shape == null) return;
        All.Add(new Lit { Go = go, Mf = mf, Main = full, Shape = shape, Root = carRoot, Material = material, Setter = setter, Part = p, Pending = true });
    }

    /// <summary>Makes a noted part's extras (on the main thread, the first time it is shown).</summary>
    private static void Setup(Lit lit)
    {
        lit.Pending = false;
        var p = lit.Part;
        var cfg = p.Manifest.Light;
        var go = lit.Go;
        var shape = lit.Shape;
        var material = lit.Material;
        var carRoot = lit.Root;
        try
        {
            var spots = SpotsOf(p, shape);
            if (spots.Count == 0) { DebugLog.Info(Mod, $"{p.Id}: no glowing lens found; no extra lights"); lit.Broken = true; return; }
            int layer = go.layer;
            // point lights, off until the pool hands them out
            foreach (var s in LampSpots.PoolSpots(spots, cfg.Pool, cfg.PoolChannel))
            {
                var lo = new GameObject("CC_light");
                lo.layer = layer;
                lo.transform.SetParent(go.transform, false);
                lo.transform.localPosition = new Vector3(s.X, s.Y, s.Z) + new Vector3(s.NX, s.NY, s.NZ) * 0.03f;
                var l = lo.AddComponent<Light>();
                l.type = LightType.Point;
                l.range = cfg.Range;
                l.color = Tint(s);
                l.shadows = LightShadows.None;
                l.renderMode = LightRenderMode.Auto;
                l.intensity = 0f;
                l.enabled = false;
                lit.Pool.Add((l, s.Channel, cfg.Intensity));
            }
            // flares on the lamps, from the game's own roof-bar flare
            if (cfg.Flares && FlareTemplate(carRoot) != null)
                foreach (var s in LampSpots.Lamps(spots, FlareMerge, MaxFlares))
                {
                    var fo = Object.Instantiate(_flareTemplate);
                    fo.name = "CC_flare";
                    fo.layer = layer;
                    fo.transform.SetParent(go.transform, false);
                    var n = new Vector3(s.NX, s.NY, s.NZ);
                    fo.transform.localPosition = new Vector3(s.X, s.Y, s.Z) + n * 0.01f;
                    if (n.sqrMagnitude > 0.5f) fo.transform.localRotation = Quaternion.LookRotation(n);
                    fo.SetActive(true);
                    var f = fo.GetComponent<LensFlareComponentSRP>();
                    if (f == null) { Object.Destroy(fo); continue; }
                    f.intensity = 0f;
                    f.enabled = false;
                    lit.Flares.Add((f, s.Channel));
                }
            // twinkling bulbs: three objects of their own, their light values set here every frame
            if (cfg.Twinkle)
            {
                var sets = SetsFor(p, shape);
                if (sets != null && sets[3] != null && lit.Main != null)
                {
                    // the object draws the shape without its bulbs from now on (the LOD swap too)
                    var full = lit.Main;
                    PartLods.SwapFull(lit.Mf, full, sets[3]);
                    if (lit.Mf != null && lit.Mf.sharedMesh != null && lit.Mf.sharedMesh.Pointer == full.Pointer) lit.Mf.sharedMesh = sets[3];
                    lit.Main = sets[3];
                }
                if (sets != null && sets[3] != null)
                    for (int k = 0; k < 3; k++)
                    {
                        if (sets[k] == null) continue;
                        var so = new GameObject("CC_twinkle" + k);
                        so.layer = layer;
                        so.transform.SetParent(go.transform, false);
                        so.AddComponent<MeshFilter>().sharedMesh = sets[k];
                        var r = so.AddComponent<MeshRenderer>();
                        r.sharedMaterial = material;
                        r.shadowCastingMode = ShadowCastingMode.Off;
                        lit.Sets.Add((so, r, k));
                    }
                lit.Block = new MaterialPropertyBlock();
            }
            DebugLog.Info(Mod, $"{p.Id}: {lit.Pool.Count} pool light(s), {lit.Flares.Count} flare(s), {lit.Sets.Count} twinkle set(s) on {go.transform.root.name}");
        }
        catch (Exception e)
        {
            lit.Broken = true;
            DebugLog.Error(Mod, e, "PartLights.Setup " + p.Id);
        }
    }

    /// <summary>A light's colour from its lens's paint: the bulb's own tint, never darker than a bulb is.</summary>
    private static Color Tint(LampSpot s)
    {
        var c = new Color(s.R, s.G, s.B);
        float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        if (m < 0.05f) return new Color(1f, 0.92f, 0.8f);
        c /= m;   // full brightness, its hue
        return Color.Lerp(c, Color.white, 0.15f);
    }

    /// <summary>A stand-alone copy of the game's lamp flare (its roof bar's RooflightFlare or any lamp flare of this car),
    /// kept inactive; null when the car has none.</summary>
    private static GameObject FlareTemplate(Transform root)
    {
        if (_flareTemplate != null || root == null) return _flareTemplate;
        LensFlareComponentSRP best = null;
        foreach (var f in root.GetComponentsInChildren<LensFlareComponentSRP>(true))
        {
            if (f == null || f.lensFlareData == null) continue;
            if (f.intensity > _flareIntensity) _flareIntensity = f.intensity;
            if (best == null || f.name.IndexOf("Rooflight", StringComparison.OrdinalIgnoreCase) >= 0) best = f;
        }
        if (best == null) return null;
        var copy = Object.Instantiate(best.gameObject);
        copy.SetActive(false);
        copy.name = "CC_flare_template";
        copy.transform.SetParent(null, false);
        for (int i = copy.transform.childCount - 1; i >= 0; i--) Object.Destroy(copy.transform.GetChild(i).gameObject);
        foreach (var l in copy.GetComponents<Light>()) Object.Destroy(l);
        foreach (var b in copy.GetComponents<MonoBehaviour>())
            if (b != null && b.TryCast<LensFlareComponentSRP>() == null) Object.Destroy(b);
        copy.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(copy);
        _flareTemplate = copy;
        DebugLog.Info(Mod, $"lamp flare template: {best.name} (intensity {_flareIntensity:F2})");
        return _flareTemplate;
    }

    // ---------------------------------------------------------------- every frame

    public static void Tick()
    {
        if (All.Count == 0) return;
        float now = Time.unscaledTime;
        bool pool = now >= _nextPool;
        if (pool)
        {
            _nextPool = now + 0.25f;
            Eyes.Clear();
            foreach (var cam in Camera.allCameras)
                if (cam != null && cam.isActiveAndEnabled) Eyes.Add(cam.transform.position);
        }
        bool twinkle = CustomCosmeticsConfig.Twinkle == null || CustomCosmeticsConfig.Twinkle.Value;
        bool setUp = false;
        for (int i = All.Count - 1; i >= 0; i--)
        {
            var e = All[i];
            if (e.Go == null) { All.RemoveAt(i); continue; }
            if (e.Broken) continue;
            if (e.Pending)
            {
                // one part's extras per frame, the first time it is shown
                if (setUp || !e.Go.activeInHierarchy) continue;
                setUp = true;
                Setup(e);
                if (e.Broken) continue;
            }
            try { Update(e, now, pool, twinkle); }
            catch (Exception ex)
            {
                e.Broken = true;
                DebugLog.Error(Mod, ex, "PartLights.Tick " + e.Part?.Id);
            }
        }
        if (pool) HandOutPool();
    }

    /// <summary>The car's light values for this part's material (head, tail, reverse; 0 when the block has none).</summary>
    private static MaterialPropertyBlock BlockOf(Lit e)
    {
        var blocks = e.Setter != null ? e.Setter.materialPropertyBlocks : null;
        return blocks != null && e.Material != null && blocks.ContainsKey(e.Material) ? blocks[e.Material] : null;
    }

    private static float Value(MaterialPropertyBlock b, LampChannel ch)
    {
        if (b == null) return 0f;
        int id = ch switch
        {
            LampChannel.Head => PropertyBlockSetter.HeadlightsIntensityPropertyID,
            LampChannel.Tail => PropertyBlockSetter.RearlightsIntensityPropertyID,
            _ => PropertyBlockSetter.ReverselightsIntensityPropertyID,
        };
        return b.HasFloat(id) ? Mathf.Clamp01(b.GetFloat(id)) : 0f;
    }

    private static void Update(Lit e, float now, bool pool, bool twinkle)
    {
        bool shown = e.Go.activeInHierarchy;
        var block = shown ? BlockOf(e) : null;
        // flares with their lamps
        foreach (var (flare, ch) in e.Flares)
        {
            if (flare == null) continue;
            float v = Value(block, ch);
            bool on = v > 0.05f;
            if (flare.enabled != on) flare.enabled = on;
            if (on) flare.intensity = _flareIntensity * v;
        }
        // the twinkle sets: drawn at full detail only (the LOD mesh has its own bulbs), each at its turn's brightness
        if (e.Sets.Count > 0)
        {
            var drawn = e.Mf != null ? e.Mf.sharedMesh : null;
            bool full = drawn != null && e.Main != null && drawn.Pointer == e.Main.Pointer;
            foreach (var (go, r, set) in e.Sets)
            {
                if (go == null) continue;
                if (go.activeSelf != full) go.SetActive(full);
                if (!full || !shown || r == null) continue;
                var b = e.Block;
                b.Clear();
                if (block != null)
                {
                    foreach (int id in Copied)
                        if (block.HasFloat(id)) b.SetFloat(id, block.GetFloat(id));
                    if (_livery < 0) _livery = Shader.PropertyToID("_Livery_Texture");
                    if (block.HasTexture(_livery)) b.SetTexture(_livery, block.GetTexture(_livery));
                }
                float head = Value(block, LampChannel.Head);
                b.SetFloat(PropertyBlockSetter.HeadlightsIntensityPropertyID, head * (twinkle ? LampSpots.Twinkle(set, now) : 1f));
                r.SetPropertyBlock(b);
            }
        }
        if (!pool) return;
        // the pool's claim: how near the part is to a camera, while its lamps are on
        e.PoolOn = false;
        e.Near = float.MaxValue;
        if (!shown || e.Pool.Count == 0 || Eyes.Count == 0) return;
        foreach (var (light, ch, _) in e.Pool)
            if (light != null && Value(block, ch) > 0.05f) { e.PoolOn = true; break; }
        if (!e.PoolOn) return;
        var at = e.Go.transform.position;
        foreach (var eye in Eyes) e.Near = Mathf.Min(e.Near, (at - eye).sqrMagnitude);
    }

    private static readonly List<Lit> Wanting = new();

    /// <summary>The pool: the nearest parts with their lamps on get their lights, up to <see cref="PoolBudget"/>; the rest
    /// are switched off.</summary>
    private static void HandOutPool()
    {
        Wanting.Clear();
        foreach (var e in All) if (!e.Broken && e.Go != null && e.PoolOn) Wanting.Add(e);
        Wanting.Sort((a, b) => a.Near.CompareTo(b.Near));
        int left = PoolBudget;
        var on = new HashSet<Lit>();
        foreach (var e in Wanting)
        {
            if (left < e.Pool.Count) continue;
            left -= e.Pool.Count;
            on.Add(e);
        }
        foreach (var e in All)
        {
            if (e.Go == null || e.Broken) continue;
            bool mine = on.Contains(e);
            MaterialPropertyBlock block = mine ? BlockOf(e) : null;
            foreach (var (light, ch, strength) in e.Pool)
            {
                if (light == null) continue;
                float v = mine ? Value(block, ch) : 0f;
                bool lit = v > 0.05f;
                if (light.enabled != lit) light.enabled = lit;
                light.intensity = lit ? strength * v : 0f;
            }
        }
    }
}
