using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.Cars;
using UnityEngine;
using Object = UnityEngine.Object;
#if MELON
using ItemType = Il2Cpp.InventoryItemScriptableObject.ItemType;
#else
using ItemType = InventoryItemScriptableObject.ItemType;
#endif
#if MELON
using GameParts = Il2CppSystem.Collections.Generic.List<Il2Cpp.VehiclePartSO>;
using PartsDictionary = Il2CppSystem.Collections.Generic.Dictionary<Il2Cpp.VehiclePartSO, Il2CppSystem.Collections.Generic.List<UnityEngine.GameObject>>;
#else
using GameParts = Il2CppSystem.Collections.Generic.List<VehiclePartSO>;
using PartsDictionary = Il2CppSystem.Collections.Generic.Dictionary<VehiclePartSO, Il2CppSystem.Collections.Generic.List<UnityEngine.GameObject>>;
#endif

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// The game objects behind the parts. For each part: a runtime <c>VehicleCosmetic</c> (registered in the item catalog under
/// an id hashed from its name), a mesh and a material made from the car's own material (same shader, the part's textures).
/// For each car: the parts that fit it are appended at the END of its <c>UpgradesLists.Parts</c> (so the bit a part takes in
/// the game's mounted-parts bitset is its index, and no stock index moves), and each controller instance (the networked
/// car, the garage's model) gets one child object per part with the game's own <c>VehicleUpgradeObject</c>, registered in
/// its <c>totalPartsDictionary</c>: from there the game's own code shows and hides it by the bitset, in the garage and on
/// every car in a session. That object is an empty stand-in when the car spawns; its mesh, material and textures are
/// made the first time the game shows it (<see cref="PartBuilds"/>), so a car costs the same whether it could carry five
/// parts or fifty. Everything is idempotent, so it can be called from any hook; any failure is logged and the part is
/// just not there, never an exception into the game.
/// </summary>
internal static class PartObjects
{
    private const string Mod = CustomCosmeticsPlugin.ModId;
    internal const string LiveryProperty = "_Livery_Texture";

    private static readonly Dictionary<uint, VehicleCosmetic> Sos = new();
    private static readonly Dictionary<uint, Sprite> Icons = new();
    /// <summary>UpgradesLists (pointer) the parts were appended to, with the list's length afterwards.</summary>
    private static readonly Dictionary<IntPtr, int> Done = new();
    private static readonly HashSet<string> Logged = new();
    private static int _errors;

    /// <summary>The feature switched itself off after repeated failures.</summary>
    public static bool Off { get; private set; }

    public static int SoCount => Sos.Count;
    public static int CarsDone => Done.Count;

    internal static void Fail(Exception e, string where)
    {
        DebugLog.Error(Mod, e, where);
        if (++_errors == 1) CustomCosmeticsPlugin.Logger?.LogWarning($"Custom Cosmetics: {where} failed: {e.Message}");
        if (_errors >= 25 && !Off)
        {
            Off = true;
            CustomCosmeticsPlugin.Logger?.LogError("Custom Cosmetics: too many errors; the parts are switched off for this session (the garage and cars work as without the mod).");
        }
    }

    internal static void LogOnce(string key, string text)
    {
        if (Logged.Add(key)) CustomCosmeticsPlugin.Logger?.LogInfo("Custom Cosmetics: " + text);
    }

    // ---------------------------------------------------------------- the items

    public static VehicleCosmetic SoOf(uint itemId) => Sos.TryGetValue(itemId, out var so) ? so : null;

    /// <summary>Puts each part's price as the garage should ask it now (a career started or stopped, the free-play
    /// prices setting changed). Cheap: only touches the items when the answer changed.</summary>
    public static void RefreshPrices()
    {
        try
        {
            foreach (var kv in Sos)
            {
                if (kv.Value == null) continue;
                var p = PartLibrary.ByItemId(kv.Key);
                if (p == null) continue;
                int want = CosmeticState.PriceNow(p);
                if (kv.Value.Price != want) kv.Value.Price = want;
            }
        }
        catch (Exception e) { Fail(e, "PartObjects.RefreshPrices"); }
    }

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
        so.Price = CosmeticState.PriceNow(p);
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

    internal static Texture2D LoadPng(string name, byte[] png, bool linear, bool nonReadable = true)
    {
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = name };
        if (!ImageConversion.LoadImage(t, new Il2CppStructArray<byte>(png), nonReadable))
        {
            Object.Destroy(t);
            return null;
        }
        t.wrapMode = TextureWrapMode.Clamp; // like the game's car textures: no bleeding across the atlas edge
        // sampled like the modded cars' textures (core CarAssets.Sampling): trilinear, so lamp lenses and bars don't step
        // between mip levels, and anisotropic for parts seen at a grazing angle (bars, racks, steps)
        t.filterMode = FilterMode.Trilinear;
        t.anisoLevel = 8;
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

            var existing = new List<uint>(parts.Count);
            var names = new List<string>(parts.Count);
            VehicleCosmetic like = null;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                existing.Add(part != null ? part.ItemId : 0);
                bool ours = part != null && PartLibrary.IsOurs(part.ItemId);
                names.Add(part != null && !ours ? part.name : null);
                if (like == null && part != null && !ours) like = part.TryCast<VehicleCosmetic>();
            }
            var candidates = new List<PartToAdd>();
            var looks = new List<string>();
            foreach (var p in PartLibrary.Parts)
            {
                if (FitLookup.Find(p.Manifest.Fits, car, modded?.Id) == null) continue;
                // a part that takes the look of the game's part this car has: drawn in its place, not a part of its own
                if (!PartReplace.OwnEntry(p.Manifest, names)) { looks.Add(p.Id + " (draws " + p.Manifest.Replaces + ")"); continue; }
                candidates.Add(new PartToAdd(p.Id, p.ItemId));
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
                          (candidates.Count == 0 ? " (none fit this car)" : "") + (skipped.Count > 0 ? "; left out: " + string.Join("; ", skipped) : "") +
                          (looks.Count > 0 ? "; in place of the game's own: " + string.Join(", ", looks) : "");
            if (added > 0 || skipped.Count > 0) LogOnce("car" + key, what + ".");
            else DebugLog.Info(Mod, what);
        }
        catch (Exception e) { Fail(e, "PartObjects.EnsureCar"); }
    }

    // ---------------------------------------------------------------- the car objects

    internal static bool UsesCarShader(Material m, PropertyBlockSetter setter)
    {
        if (m == null || !m.HasProperty(LiveryProperty)) return false;
        string want = setter != null ? setter.shaderName : null;
        return string.IsNullOrEmpty(want) || m.shader.name == want;
    }

    /// <summary>Is this car one the parts are offered on (a Mod Manager car, or a stock car when the config says so)?</summary>
    private static bool Eligible(CarInfo info) => ModdedCars.ByCarName(info.name) != null || CustomCosmeticsConfig.StockCars.Value;

    /// <summary>
    /// Makes sure this controller instance has an upgrade object for each custom part of its car and that the game's
    /// <c>totalPartsDictionary</c> lists it. The objects are empty stand-ins at the parts' places, hidden like every part
    /// the car doesn't wear: a few milliseconds for a whole car. What a part looks like is made when the game first
    /// switches its stand-in on (<see cref="PartBuilds"/>). <paramref name="garage"/>: the garage's model (lowered as the
    /// car settles, see the layout offset). <paramref name="setter"/>: the instance's PropertyBlockSetter, which drives
    /// the car's lights, dirt and wet on every renderer it knows. Safe to call as often as you like: a call that finds
    /// the game's dictionary as the last one left it does nothing.
    /// </summary>
    public static void Attach(Transform cosmetics, CarInfo info, PartsDictionary dict, PropertyBlockSetter setter, bool garage)
    {
        if (Off || info == null) return;
        // every car (stock ones too, whatever the config): a fitted look for the game's own parts that need one
        if (cosmetics != null) AttachLooks(cosmetics, info, dict, setter, garage);
        if (!Eligible(info)) return;
        string car = info.name;
        string where = $"{car} ({(garage ? "garage model" : "car")})";
        if (cosmetics == null) { LogOnce("nocosm" + where, $"{where}: the controller has no upgradesParent yet; its parts wait for the next refresh."); return; }
        if (dict == null) { LogOnce("nodict" + where, $"{where}: the controller has no totalPartsDictionary yet; its parts wait for the next refresh."); return; }
        try
        {
            EnsureCar(info);
            var list = info.UpgradesLists?.Parts;
            if (list == null) return;
            int listCount = list.Count;
            var set = PartBuilds.SetOf(cosmetics);
            if (set != null && set.Settled(dict.Pointer, dict.Count, listCount)) return;
            long t0 = Stopwatch.GetTimestamp();
            if (set != null && set.Setter == null && setter != null) set.Setter = setter;
            var modded = ModdedCars.ByCarName(car);
            PartBuilds.WheelSet wheels = null;
            int made = 0, objects = 0, back = 0, first = -1, last = -1;
            for (int i = 0; i < listCount; i++)
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
                set ??= PartBuilds.NewSet(cosmetics, car, setter, garage);
                // the game made its dictionary anew and missed ours (the wheels' sit outside the cosmetics): the same
                // objects go back in, never a second set
                var stand = set.Find(part, look: false);
                if (stand != null) back++;
                else
                {
                    var fit = FitLookup.Find(part.Manifest.Fits, car, modded?.Id);
                    if (fit == null) continue; // not a skip: the part just isn't for this car
                    string why = null;
                    if (PartShape.Pick(part.Mesh.Shapes, out _, car, modded?.Id) < 0) why = "its mesh has no triangles";
                    else if (part.Manifest.Mount == PartMount.Wheels) stand = PartBuilds.AddWheels(set, part, so, fit, i, ref wheels, out why);
                    else stand = PartBuilds.AddBody(set, part, so, fit, i);
                    if (stand == null) { if (why != null) LogOnce("skip" + where + part.Id, $"{where}: {part.Id} skipped: {why}."); continue; }
                    made++;
                    objects += stand.Gos.Length;
                    if (first < 0) first = i;
                    last = i;
                }
                if (!dict.ContainsKey(registered)) dict[registered] = new Il2CppSystem.Collections.Generic.List<GameObject>();
                foreach (var go in stand.Gos) dict[registered].Add(go);
            }
            if (set == null) return;
            set.Settle(dict.Pointer, dict.Count, listCount);
            if (made + back == 0) return;
            PartBuilds.Refreshed(cosmetics);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            // one line for the car (it was three per part): what each part is and where it sits is said in the debug log
            // when it is built
            string line = $"{where}: {made} part stand-in(s) made in {ms:0.0} ms" + (made > 0 ? $" (bits {first}..{last}{(objects > made ? $", {objects} objects with the wheels'" : "")})" : "") +
                          (back > 0 ? $", {back} put back in the game's list" : "") + "; a part is built when the game first shows it.";
            if (made > 0) CustomCosmeticsPlugin.Logger?.LogInfo("Custom Cosmetics: " + line);
            else DebugLog.Verbose(Mod, line);
        }
        catch (Exception e) { Fail(e, "PartObjects.Attach"); }
    }

    // ---------------------------------------------------------------- the game's parts drawn with our shape

    /// <summary>
    /// For each part that takes the look of a game part (<see cref="PartManifest.Replaces"/>) and fits this car, when the
    /// car's list has that game part: one object of ours under the car's cosmetics (no upgrade object, no dictionary entry:
    /// the game knows nothing of it), shown exactly while the game shows its own part (<see cref="StockLooks"/>). Like
    /// every part it is an empty stand-in until it is first shown; the game's own meshes draw until ours is built, and
    /// nothing from then on. Once per controller instance; cheap on every later call.
    /// </summary>
    private static void AttachLooks(Transform cosmetics, CarInfo info, PartsDictionary dict, PropertyBlockSetter setter, bool garage)
    {
        try
        {
            if (!PartLibrary.HasLooks || StockLooks.Done(cosmetics)) return;
            var list = info.UpgradesLists?.Parts;
            if (list == null) return;
            string car = info.name;
            var modded = ModdedCars.ByCarName(car);
            string where = $"{car} ({(garage ? "garage model" : "car")})";
            PartBuilds.CarSet set = null;
            bool waiting = false;
            foreach (var part in PartLibrary.Parts)
            {
                var m = part.Manifest;
                if (m.Replaces.Length == 0) continue;
                var fit = FitLookup.Find(m.Fits, car, modded?.Id);
                if (fit == null) continue;
                VehiclePartSO stock = null;
                for (int i = 0; i < list.Count && stock == null; i++)
                {
                    var so = list[i];
                    if (so != null && !PartLibrary.IsOurs(so.ItemId) && string.Equals(so.name, m.Replaces, StringComparison.OrdinalIgnoreCase)) stock = so;
                }
                if (stock == null) continue;
                // the objects the game switches for its part: from its dictionary, else the node of that name in the part's slot
                var shownBy = new List<GameObject>();
                var key = stock.TryCast<VehicleCosmetic>();
                if (dict != null && key != null && dict.ContainsKey(key))
                {
                    var objs = dict[key];
                    if (objs != null)
                        for (int k = 0; k < objs.Count; k++)
                            if (objs[k] != null) shownBy.Add(objs[k]);
                }
                if (shownBy.Count == 0)
                {
                    var node = FindItem(cosmetics, m.Slot, m.Replaces);
                    if (node != null) shownBy.Add(node.gameObject);
                }
                if (shownBy.Count == 0) { waiting = true; LogOnce("lookwait" + where + part.Id, $"{where}: {part.Id} waits: the game's {m.Replaces} has no objects yet."); continue; }
                set ??= PartBuilds.SetOf(cosmetics) ?? PartBuilds.NewSet(cosmetics, car, setter, garage);
                if (set.Find(part, look: true) != null) continue;
                if (PartShape.Pick(part.Mesh.Shapes, out _, car, modded?.Id) < 0) continue;
                var stand = PartBuilds.AddBody(set, part, null, fit, -1);
                StockLooks.Watch(shownBy, stand.Gos[0]);
                int count = shownBy.Count;
                DebugLog.Verbose(Mod, () => $"{where}: {part.Id} stands by for the game's {m.Replaces} ({count} object(s)); it is drawn in its place once the game shows it");
            }
            if (set != null) set.Placing = false;
            if (!waiting) StockLooks.MarkDone(cosmetics);
        }
        catch (Exception e) { Fail(e, "PartObjects.AttachLooks"); }
    }

    /// <summary>The item node <paramref name="name"/> in the slot <paramref name="slot"/> ("Visual/Hood") under the cosmetics.</summary>
    private static Transform FindItem(Transform cosmetics, string slot, string name)
    {
        string leaf = slot != null && slot.Contains("/") ? slot.Substring(slot.LastIndexOf('/') + 1) : slot;
        foreach (var t in cosmetics.GetComponentsInChildren<Transform>(true))
            if (t != null && string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase) && t.parent != null &&
                (string.IsNullOrEmpty(leaf) || t.parent.name == leaf)) return t;
        return null;
    }

    /// <summary>
    /// Hands the renderer to the car's PropertyBlockSetter, the game's own way to give a renderer the car's lights (the
    /// shader glows lights.png by the head, tail, brake and reverse light intensities in the block), dirt, mud, snow and
    /// wet. The block starts as a copy of the stock cosmetic's, so the part is born with the car's present state.
    /// </summary>
    internal static void RegisterWithSetter(MeshRenderer renderer, Material material, Material source, PropertyBlockSetter setter, string part, string car)
    {
        if (setter == null) { LogOnce("nosetter" + car, $"{car}: no PropertyBlockSetter on the controller; its parts have no car lighting (no glow, dirt or wet)."); return; }
        try
        {
            setter.AddRenderer(renderer);
            var blocks = setter.materialPropertyBlocks;
            if (blocks != null && blocks.ContainsKey(material))
            {
                var ours = blocks[material];
                // The stock cosmetic's block when the source is one of the setter's materials. Otherwise (a slot whose
                // stock parts aren't in the setter, such as the Hilo's side slot for window_nets, or the body material
                // as the source) the setter's own present values: the state it pushes to every block, so the part is
                // still born with the car's lights, dirt and wet instead of the defaults.
                var theirs = source != null && blocks.ContainsKey(source) ? blocks[source] : null;
                foreach (int id in theirs != null ? CopiedIds : SetterIds)
                {
                    if (theirs != null) { if (theirs.HasFloat(id)) ours.SetFloat(id, theirs.GetFloat(id)); }
                    else
                    {
                        try { ours.SetFloat(id, setter.GetMaterialPropertyValue(id)); }
                        catch (Exception ex) { DebugLog.Error(Mod, ex, "PartObjects.RegisterWithSetter value"); }
                    }
                }
                ours.SetTexture(LiveryProperty, material.GetTexture(LiveryProperty));
                setter.PushBlock(material, ours);
                bool copied = theirs != null;
                DebugLog.Verbose(Mod, () => copied
                    ? $"{car}: {part} is in the car's PropertyBlockSetter (lights, dirt and wet follow the car)"
                    : $"{car}: {part} is in the car's PropertyBlockSetter (its start state from the setter's present values: its source material isn't one of the setter's)");
            }
            else LogOnce("setter" + car + part, $"{car}: {part} was added to the car's PropertyBlockSetter, but no block was made for its material; it follows the car from the next light or weather change.");
        }
        catch (Exception e) { LogOnce("setterfail" + car + part, $"{car}: {part} could not join the car's PropertyBlockSetter ({e.GetType().Name}: {e.Message}); it draws without the car's lights, dirt and wet."); }
    }

    private static int[] _copiedIds, _setterIds;

    /// <summary>The block floats a part copies from the stock cosmetic's block: dirt, weather, damage, the lights and the fade.</summary>
    private static int[] CopiedIds => _copiedIds ??= new[]
    {
        PropertyBlockSetter.DustAmountPropertyID, PropertyBlockSetter.MudAmountPropertyID, PropertyBlockSetter.SnowAmountPropertyID, PropertyBlockSetter.SandAmountPropertyID,
        PropertyBlockSetter.WetAmountPropertyID, PropertyBlockSetter.RainAmountPropertyID, PropertyBlockSetter.DamageAmountPropertyID, PropertyBlockSetter.HeadlightsIntensityPropertyID,
        PropertyBlockSetter.RearlightsIntensityPropertyID, PropertyBlockSetter.BrakingRearlightsIntensityPropertyID, PropertyBlockSetter.ReverselightsIntensityPropertyID,
        PropertyBlockSetter.FadeOutPropertyID,
    };

    /// <summary>The same read from the setter's present values when there is no block to copy: the fade is left out (it is
    /// per renderer, not the car's state).</summary>
    private static int[] SetterIds => _setterIds ??= new[]
    {
        PropertyBlockSetter.DustAmountPropertyID, PropertyBlockSetter.MudAmountPropertyID, PropertyBlockSetter.SnowAmountPropertyID, PropertyBlockSetter.SandAmountPropertyID,
        PropertyBlockSetter.WetAmountPropertyID, PropertyBlockSetter.RainAmountPropertyID, PropertyBlockSetter.DamageAmountPropertyID, PropertyBlockSetter.HeadlightsIntensityPropertyID,
        PropertyBlockSetter.RearlightsIntensityPropertyID, PropertyBlockSetter.BrakingRearlightsIntensityPropertyID, PropertyBlockSetter.ReverselightsIntensityPropertyID,
    };
}
