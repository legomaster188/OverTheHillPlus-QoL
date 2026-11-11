using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// What a part's <c>light</c> adds to the glow of its lenses (lights.png already glows them through the car's
/// PropertyBlockSetter, like the game's own cosmetic lamps): lens flares on its lamps, a few real point lights from a
/// small shared pool, and twinkling bulbs. The lenses are found once per part shape from its mesh and lights.png
/// (<see cref="LampSpots"/>), off the game thread while the part is built (<see cref="PartBuilds"/>), which hands them
/// over with the bulb sets' meshes (<see cref="Provide"/>) before the part object is noted here. Everything follows the car's own lamps: the light values the car's PropertyBlockSetter gives
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
        /// <summary>The shape it draws ("part@Hilo"): with the part, the key of its lenses and bulb sets.</summary>
        public string ShapeName;
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

    public static int Count => All.Count;

    // ---------------------------------------------------------------- the lenses of a shape

    private static string Key(CustomPart p, string shapeName) => p.ItemId.ToString("X8") + "|" + shapeName;

    /// <summary>Have this part's lenses on this shape been handed over already?</summary>
    public static bool Has(CustomPart p, string shapeName) => Spots.ContainsKey(Key(p, shapeName));

    /// <summary>
    /// The lenses of one of a part's shapes, in the drawn mesh's space, and (for a twinkling part) the meshes of its three
    /// bulb sets and of the shape without them ([0..2] sets, null where a set is empty; [3] the rest); <paramref name="sets"/>
    /// is null when it has no head-lamp bulbs. Found off the game thread; kept for every car that draws this shape.
    /// </summary>
    public static void Provide(CustomPart p, string shapeName, List<LampSpot> spots, Mesh[] sets)
    {
        string key = Key(p, shapeName);
        Spots[key] = spots ?? new List<LampSpot>();
        SetMeshes[key] = sets;
    }

    /// <summary>Notes a part object for its extras (point lights, flares, twinkle sets): they are made the next time it
    /// is seen shown, one part per frame. <paramref name="full"/> is the full-detail mesh the object draws,
    /// <paramref name="shapeName"/> the shape it is (null for a part that has no extras, such as a wheel). No-op for a
    /// part without <c>light</c>.</summary>
    public static void Add(GameObject go, MeshFilter mf, CustomPart p, string shapeName, Mesh full, Material material, PropertyBlockSetter setter, Transform carRoot)
    {
        var cfg = p?.Manifest.Light;
        if (cfg == null || !cfg.Any || go == null || shapeName == null) return;
        All.Add(new Lit { Go = go, Mf = mf, Main = full, ShapeName = shapeName, Root = carRoot, Material = material, Setter = setter, Part = p, Pending = true });
    }

    /// <summary>Makes a noted part's extras (on the main thread, the first time it is shown).</summary>
    private static void Setup(Lit lit)
    {
        lit.Pending = false;
        var p = lit.Part;
        var cfg = p.Manifest.Light;
        var go = lit.Go;
        string key = Key(p, lit.ShapeName);
        var material = lit.Material;
        var carRoot = lit.Root;
        try
        {
            if (!Spots.TryGetValue(key, out var spots)) spots = new List<LampSpot>();
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
                SetMeshes.TryGetValue(key, out var sets);
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
