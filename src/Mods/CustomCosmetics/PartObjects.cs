using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.Cars;
using UnityEngine;
using Object = UnityEngine.Object;
using ItemType = InventoryItemScriptableObject.ItemType;
using GameParts = Il2CppSystem.Collections.Generic.List<VehiclePartSO>;
using PartsDictionary = Il2CppSystem.Collections.Generic.Dictionary<VehiclePartSO, Il2CppSystem.Collections.Generic.List<UnityEngine.GameObject>>;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// The game objects behind the parts. For each part: a runtime <c>VehicleCosmetic</c> (registered in the item catalog under
/// an id hashed from its name), a mesh and a material made from the car's own material (same shader, the part's textures).
/// For each car: the parts that fit it are appended at the END of its <c>UpgradesLists.Parts</c> (so the bit a part takes in
/// the game's mounted-parts bitset is its index, and no stock index moves), and each controller instance (the networked
/// car, the garage's model) gets one child object per part with the game's own <c>VehicleUpgradeObject</c>, registered in
/// its <c>totalPartsDictionary</c>: from there the game's own code shows and hides it by the bitset, in the garage and on
/// every car in a session. Everything is idempotent, so it can be called from any hook; any failure is logged and the
/// part is just not there, never an exception into the game.
/// </summary>
internal static class PartObjects
{
    private const string Mod = CustomCosmeticsPlugin.ModId;
    private const string LiveryProperty = "_Livery_Texture";

    private static readonly Dictionary<uint, VehicleCosmetic> Sos = new();
    private static readonly Dictionary<uint, Mesh> Meshes = new();
    private static readonly Dictionary<uint, Sprite> Icons = new();
    private static readonly Dictionary<string, Material> Materials = new();
    private static readonly Dictionary<uint, Dictionary<string, Texture2D>> Textures = new();
    /// <summary>UpgradesLists (pointer) the parts were appended to, with the list's length afterwards.</summary>
    private static readonly Dictionary<IntPtr, int> Done = new();
    private static readonly HashSet<string> Logged = new();
    private static int _errors;

    /// <summary>The feature switched itself off after repeated failures.</summary>
    public static bool Off { get; private set; }

    public static int SoCount => Sos.Count;
    public static int CarsDone => Done.Count;

    private static void Fail(Exception e, string where)
    {
        DebugLog.Error(Mod, e, where);
        if (++_errors == 1) CustomCosmeticsPlugin.Logger?.LogWarning($"Custom Cosmetics: {where} failed: {e.Message}");
        if (_errors >= 25 && !Off)
        {
            Off = true;
            CustomCosmeticsPlugin.Logger?.LogError("Custom Cosmetics: too many errors; the parts are switched off for this session (the garage and cars work as without the mod).");
        }
    }

    private static void LogOnce(string key, string text)
    {
        if (Logged.Add(key)) CustomCosmeticsPlugin.Logger?.LogInfo("Custom Cosmetics: " + text);
    }

    // ---------------------------------------------------------------- the items

    public static VehicleCosmetic SoOf(uint itemId) => Sos.TryGetValue(itemId, out var so) ? so : null;

    private static VehicleCosmetic EnsureSo(CustomPart p, VehicleCosmetic like)
    {
        if (Sos.TryGetValue(p.ItemId, out var have) && have != null) return have;
        if (like == null) return null;
        // Part ids are hashes; the odds of a clash with a game item are tiny, but a clash must never replace its item.
        if (CosmeticsFeature.IdTaken(p.ItemId))
        {
            LogOnce("taken" + p.ItemId, $"{p.Id}: its item id 0x{p.ItemId:X8} belongs to a game item; the part is left out.");
            return null;
        }
        var so = ScriptableObject.CreateInstance<VehicleCosmetic>();
        so.name = "CustomCosmetic_" + p.Id;
        so.ItemId = p.ItemId;
        so.Type = ItemType.VehicleCosmetic;
        so.Price = p.Manifest.Price;
        so.RequiredDlc = DlcId.None;
        so.CosmeticType = Enum.Parse<CosmeticType>(p.Manifest.CosmeticType);
        so.Name = PartText.Make(like.Name, p.NameKey, p.Manifest.Name);
        so.Description = PartText.Make(like.Description ?? like.Name, p.DescriptionKey,
            "A custom cosmetic from the Custom Cosmetics mod. Players without the mod don't see it.");
        if (so.Name == null)
        {
            Object.Destroy(so);
            LogOnce("noname", "no string table to put the part names in (the stock cosmetic has no Name); the parts are not added.");
            return null;
        }
        so.Icon = IconOf(p);
        so.Effects = new Il2CppSystem.Collections.Generic.List<UpgradeEffect>();
        so.LinkedParts = new GameParts();
        so.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(so);
        Sos[p.ItemId] = so;
        return so;
    }

    private static Sprite IconOf(CustomPart p)
    {
        if (Icons.TryGetValue(p.ItemId, out var have) && have != null) return have;
        Texture2D tex = null;
        if (p.Icon != null) tex = LoadPng(p.Id + "_icon", p.Icon, linear: false, nonReadable: false);
        tex ??= MakeIcon(p.ItemId);
        var s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        s.name = p.Id + "_icon";
        s.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(s);
        Icons[p.ItemId] = s;
        return s;
    }

    /// <summary>A plain icon for a part without icon.png: a tile in a colour from its id, with a bar across.</summary>
    private static Texture2D MakeIcon(uint id)
    {
        const int n = 128;
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "cc_icon" };
        var c = Color.HSVToRGB((id % 360) / 360f, 0.45f, 0.55f);
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool bar = y > 52 && y < 76 && x > 20 && x < 108;
                px[y * n + x] = bar ? new Color32(235, 235, 235, 255) : (Color32)c;
            }
        t.SetPixels32(px);
        t.Apply(false, false);
        t.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(t);
        return t;
    }

    private static Texture2D LoadPng(string name, byte[] png, bool linear, bool nonReadable = true)
    {
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = name };
        if (!ImageConversion.LoadImage(t, new Il2CppStructArray<byte>(png), nonReadable))
        {
            Object.Destroy(t);
            return null;
        }
        t.wrapMode = TextureWrapMode.Clamp; // like the game's car textures: no bleeding across the atlas edge
        t.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(t);
        return t;
    }

    /// <summary>Puts every part's item in the game's item catalog (id, name, lookup by name). Cheap when it is all in.</summary>
    public static void RegisterAll()
    {
        try
        {
            var cat = PlayerInventoryManager.InventoryItemsCatalog;
            if (cat?.IdToSO == null) return;
            foreach (var kv in Sos)
            {
                var so = kv.Value;
                if (so == null) continue;
                if (cat.IdToSO.ContainsKey(kv.Key))
                {
                    var other = cat.IdToSO[kv.Key];
                    if (other != null && other.Pointer != so.Pointer) LogOnce("clash" + kv.Key, $"another item took 0x{kv.Key:X8}; {so.name} is not in the catalog.");
                    continue;
                }
                if (cat.NameToId.ContainsKey(so.name)) { LogOnce("nameclash" + kv.Key, $"another item is named {so.name}; it is not in the catalog."); continue; }
                cat.IdToSO[kv.Key] = so;
                cat.IdToName[kv.Key] = so.name;
                cat.NameToId[so.name] = kv.Key;
                LogOnce("reg" + kv.Key, $"{so.name} is in the item catalog (0x{kv.Key:X8}).");
            }
        }
        catch (Exception e) { Fail(e, "PartObjects.RegisterAll"); }
    }

    // ---------------------------------------------------------------- the car's part list

    /// <summary>
    /// Appends the parts that fit this car to the end of its part list (once; again if the list was rebuilt). Modded cars
    /// have their own copy of the list; stock cars only when the config says so.
    /// </summary>
    public static void EnsureCar(CarInfo info)
    {
        if (Off || info == null) return;
        try
        {
            var lists = info.UpgradesLists;
            var parts = lists?.Parts;
            if (parts == null) return;
            string car = info.name;
            var modded = ModdedCars.ByCarName(car);
            if (modded == null && !CustomCosmeticsConfig.StockCars.Value) return;
            var key = lists.Pointer;
            if (Done.TryGetValue(key, out int count) && count == parts.Count) return;

            var candidates = new List<PartToAdd>();
            foreach (var p in PartLibrary.Parts)
                if (FitLookup.Find(p.Manifest.Fits, car, modded?.Id) != null) candidates.Add(new PartToAdd(p.Id, p.ItemId));
            var existing = new List<uint>(parts.Count);
            VehicleCosmetic like = null;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                existing.Add(part != null ? part.ItemId : 0);
                if (like == null && part != null && !PartLibrary.IsOurs(part.ItemId)) like = part.TryCast<VehicleCosmetic>();
            }
            var skipped = new List<string>();
            var plan = PartLists.Plan(existing, candidates, skipped);
            int first = parts.Count, added = 0;
            foreach (var add in plan)
            {
                var so = EnsureSo(PartLibrary.ByItemId(add.ItemId), like);
                if (so == null) continue;
                parts.Add(so);
                added++;
            }
            Done[key] = parts.Count;
            RegisterAll();
            string what = $"{car}: {added} custom part(s) appended to its list of {first} (bits {first}..{parts.Count - 1})" +
                          (candidates.Count == 0 ? " (none fit this car)" : "") + (skipped.Count > 0 ? "; left out: " + string.Join("; ", skipped) : "");
            if (added > 0 || skipped.Count > 0) LogOnce("car" + key, what + ".");
            else DebugLog.Info(Mod, what);
        }
        catch (Exception e) { Fail(e, "PartObjects.EnsureCar"); }
    }

    // ---------------------------------------------------------------- the car objects

    /// <summary>The car's root: the node above the cosmetic parts that has the game's "Parts" child.</summary>
    private static Transform RootOf(Transform from)
    {
        for (var t = from; t != null; t = t.parent)
            if (t.Find("Parts") != null) return t;
        return from.root;
    }

    private static Transform GroupOf(Transform cosmetics)
    {
        foreach (var t in cosmetics.GetComponentsInChildren<Transform>(true))
            if (t.name == "Visual" && t.parent != null && t.parent.name.EndsWith("_CosmeticParts", StringComparison.Ordinal)) return t;
        return cosmetics;
    }

    private static Material CarMaterial(Transform root)
    {
        foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r == null) continue;
            foreach (var m in r.sharedMaterials)
                if (m != null && m.HasProperty(LiveryProperty)) return m;
        }
        return null;
    }

    private static bool UsesCarShader(Material m, PropertyBlockSetter setter)
    {
        if (m == null || !m.HasProperty(LiveryProperty)) return false;
        string want = setter != null ? setter.shaderName : null;
        return string.IsNullOrEmpty(want) || m.shader.name == want;
    }

    /// <summary>
    /// The material to copy: one a stock cosmetic of this very car uses (the one in the part's own slot first), so the shader
    /// settings, render state and lighting match the car's other parts and the car's PropertyBlockSetter (which drives the
    /// lights, dirt and wet by that shader) takes it. Falls back to the car's body material. <paramref name="from"/> says
    /// which it was.
    /// </summary>
    private static Material SourceMaterial(Transform cosmetics, Transform root, string slot, PropertyBlockSetter setter, out string from)
    {
        from = null;
        string[] path = (slot ?? "").Split('/');
        string leaf = path.Length > 0 ? path[path.Length - 1] : "";
        string parent = path.Length > 1 ? path[path.Length - 2] : null;
        Material any = null;
        foreach (var t in cosmetics.GetComponentsInChildren<Transform>(true))
        {
            bool inSlot = t.name == leaf && (parent == null || (t.parent != null && t.parent.name == parent));
            if (!inSlot && any != null) continue;
            foreach (var r in t.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (!UsesCarShader(m, setter)) continue;
                    if (inSlot) { from = "the stock " + slot + " cosmetic's material"; return m; }
                    if (any == null) any = m;
                }
            }
        }
        if (any != null) { from = "a stock cosmetic's material"; return any; }
        var body = CarMaterial(root);
        if (body != null) from = "the car's body material";
        return body;
    }

    private static Mesh MeshOf(CustomPart p)
    {
        if (Meshes.TryGetValue(p.ItemId, out var have) && have != null) return have;
        var mesh = CarAssets.BuildMesh("cc_" + p.Id, new List<MeshPart> { p.Shape }, Matrix4x4.identity);
        if (mesh != null) Meshes[p.ItemId] = mesh;
        return mesh;
    }

    private static Texture2D Tex(CustomPart p, string file, byte[] png, bool linear, Texture2D fallback)
    {
        if (!Textures.TryGetValue(p.ItemId, out var map)) Textures[p.ItemId] = map = new Dictionary<string, Texture2D>();
        if (map.TryGetValue(file, out var have) && have != null) return have;
        Texture2D t = png != null ? LoadPng(p.Id + "_" + file, png, linear) : null;
        if (t == null) t = fallback;
        map[file] = t;
        return t;
    }

    private static Material MaterialOf(CustomPart p, Material carMaterial)
    {
        string key = p.Id + "|" + carMaterial.GetInstanceID();
        if (Materials.TryGetValue(key, out var have) && have != null) return have;
        var m = new Material(carMaterial) { name = "cc_" + p.Id };
        m.SetTexture(LiveryProperty, Tex(p, "albedo", p.Albedo, false, Texture2D.whiteTexture));
        m.SetTexture("_AO_Texture", Tex(p, "ao", p.Ao, true, Texture2D.whiteTexture));
        m.SetTexture("_Lights_Texture", Tex(p, "lights", p.Lights, true, Texture2D.blackTexture));
        m.SetTexture("_Mask_Texture", Tex(p, "mask", p.Mask, true, Texture2D.blackTexture));
        // The game's livery goes onto materials that are switchable; the part keeps its own albedo.
        if (m.HasProperty("_IsSwitchable")) m.SetFloat("_IsSwitchable", 0f);
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(m);
        Materials[key] = m;
        return m;
    }

    /// <summary>Is this car one the parts are offered on (a Mod Manager car, or a stock car when the config says so)?</summary>
    private static bool Eligible(CarInfo info) => ModdedCars.ByCarName(info.name) != null || CustomCosmeticsConfig.StockCars.Value;

    /// <summary>
    /// Makes sure this controller instance has an upgrade object for each custom part of its car and that the game's
    /// <c>totalPartsDictionary</c> lists it. <paramref name="garage"/>: the garage's model (lowered as the car settles, see
    /// the layout offset). <paramref name="setter"/>: the instance's PropertyBlockSetter, which drives the car's lights,
    /// dirt and wet on every renderer it knows. Safe to call as often as you like.
    /// </summary>
    public static void Attach(Transform cosmetics, CarInfo info, PartsDictionary dict, PropertyBlockSetter setter, bool garage)
    {
        if (Off || info == null || !Eligible(info)) return;
        string where = $"{info.name} ({(garage ? "garage model" : "car")})";
        if (cosmetics == null) { LogOnce("nocosm" + where, $"{where}: the controller has no upgradesParent yet; its parts wait for the next refresh."); return; }
        if (dict == null) { LogOnce("nodict" + where, $"{where}: the controller has no totalPartsDictionary yet; its parts wait for the next refresh."); return; }
        try
        {
            EnsureCar(info);
            var list = info.UpgradesLists?.Parts;
            if (list == null) return;
            Transform root = null, group = null;
            for (int i = 0; i < list.Count; i++)
            {
                var so = list[i];
                if (so == null) continue;
                var part = PartLibrary.ByItemId(so.ItemId);
                if (part == null) continue;
                var registered = so.TryCast<VehicleCosmetic>();
                if (dict.ContainsKey(registered))
                {
                    var objs = dict[registered];
                    if (objs != null && objs.Count > 0) continue;
                }
                root ??= RootOf(cosmetics);
                group ??= GroupOf(cosmetics);
                var source = SourceMaterial(cosmetics, root, part.Manifest.Slot, setter, out string from);
                if (source == null) { LogOnce("nomaterial" + where + part.Id, $"{where}: {part.Id} skipped: no car material found to copy (nothing under {cosmetics.name} uses a material with {LiveryProperty})."); continue; }
                var go = Build(part, so, info, root, group, source, setter, garage, out string why);
                if (go == null) { if (why != null) LogOnce("skip" + where + part.Id, $"{where}: {part.Id} skipped: {why}."); continue; }
                if (!dict.ContainsKey(registered)) dict[registered] = new Il2CppSystem.Collections.Generic.List<GameObject>();
                dict[registered].Add(go);
                LogOnce("built" + where + part.Id,
                    $"{where}: {part.Id} object made (bit {i}, slot {part.Manifest.Slot}) under {group.name} of {root.name}: car-root pos {F(root.InverseTransformPoint(go.transform.position))}, " +
                    $"local pos {F(go.transform.localPosition)}, scale {F(go.transform.localScale)}, layer {go.layer}, material from {from}; hidden until the game mounts it.");
            }
        }
        catch (Exception e) { Fail(e, "PartObjects.Attach"); }
    }

    /// <summary>After the game's own refresh: says once per part and car whether its object ended up shown (the proof the
    /// bitset, the dictionary and the object agree), with where it is and how it draws.</summary>
    public static void Report(CarInfo info, PartsDictionary dict, bool garage)
    {
        if (Off || info == null || dict == null || !Eligible(info)) return;
        try
        {
            var list = info.UpgradesLists?.Parts;
            if (list == null) return;
            string where = $"{info.name} ({(garage ? "garage model" : "car")})";
            for (int i = 0; i < list.Count; i++)
            {
                var so = list[i];
                if (so == null) continue;
                var part = PartLibrary.ByItemId(so.ItemId);
                if (part == null) continue;
                var registered = so.TryCast<VehicleCosmetic>();
                if (!dict.ContainsKey(registered)) continue;
                var objs = dict[registered];
                if (objs == null) continue;
                for (int k = 0; k < objs.Count; k++)
                {
                    var go = objs[k];
                    if (go == null || !go.activeSelf) continue;
                    var r = go.GetComponent<MeshRenderer>();
                    string draw = r == null ? "no renderer" :
                        $"renderer {(r.enabled ? "on" : "OFF")}, shader {(r.sharedMaterial != null ? r.sharedMaterial.shader.name : "none")}, world bounds centre {F(r.bounds.center)} size {F(r.bounds.size)}";
                    LogOnce("shown" + where + part.Id,
                        $"{where}: {part.Id} is SHOWN (active {go.activeInHierarchy} in the hierarchy, layer {go.layer}, {draw}).");
                }
            }
        }
        catch (Exception e) { Fail(e, "PartObjects.Report"); }
    }

    private static GameObject Build(CustomPart part, VehiclePartSO so, CarInfo info, Transform root, Transform group, Material source,
        PropertyBlockSetter setter, bool garage, out string why)
    {
        why = null;
        var modded = ModdedCars.ByCarName(info.name);
        var fit = FitLookup.Find(part.Manifest.Fits, info.name, modded?.Id);
        if (fit == null) return null; // not a skip: the part just isn't for this car
        var mesh = MeshOf(part);
        if (mesh == null) { why = "its mesh has no triangles"; return null; }

        // A modded car's model sits on its donor skeleton at the layout's offset (CarBody does the same for the game's
        // slots); the garage model is lowered by the wheel drop, as the car settles.
        var offset = Vector3.zero;
        var layout = modded?.Layout;
        if (layout != null)
        {
            offset = layout.Offset;
            if (garage) offset.y -= layout.WheelDrop;
        }

        var go = new GameObject("CC_" + part.Id);
        go.layer = group.gameObject.layer;
        var t = go.transform;
        t.SetParent(group, false);
        t.position = root.TransformPoint(new Vector3(fit.Pos[0], fit.Pos[1], fit.Pos[2]) + offset);
        t.rotation = root.rotation * Quaternion.Euler(fit.Rot[0], fit.Rot[1], fit.Rot[2]);
        var gl = group.lossyScale;
        var rl = root.lossyScale;
        t.localScale = new Vector3(Div(rl.x * fit.Scale, gl.x), Div(rl.y * fit.Scale, gl.y), Div(rl.z * fit.Scale, gl.z));

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        var material = MaterialOf(part, source);
        renderer.sharedMaterial = material;
        RegisterWithSetter(renderer, material, source, setter, part.Id, info.name);
        var upgrade = go.AddComponent<VehicleUpgradeObject>();
        upgrade.vehiclePartReference = so;
        go.SetActive(false); // the game switches it on by the car's bitset
        return go;
    }

    /// <summary>
    /// Hands the renderer to the car's PropertyBlockSetter, the game's own way to give a renderer the car's lights (the
    /// shader glows lights.png by the head, tail, brake and reverse light intensities in the block), dirt, mud, snow and
    /// wet. The block starts as a copy of the stock cosmetic's, so the part is born with the car's present state.
    /// </summary>
    private static void RegisterWithSetter(MeshRenderer renderer, Material material, Material source, PropertyBlockSetter setter, string part, string car)
    {
        if (setter == null) { LogOnce("nosetter" + car, $"{car}: no PropertyBlockSetter on the controller; its parts have no car lighting (no glow, dirt or wet)."); return; }
        try
        {
            setter.AddRenderer(renderer);
            var blocks = setter.materialPropertyBlocks;
            if (blocks != null && blocks.ContainsKey(material) && blocks.ContainsKey(source))
            {
                var ours = blocks[material];
                var theirs = blocks[source];
                foreach (int id in new[] { PropertyBlockSetter.DustAmountPropertyID, PropertyBlockSetter.MudAmountPropertyID, PropertyBlockSetter.SnowAmountPropertyID, PropertyBlockSetter.SandAmountPropertyID,
                             PropertyBlockSetter.WetAmountPropertyID, PropertyBlockSetter.RainAmountPropertyID, PropertyBlockSetter.DamageAmountPropertyID, PropertyBlockSetter.HeadlightsIntensityPropertyID,
                             PropertyBlockSetter.RearlightsIntensityPropertyID, PropertyBlockSetter.BrakingRearlightsIntensityPropertyID, PropertyBlockSetter.ReverselightsIntensityPropertyID,
                             PropertyBlockSetter.FadeOutPropertyID })
                    if (theirs.HasFloat(id)) ours.SetFloat(id, theirs.GetFloat(id));
                ours.SetTexture(LiveryProperty, material.GetTexture(LiveryProperty));
                setter.PushBlock(material, ours);
                LogOnce("setter" + car + part, $"{car}: {part} is in the car's PropertyBlockSetter (lights, dirt and wet follow the car).");
            }
            else LogOnce("setter" + car + part, $"{car}: {part} was added to the car's PropertyBlockSetter, but there was no block to copy from (the source material is not one of its materials); its lights start from their defaults.");
        }
        catch (Exception e) { LogOnce("setterfail" + car + part, $"{car}: {part} could not join the car's PropertyBlockSetter ({e.GetType().Name}: {e.Message}); it draws without the car's lights, dirt and wet."); }
    }

    private static float Div(float a, float b) => Mathf.Abs(b) < 1e-6f ? a : a / b;

    private static string F(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
}
