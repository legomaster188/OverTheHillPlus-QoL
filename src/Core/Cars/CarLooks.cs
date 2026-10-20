using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.Shared;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cars;

/// <summary>This player's look for one modded car: the MODS page settings, and what other players are told.</summary>
internal sealed class CarLookState
{
    private readonly RuntimeCar _car;

    public CarLookState(RuntimeCar car) { _car = car; }

    /// <summary>Your look as set on the MODS page (the skin without its hash until the file was read).</summary>
    public CarLook Local { get; private set; } = new();

    /// <summary>What others are told: your look, with the skin only once its file was read and checked.</summary>
    public CarLook Announced { get; private set; } = new();

    /// <summary>One line about your skin for the MODS page (null when there is nothing to say).</summary>
    public string SkinStatus { get; private set; }

    /// <summary>The settings into <see cref="Local"/> and <see cref="Announced"/>.</summary>
    public void Refresh()
    {
        var cfg = _car.Config;
        var def = _car.Def;
        if (cfg.Paint == null) return;
        var main = CarPaint.MainColour(def.Paints, cfg.Paint.Value, cfg.PaintCustom.Value);
        var trim = CarPaint.TrimColour(def.Trims, cfg.Trim.Value, cfg.TrimCustom.Value, main);
        string key = cfg.Skin.Value;
        if (string.IsNullOrEmpty(key) || !CarSkinKey.IsValid(key)) key = null;
        ulong hash = 0;
        bool skinReady = false;
        SkinStatus = null;
        if (key != null)
        {
            var sf = CarLooks.SkinFileFor(_car, key);
            if (sf == null) SkinStatus = "Your skin wasn't found (is its pack still installed?): showing your colours.";
            else if (sf.Problem != null) SkinStatus = "Your skin can't be used: " + sf.Problem + ".";
            else if (!sf.HashKnown) SkinStatus = "Loading your skin…";
            else { hash = sf.Hash; skinReady = true; }
        }
        byte engine = def.EngineIndexOf(cfg.EngineSound?.Value);
        var local = new CarLook { Main = main, Trim = trim, SkinKey = key, SkinHash = hash, Engine = engine };
        if (!local.Equals(Local)) Local = local;
        var announced = skinReady ? local : local.WithoutSkin();
        if (!announced.Equals(Announced)) Announced = announced;
    }
}

/// <summary>
/// Each modded car's paint: the colour pair or the skin its driver picked, as its own albedo texture. One runtime for
/// every modded car (looks are per player and per car).
///
/// <para><b>How the game paints a car.</b> Every car has a PropertyBlockSetter: in Awake it collects the car's
/// renderers whose material uses the car shader, grouped by material, with one MaterialPropertyBlock per material.
/// Dirt, mud, snow, sand, wet, rain, damage, lights and fade (CarNetworkedMaterialManager and the light code, through
/// UpdateMaterialProperties) and the livery (SetLiveryTexture: _Livery_Texture, on the blocks of materials whose
/// _IsSwitchable is on) are all set in those per-car blocks, never on the material. The game calls SetLiveryTexture
/// for a stock livery part (LiveryAppliedEffect, when the mounted upgrades change) and for a mod livery
/// (ModLiveryEquipper.ApplyLivery / EnsureInstalledAndApply: the garage's Livery Mods tab, and
/// VehicleCustomizationController.ApplyNetworkedModLivery when a car's equipped mod livery id changes).</para>
///
/// <para><b>Per-car looks.</b> All copies of a modded car share one material (CarBody.Material, with the base albedo),
/// so each car's look goes into its own block with the same SetLiveryTexture the game uses: the other properties in that
/// block (dirt, damage, ...) are untouched. The base car's liveries are dropped (CarCatalog); the car's own livery parts
/// keep a stock livery's effect, so when the game mounts one it sets the base car's texture, and when it sets anything
/// else on such a car's block (a mod livery for the car's name from the garage tab, or another player's equipped id)
/// <see cref="AfterGameSetLivery"/> puts the car's own texture straight back, so no donor texture ever shows on it. A
/// mod livery picked for the car in the garage's Livery Mods tab becomes its skin instead (<see cref="GameEquipped"/>).</para>
///
/// <para><b>Liveries.</b> What a car shows, in the game's order: its skin (a mod livery: a pack entry, a mod.io livery
/// for the car's name or a bundled livery picked on the MODS page), else the livery mounted in the garage's Liveries
/// tab (<see cref="CarLiveryParts"/>: read from the car's mounted-parts bitset, which the game syncs; the garage's model
/// shows the one picked in the tab), else its paint colours. An opaque image replaces the paint, as the game's own
/// liveries do; one with transparency is laid over the player's paint and trim (CarPaint.Overlay).</para>
///
/// <para><b>Textures.</b> One RGBA32 sRGB texture with mipmaps per car, colour pair, image or image-over-colours in use,
/// recoloured off the main thread (CarPaint.Recolour of the base albedo with the car's zones), read from the image's
/// PNG (a skin's checked again right before reading), or both (an overlay). At most <see cref="ColourCap"/> colour,
/// <see cref="SkinCap"/> image and <see cref="OverlayCap"/> overlay textures exist, for all modded cars together: one
/// no car uses goes after a minute, or at once when its kind is full; when every one of them is in use, another
/// player's new look shows with the standard paint (colours) or their colours (image) instead (your own always gets
/// one). Other players' look changes are applied at most every two seconds each (CarLookNet), so a player cycling
/// colours can't make everyone rebuild textures.</para>
/// </summary>
internal static class CarLooks
{
    private const int ColourCap = 6, SkinCap = 4, OverlayCap = 4;
    private const float UnusedLife = 60f, TickEvery = 0.5f;

    private sealed class Tex
    {
        public string Key;
        public Texture2D Texture;
        public float LastUsed;
        public Task<byte[]> Build;
        public int W, H;
        public string Failed;
        public RuntimeCar Car;
    }

    internal sealed class SkinRead
    {
        /// <summary>When the main thread saw it finish (bytes not made into a texture by 10 s later are dropped).</summary>
        public float DoneAt = -1f;
        public byte[] Bytes;
        public ulong Hash;
        public int Side;
        public string Problem;
    }

    internal sealed class SkinFile
    {
        public string Path;
        public string PackDir;
        /// <summary>Reads the image instead of the file at <see cref="Path"/> (an embedded livery or skin). Worker thread.</summary>
        public Func<SkinRead> Reader;
        /// <summary>A car's own livery file (CarLiveryParts keeps it): a skin rescan leaves it alone.</summary>
        public bool Owned;
        internal Task<SkinRead> Read;
        public bool HashKnown;
        public ulong Hash;
        public string Problem;
    }

    private sealed class Target
    {
        public PropertyBlockSetter Setter;
        public Texture2D Texture;
        public string CacheKey;
    }

    private static readonly Dictionary<string, Tex> Cache = new();
    /// <summary>Skin files by "&lt;car id&gt;|&lt;skin key&gt;".</summary>
    private static readonly Dictionary<string, SkinFile> SkinFiles = new(StringComparer.Ordinal);
    /// <summary>What each modded car's block should hold (by PropertyBlockSetter pointer), for the guard.</summary>
    private static readonly Dictionary<IntPtr, Target> Desired = new();
    private static readonly HashSet<SkinFile> PendingHashes = new();
    private static bool _guarding;
    private static float _next;

    private static StateMerchantMenu _merchant;

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        PumpReads();
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + TickEvery;
        foreach (var car in ModdedCars.All)
            if (car.Ready) car.Look.Refresh();
        var seen = new HashSet<IntPtr>();
        try
        {
            foreach (var fp in GameState.FusionPlayers)
            {
                // One car failing never takes the others' looks (or their livery guard) with it.
                IntPtr setterPtr = IntPtr.Zero;
                RuntimeCar car = null;
                try
                {
                    if (fp == null) continue;
                    var vcc = fp.GetComponent<VehicleCustomizationController>();
                    car = ModdedCars.ByCar(vcc);
                    if (car == null || !car.Ready) continue;
                    var setter = vcc.propertyBlockSetter;
                    if (setter != null) setterPtr = setter.Pointer;
                    bool mine = false;
                    int owner = 0;
                    try
                    {
                        mine = fp.HasInputAuthority;
                        owner = fp.Object.InputAuthority.RawEncoded;
                    }
                    catch (Exception ex) { DebugLog.Error("core", ex, "CarLooks.Tick"); }
                    var look = mine ? car.Look.Local : CarLookNet.Of(owner, car.Id);
                    CrashTrail.Mark(car.Id + ": look on a car");
                    // The livery mounted in the garage's Liveries tab, as the game syncs it: the car's bitset.
                    int livery = car.Liveries.Mounted(vcc);
                    Apply(car, setter, look, !mine, seen, "car", livery);
                    ApplyEngine(car, fp, look?.Engine ?? car.Def.DefaultEngine, seen);
                    if (DebugLog.IsVerbose) CarCosmeticsProbe.Check(car, fp.transform, "car");
                }
                catch (Exception e)
                {
                    if (setterPtr != IntPtr.Zero) seen.Add(setterPtr);
                    DebugLog.Error(car?.Id ?? "cars", e, "look on a car");
                    car?.LogOnce("look-car", "paint on one car: " + e.Message);
                }
            }
            try { ApplyDummy(seen); }
            catch (Exception e) { DebugLog.Error("core", e, "CarLooks.Tick"); Core.CorePlugin.Logger?.LogWarning("[Cars] paint on the garage model: " + e.Message); }
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "CarLooks.Tick");
            // The car list itself failed: keep every guard and texture as it is until the next tick.
            LogOnce("look", "[Cars] paint: " + e.Message);
            return;
        }

        // Forget cars that are gone (their textures may go when nothing else uses them).
        List<IntPtr> gone = null;
        foreach (var kv in Desired)
            if (!seen.Contains(kv.Key) || kv.Value.Setter == null) (gone ??= new List<IntPtr>()).Add(kv.Key);
        if (gone != null) foreach (var k in gone) Desired.Remove(k);
        List<IntPtr> goneEngines = null;
        foreach (var k in EngineApplied.Keys)
            if (!seen.Contains(k)) (goneEngines ??= new List<IntPtr>()).Add(k);
        if (goneEngines != null) foreach (var k in goneEngines) EngineApplied.Remove(k);
        Evict();
    }

    private static readonly HashSet<string> LoggedOnce = new();

    private static void LogOnce(string key, string text)
    {
        if (LoggedOnce.Add(key)) Core.CorePlugin.Logger?.LogWarning(text + " (further ones suppressed)");
    }

    private static void ApplyDummy(HashSet<IntPtr> seen)
    {
        var menu = _merchant;
        if (menu == null) return;
        GameObject go;
        try { go = menu.dummyChosenCarGameObject; }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarLooks.ApplyDummy"); _merchant = null; return; }
        if (go == null) return;
        RuntimeCar car = null;
        foreach (var c in ModdedCars.All)
            if (c.Ready && go.name.StartsWith(c.Def.PrefabName + "_Dummy", StringComparison.Ordinal)) { car = c; break; }
        if (car == null) return;
        PropertyBlockSetter setter = null;
        var lvcc = go.GetComponentInChildren<LocalVehicleCustomizationController>(true);
        if (lvcc != null) setter = lvcc.propertyBlockSetter;
        if (setter == null) setter = go.GetComponentInChildren<PropertyBlockSetter>(true);
        CrashTrail.Mark(car.Id + ": look on the garage model");
        Apply(car, setter, car.Look.Local, remote: false, seen, "garage model", car.Liveries.ForGarage(menu));
        if (DebugLog.IsVerbose) CarCosmeticsProbe.Check(car, go.transform, "garage model");
    }

    // ---------------------------------------------------------------- applying

    /// <summary>Puts the look's texture into the car's blocks (a look that isn't ready yet leaves the car as it is).
    /// <paramref name="livery"/>: the car's livery entry (0 = its paint).</summary>
    private static void Apply(RuntimeCar car, PropertyBlockSetter setter, CarLook look, bool remote, HashSet<IntPtr> seen, string what,
        int livery)
    {
        if (setter == null) return;
        seen.Add(setter.Pointer);
        if (!Resolve(car, look, remote, livery, out var tex, out string cacheKey, out string shown))
        {
            // Still being made. A car seen for the first time gets its own albedo meanwhile, so the base car's
            // livery texture (which the game sets when it mounts one of the car's livery parts) never shows on it.
            if (Desired.ContainsKey(setter.Pointer)) return;
            tex = null;
            cacheKey = null;
            shown = "standard paint (the look is still being made)";
        }
        var want = tex != null ? tex : car.Assets.Albedo;
        Desired[setter.Pointer] = new Target { Setter = setter, Texture = want, CacheKey = cacheKey };
        Texture2D have = null;
        try { have = setter.GetLiveryTexture(); } catch (Exception ex) { DebugLog.Error("core", ex, "CarLooks.Apply"); }
        if (have != null && have.Pointer == want.Pointer) return;
        CrashTrail.Mark(car.Id + ": set livery texture");
        if (DebugLog.IsVerbose) DebugLog.Verbose(car.Id, $"livery texture {want.name} on the {what} ({(remote ? "another player's" : "own")} look)");
        Set(setter, want);
        LogApplied(car, setter, look, want, what, remote, shown);
    }

    private static readonly HashSet<string> LoggedApplied = new();

    /// <summary>One info line per kind of target and look: what went on, to how many renderers, and whether the car's
    /// blocks took it (they only take it for car-shader materials with _IsSwitchable on).</summary>
    private static void LogApplied(RuntimeCar car, PropertyBlockSetter setter, CarLook look, Texture2D tex, string what, bool remote, string shown)
    {
        // Your own looks once each (a few, by hand); other players' once per kind of target (they may change often).
        if (LoggedApplied.Count >= 64) return;
        string desc = shown ?? (look == null ? "standard paint (no look from this player yet)" : "paint");
        if (look != null && look.HasSkin && !desc.StartsWith("skin", StringComparison.Ordinal)) desc += $" (skin {look.SkinKey} not here)";
        if (!car.Assets.HasZones) desc += " (no paint zones: standard paint)";
        if (!LoggedApplied.Add(car.Id + "|" + (remote ? what + "|another player's" : what + "|" + desc))) return;
        if (remote) desc = "another player's look (" + desc + ")";
        int renderers = 0, materials = 0;
        try
        {
            var map = setter.materialRenderers;
            if (map != null)
                foreach (var kv in map)
                {
                    materials++;
                    if (kv.Value != null) renderers += kv.Value.Count;
                }
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarLooks.LogApplied"); }
        bool took = false;
        try { var now = setter.GetLiveryTexture(); took = now != null && now.Pointer == tex.Pointer; } catch (Exception ex) { DebugLog.Error("core", ex, "CarLooks.LogApplied"); }
        car.Info($"look applied: {desc} to {renderers} renderer(s) / {materials} material(s) of the {what}" +
                 (took ? "." : " - but the car's blocks didn't take it (no switchable car-shader material?)."));
    }

    // ---------------------------------------------------------------- engine sound

    private static readonly Dictionary<IntPtr, string> EngineApplied = new();
    private static readonly HashSet<string> LoggedEngine = new();

    /// <summary>
    /// The car's engine sound: CarAudioManager picks the FMOD "VehicleType" label from its vehicleType (in Start, and
    /// for every engine sound it starts). A change sets the type, relabels, and stops the engine sound so the game
    /// starts it again with the new label on its next update.
    /// </summary>
    private static void ApplyEngine(RuntimeCar car, Component fp, byte engine, HashSet<IntPtr> seen)
    {
        var cam = fp.GetComponentInChildren<CarAudioManager>(true);
        if (cam == null) return;
        seen.Add(cam.Pointer);
        string label = car.Def.EngineLabel(engine);
        if (EngineApplied.TryGetValue(cam.Pointer, out string have) && have == label) return;
        if (!Enum.TryParse<CarAudioManager.VehicleType>(label, out var type)) return;
        EngineApplied[cam.Pointer] = label;
        if (cam.vehicleType == type) return;
        CrashTrail.Mark(car.Id + ": engine sound " + label);
        DebugLog.Verbose(car.Id, "engine sound " + label);
        cam.vehicleType = type;
        cam.SetupVehicleAudioParameter();
        var emitter = cam.engineAudioEmitter;
        if (emitter != null && emitter.IsPlaying()) emitter.Stop();
        if (LoggedEngine.Add(car.Id + "|" + label)) car.Info($"engine sound: {label}.");
    }

    private static void Set(PropertyBlockSetter setter, Texture2D tex)
    {
        _guarding = true;
        try { setter.SetLiveryTexture(tex); }
        finally { _guarding = false; }
    }

    /// <summary>The game set a livery texture on some car: on a modded car, anything but its own goes straight back.</summary>
    internal static void AfterGameSetLivery(PropertyBlockSetter setter, Texture2D tex)
    {
        if (_guarding || setter == null || !Desired.TryGetValue(setter.Pointer, out var t) || t.Texture == null) return;
        if (tex != null && tex.Pointer == t.Texture.Pointer) return;
        Set(setter, t.Texture);
    }

    /// <summary>
    /// The texture for a look: true with the texture (null = the base albedo) when it can be shown now, false while it
    /// is still being made. In the game's order: a skin (a mod livery) first, when this game has a skin of that key whose
    /// file has the same hash (for your own look: the file you picked); then the livery mounted in the garage's Liveries
    /// tab (<paramref name="livery"/>, the car's entry; 0 = its paint); then the look's colours. A skin or livery with
    /// transparency is laid over the look's colours. <paramref name="shown"/> says which one it was, for the log.
    /// </summary>
    private static bool Resolve(RuntimeCar car, CarLook look, bool remote, int livery, out Texture2D tex, out string cacheKey, out string shown)
    {
        tex = null;
        cacheKey = null;
        shown = null;
        var main = look?.Main ?? DefaultMain(car);
        var trim = look?.Trim ?? DefaultTrim(car, main);
        if (look != null && look.HasSkin)
        {
            var sf = SkinFileFor(car, look.SkinKey);
            if (sf != null && sf.Problem == null)
            {
                if (!sf.HashKnown) { if (!remote) return false; } // ours: wait for it; theirs: colours meanwhile
                else if (!remote || sf.Hash == look.SkinHash)
                {
                    shown = "skin " + look.SkinKey;
                    switch (ImageLook(car, look.SkinKey, sf, main, trim, remote, out tex, out cacheKey))
                    {
                        case 1: return true;
                        case 0: return false;
                    }
                    // the file failed: the livery or the colours below
                }
            }
        }
        var entry = livery > 0 ? car.Liveries[livery] : null;
        if (entry?.File != null)
        {
            var sf = entry.File;
            if (sf.Read == null && sf.Problem == null) StartRead(sf);
            shown = "livery " + entry.Name + (look != null ? $" over paint {main.ToHex()} trim {trim.ToHex()}" : "");
            if (sf.Problem == null && sf.HashKnown)
            {
                switch (ImageLook(car, entry.ImageKey, sf, main, trim, remote, out tex, out cacheKey))
                {
                    case 1: return true;
                    case 0: return false;
                }
            }
            else if (sf.Problem == null) return false; // read in a moment
            else car.LogOnce("livery-file" + entry.Id, $"livery {entry.Name} can't be shown: {sf.Problem}");
        }
        shown = look == null ? null : $"paint {main.ToHex()} trim {trim.ToHex()}";
        tex = null;
        if (look == null) { cacheKey = null; return true; } // a player whose look we don't know: the car's own paint
        if (!car.Assets.PaintPixels(out _, out _, out _, out _, out _)) { cacheKey = null; return true; }
        cacheKey = $"c:{car.Id}:" + main.ToHex() + trim.ToHex();
        return ColourTexture(car, cacheKey, main, trim, remote, out tex);
    }

    private static CarRgb DefaultMain(RuntimeCar car) => car.Def.Paints[0].Rgb;

    private static CarRgb DefaultTrim(RuntimeCar car, CarRgb main) =>
        CarPaint.TrimColour(car.Def.Trims, car.Def.Trims[0].Name, car.Def.TrimCustomDefault, main);

    /// <summary>Images whose PNG has transparency (by image key "car:source:hash"): they are laid over the paint.</summary>
    private static readonly HashSet<string> Transparent = new(StringComparer.Ordinal);

    private sealed class Decoded
    {
        public byte[] Pixels;
        public float LastUsed;
    }

    /// <summary>The decoded pixels of transparent images, kept while overlays are made from them (dropped a minute
    /// after the last use; decoded again when needed).</summary>
    private static readonly Dictionary<string, Decoded> DecodedImages = new(StringComparer.Ordinal);

    /// <summary>
    /// A skin's or livery's image as the car's texture: as it is when it is opaque (the game's way: it carries its
    /// colours), laid over the paint colours when it has transparency. 1: ready, 0: still being made, -1: failed.
    /// <paramref name="source"/> names the image (a skin key; a livery's image shares its bundled skin's key).
    /// </summary>
    private static int ImageLook(RuntimeCar car, string source, SkinFile sf, CarRgb main, CarRgb trim, bool remote, out Texture2D tex, out string cacheKey)
    {
        string img = $"{car.Id}:{source}:{sf.Hash:x16}";
        if (Transparent.Contains(img))
        {
            cacheKey = $"o:{img}:{main.ToHex()}{trim.ToHex()}";
            return OverlayTexture(car, cacheKey, img, main, trim, remote, out tex);
        }
        cacheKey = "s:" + img;
        return SkinTexture(car, cacheKey, img, sf, remote, out tex);
    }

    /// <summary>1: ready (in <paramref name="tex"/>), 0: still loading, -1: failed. An image found to have transparency
    /// is not kept as a texture: its pixels wait for <see cref="OverlayTexture"/> (0 this time).</summary>
    private static int SkinTexture(RuntimeCar car, string cacheKey, string img, SkinFile sf, bool remote, out Texture2D tex)
    {
        tex = null;
        if (Cache.TryGetValue(cacheKey, out var c))
        {
            if (c.Texture != null) { c.LastUsed = Time.unscaledTime; tex = c.Texture; return 1; }
            if (c.Failed != null) return -1;
        }
        else if (!Room("s:", SkinCap, remote))
        {
            LogOnce("skin-cap", $"[Cars] {SkinCap} skin and livery textures are in use: another player's shows as their colours.");
            return -1;
        }
        // The bytes are kept from reading the file until the texture is made, then dropped (read again if evicted).
        var r = sf.Read;
        if (r == null || r.IsCompleted && (r.IsFaulted || r.Result.Bytes == null))
        {
            StartRead(sf);
            return 0;
        }
        if (!r.IsCompleted) return 0;
        var read = r.Result;
        Texture2D t = null;
        string failed = null;
        bool transparent = false;
        try
        {
            t = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { name = car.Def.CarName + "_skin", ignoreMipmapLimit = true };
            // Readable for now: its alpha is checked before the pixels go to the GPU only.
            if (!ImageConversion.LoadImage(t, new Il2CppStructArray<byte>(read.Bytes), false)) failed = "the PNG could not be decoded";
            else if (t.width != read.Side || t.height != read.Side) failed = "the PNG decoded to another size";
            else
            {
                var px = t.GetPixels32();
                var span = MemoryMarshal.AsBytes(px.AsSpan());
                if (!CarPaint.IsOpaque(span) && car.Assets.PaintPixels(out _, out _, out int pw, out int ph, out _) && pw == t.width && ph == t.height)
                {
                    transparent = true;
                    DecodedImages[img] = new Decoded { Pixels = span.ToArray(), LastUsed = Time.unscaledTime };
                }
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarLooks.SkinTexture"); failed = e.Message; }
        read.Bytes = null;
        sf.Read = Task.FromResult(read); // keeps the hash, drops the bytes
        if (failed != null)
        {
            if (t != null) Object.Destroy(t);
            sf.Problem = failed;
            car.LogOnce("skin-load" + cacheKey, $"skin {sf.Path}: {failed}");
            Cache[cacheKey] = new Tex { Key = cacheKey, Failed = failed, Car = car };
            return -1;
        }
        if (transparent)
        {
            Object.Destroy(t);
            Transparent.Add(img);
            if (DebugLog.IsVerbose) DebugLog.Verbose(car.Id, $"{sf.Path} has transparency: laid over the paint");
            return 0;
        }
        t.Apply(false, true); // the pixels live on the GPU only from here
        CarAssets.Sampling(t, full: true);
        t.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Cache[cacheKey] = new Tex { Key = cacheKey, Texture = t, LastUsed = Time.unscaledTime, Car = car };
        tex = t;
        return 1;
    }

    /// <summary>A transparent skin or livery over a colour pair, made off the main thread like the colours (the paint
    /// recoloured, then the image laid over it). 1: ready, 0: still being made, -1: failed or no room.</summary>
    private static int OverlayTexture(RuntimeCar car, string cacheKey, string img, CarRgb main, CarRgb trim, bool remote, out Texture2D tex)
    {
        tex = null;
        float now = Time.unscaledTime;
        if (DecodedImages.TryGetValue(img, out var dec)) dec.LastUsed = now;
        if (Cache.TryGetValue(cacheKey, out var c))
        {
            c.LastUsed = now;
            if (c.Texture != null) { tex = c.Texture; return 1; }
            if (c.Failed != null) return -1;
            if (c.Build == null || !c.Build.IsCompleted) return 0;
            return FinishBuild(car, c, "_overlay", out tex) ? 1 : -1;
        }
        if (dec == null)
        {
            Transparent.Remove(img); // decoded again (and found transparent again) on the next tick
            return 0;
        }
        if (!Room("o:", OverlayCap, remote))
        {
            LogOnce("overlay-cap", $"[Cars] {OverlayCap} livery-over-paint textures are in use: another player's shows as their colours.");
            return -1;
        }
        if (!car.Assets.PaintPixels(out var basePx, out var zones, out int w, out int h, out _) || dec.Pixels.Length != basePx.Length) return -1;
        var px = dec.Pixels;
        c = new Tex { Key = cacheKey, W = w, H = h, LastUsed = now, Car = car };
        c.Build = Task.Run(() =>
        {
            var o = new byte[basePx.Length];
            CarPaint.Recolour(basePx, zones, main, trim, o);
            CarPaint.Overlay(o, px, o);
            return o;
        });
        Cache[cacheKey] = c;
        return 0;
    }

    /// <summary>Makes the texture of a finished build (colours or an overlay). False when the build failed.</summary>
    private static bool FinishBuild(RuntimeCar car, Tex c, string suffix, out Texture2D tex)
    {
        tex = null;
        try
        {
            if (c.Build.IsFaulted) throw c.Build.Exception?.GetBaseException() ?? new InvalidOperationException("the build failed");
            var px = c.Build.Result;
            var t = new Texture2D(c.W, c.H, TextureFormat.RGBA32, true, false) { name = car.Def.CarName + suffix, ignoreMipmapLimit = true };
            var arr = new Il2CppStructArray<Color32>(c.W * (long)c.H);
            px.AsSpan().CopyTo(MemoryMarshal.AsBytes(arr.AsSpan()));
            t.SetPixels32(arr);
            t.Apply(true, true); // mipmaps, then the pixels live on the GPU only
            CarAssets.Sampling(t, full: true);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            c.Texture = t;
            tex = t;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "CarLooks.FinishBuild");
            c.Failed = e.Message;
            car.LogOnce("paint-build" + suffix, "could not make the paint texture: " + e.Message);
        }
        c.Build = null;
        return tex != null;
    }

    private static bool ColourTexture(RuntimeCar car, string cacheKey, CarRgb main, CarRgb trim, bool remote, out Texture2D tex)
    {
        tex = null;
        if (!Cache.TryGetValue(cacheKey, out var c))
        {
            if (!Room("c:", ColourCap, remote))
            {
                LogOnce("colour-cap", $"[Cars] {ColourCap} paint textures are in use: another player's new colours show as the standard paint.");
                return true; // the base albedo
            }
            car.Assets.PaintPixels(out var basePx, out var zones, out int w, out int h, out _);
            c = new Tex { Key = cacheKey, W = w, H = h, LastUsed = Time.unscaledTime, Car = car };
            c.Build = Task.Run(() =>
            {
                var o = new byte[basePx.Length];
                CarPaint.Recolour(basePx, zones, main, trim, o);
                return o;
            });
            Cache[cacheKey] = c;
            return false;
        }
        c.LastUsed = Time.unscaledTime;
        if (c.Texture != null) { tex = c.Texture; return true; }
        if (c.Failed != null) return true; // base albedo
        if (c.Build == null || !c.Build.IsCompleted) return false;
        FinishBuild(car, c, "_paint", out tex); // failed: the base albedo
        return true;
    }

    /// <summary>The skin file of a key for a car in this game's scan (null when there is none), its hash read in the
    /// background.</summary>
    internal static SkinFile SkinFileFor(RuntimeCar car, string key)
    {
        string fileKey = car.Id + "|" + key;
        var skin = Api.Cars.FindSkin(car.Id, key);
        if (skin == null)
        {
            SkinFiles.Remove(fileKey);
            return null;
        }
        if (SkinFiles.TryGetValue(fileKey, out var sf) && sf.Path == skin.TexturePath) return sf;
        sf = new SkinFile { Path = skin.TexturePath, PackDir = skin.PackDir };
        var embedded = skin.Embedded;
        if (embedded != null)
        {
            // A livery the car mod bundles (listed as a skin too): checked like a file's bytes.
            sf.Reader = () =>
            {
                try
                {
                    var bytes = embedded();
                    if (bytes == null) return new SkinRead { Problem = "it is not in the car mod" };
                    string problem = CarSkinScan.CheckPng(bytes, out int side);
                    return problem != null ? new SkinRead { Problem = problem } : new SkinRead { Bytes = bytes, Hash = CarSkinScan.Hash8(bytes), Side = side };
                }
                catch (Exception e) { DebugLog.Error("core", e, "CarLooks.SkinFileFor"); return new SkinRead { Problem = e.Message }; }
            };
        }
        SkinFiles[fileKey] = sf;
        StartRead(sf);
        return sf;
    }

    internal static void StartRead(SkinFile sf)
    {
        string path = sf.Path, packDir = sf.PackDir;
        var reader = sf.Reader;
        sf.Read = Task.Run(() => reader != null ? reader() : ReadSkin(packDir, path));
        // The hash is read from the task on the next tick (main thread).
        PendingHashes.Add(sf);
    }

    /// <summary>Reads and checks a skin file (worker thread): confined to its pack right before reading, a square PNG of
    /// an allowed size, at most 16 MB.</summary>
    private static SkinRead ReadSkin(string packDir, string path)
    {
        try
        {
            string problem = CarSkinScan.CheckFile(packDir, path, out _, out _);
            if (problem != null) return new SkinRead { Problem = problem };
            var bytes = File.ReadAllBytes(path);
            problem = CarSkinScan.CheckPng(bytes, out int side);
            if (problem != null) return new SkinRead { Problem = problem };
            return new SkinRead { Bytes = bytes, Hash = CarSkinScan.Hash8(bytes), Side = side };
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarLooks.ReadSkin"); return new SkinRead { Problem = "it could not be read (" + e.Message + ")" }; }
    }

    /// <summary>Moves finished reads' hashes (or problems) onto their skin files. Main thread, every frame (cheap).</summary>
    public static void PumpReads()
    {
        if (PendingHashes.Count == 0) return;
        List<SkinFile> done = null;
        foreach (var sf in PendingHashes)
        {
            var r = sf.Read;
            if (r == null || !r.IsCompleted) continue;
            (done ??= new List<SkinFile>()).Add(sf);
            if (r.IsFaulted) { sf.Problem = "it could not be read"; continue; }
            var res = r.Result;
            res.DoneAt = Time.unscaledTime;
            if (res.Problem != null)
            {
                sf.Problem = res.Problem;
                LogOnce("skin-read" + sf.Path, $"[Cars] skin {sf.Path} can't be used: {res.Problem}");
                continue;
            }
            if (sf.HashKnown && sf.Hash != res.Hash)
            {
                // The file changed since it was first read: the old texture goes with its old key.
                foreach (var key in new List<string>(Cache.Keys))
                    if (key.EndsWith($":{sf.Hash:x16}", StringComparison.Ordinal)) Drop(key);
            }
            sf.Hash = res.Hash;
            sf.HashKnown = true;
        }
        if (done != null) foreach (var sf in done) PendingHashes.Remove(sf);
    }

    // ---------------------------------------------------------------- cache

    private static void Evict()
    {
        if (Cache.Count == 0) return;
        var used = InUse();
        float now = Time.unscaledTime;
        List<string> drop = null;
        foreach (var c in Cache.Values)
        {
            if (used.Contains(c.Key) || c.Build != null) continue;
            if (now - c.LastUsed > UnusedLife || c.Failed != null && now - c.LastUsed > 5f) (drop ??= new List<string>()).Add(c.Key);
        }
        if (drop != null) foreach (var k in drop) Drop(k);
        // Decoded transparent images no overlay was made from for a minute (16 MB each at 2048).
        List<string> gone = null;
        foreach (var kv in DecodedImages)
            if (now - kv.Value.LastUsed > UnusedLife) (gone ??= new List<string>()).Add(kv.Key);
        if (gone != null) foreach (var k in gone) DecodedImages.Remove(k);
        // Skin bytes read but never made into a texture (e.g. the remote's hash differed): dropped after 10 s.
        foreach (var sf in SkinFiles.Values)
        {
            var r = sf.Read;
            if (r == null || !r.IsCompleted || r.IsFaulted) continue;
            var res = r.Result;
            if (res.Bytes != null && res.DoneAt >= 0f && now - res.DoneAt > 10f) res.Bytes = null;
        }
    }

    private static HashSet<string> InUse()
    {
        var used = new HashSet<string>();
        foreach (var t in Desired.Values) if (t.CacheKey != null) used.Add(t.CacheKey);
        return used;
    }

    /// <summary>
    /// Room for one more texture of a kind ("c:" colours, "s:" skins): under its cap, or after dropping the oldest of
    /// that kind no car uses. Your own look always gets room (it may go one over).
    /// </summary>
    private static bool Room(string kind, int cap, bool remote)
    {
        int n = 0;
        foreach (var k in Cache.Keys) if (k.StartsWith(kind, StringComparison.Ordinal)) n++;
        if (n < cap) return true;
        var used = InUse();
        Tex oldest = null;
        foreach (var c in Cache.Values)
            if (c.Key.StartsWith(kind, StringComparison.Ordinal) && !used.Contains(c.Key) && c.Build == null &&
                (oldest == null || c.LastUsed < oldest.LastUsed)) oldest = c;
        if (oldest != null) { Drop(oldest.Key); return true; }
        return !remote;
    }

    private static void Drop(string key)
    {
        if (!Cache.TryGetValue(key, out var c)) return;
        Cache.Remove(key);
        if (c.Texture != null) Object.Destroy(c.Texture);
    }

    /// <summary>The skins were scanned again: forget what was read (files may have changed).</summary>
    public static void SkinsRescanned()
    {
        SkinFiles.Clear();
        PendingHashes.RemoveWhere(sf => !sf.Owned); // the cars' own liveries didn't change
    }

    // ---------------------------------------------------------------- the game's garage

    internal static void MerchantDummy(StateMerchantMenu menu)
    {
        _merchant = menu;
        _next = 0f; // apply on the next tick
    }

    /// <summary>The garage's Livery Mods tab unequipped a modded car's mod livery: its skin comes off too.</summary>
    internal static void GameUnequipped(string carName)
    {
        var car = ModdedCars.ByCarName(carName);
        if (car == null || car.Config.Skin == null || string.IsNullOrEmpty(car.Config.Skin.Value)) return;
        car.Info($"skin {car.Config.Skin.Value} taken off in the garage.");
        car.Config.Skin.Value = "";
    }

    /// <summary>
    /// The garage's Livery Mods tab equipped a mod livery for a modded car (the game lists livery entries for any car
    /// name there): that entry (a local pack's, or an installed mod.io livery's) becomes the car's skin, when this game
    /// found it as one.
    /// </summary>
    internal static void GameEquipped(string carName, ModLivery livery)
    {
        var car = ModdedCars.ByCarName(carName);
        if (livery == null || car == null || car.Config.Skin == null) return;
        string file = livery.TextureFileName;
        long modId = livery.ModId;
        foreach (var s in Api.Cars.SkinsFor(car.Id))
        {
            if (s.Source == Api.Cars.SkinSource.Bundled) continue;
            if ((s.Source == Api.Cars.SkinSource.ModIo ? s.ModId : LiveryPackScan.PackId(s.Pack)) != modId) continue;
            if (!string.Equals(Path.GetFileName(s.TexturePath), file, StringComparison.OrdinalIgnoreCase)) continue;
            car.Config.Skin.Value = s.Key;
            car.Info($"skin {s.Key} picked in the garage.");
            return;
        }
    }
}
