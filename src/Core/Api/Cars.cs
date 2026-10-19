using System;
using System.Collections.Generic;
using System.IO;
using OverTheHillPlus.Shared;

namespace OverTheHillPlus.Api;

/// <summary>
/// Skins for modded cars (cars a mod adds to the game, like the Vera Crossover). A modded car registers its id, its
/// display name, the car name livery packs use for it, and two callbacks (which skin it wears now, and wear this one);
/// then anyone (the Livery Packs page, another mod) can list the registered cars and the skins found for each, and
/// switch a car's skin. See docs/modding-api.md.
///
/// <para>A skin is an entry of a livery pack in the game's own format (a folder with <c>manifest.json</c>
/// <c>{"liveries":[{"car","name","texture","icon"}]}</c> and the PNG textures) whose <c>car</c> is the modded car's
/// livery name (any case). Packs are read from the game's LocalLiveries folder and BepInEx/config/oth.liveries/, with
/// the same folder confinement as Livery Packs, and each texture must be a square PNG of 1024, 2048 or 4096 pixels
/// and at most 16 MB. A skin's key is "&lt;pack folder&gt;/&lt;entry name&gt;". Loading and showing the texture is the
/// car mod's own business (the game doesn't know the car).</para>
///
/// <para>Main thread only. The folders are read on the first <see cref="SkinsFor"/> and on <see cref="Rescan"/>, never
/// every frame.</para>
/// </summary>
public static class Cars
{
    /// <summary>What a modded car registers.</summary>
    public sealed class ModdedCar
    {
        /// <summary>2 to 40 characters from a-z, 0-9, '_', '.' and '-' (usually your mod id).</summary>
        public string Id;
        /// <summary>Shown in menus, e.g. "Vera Crossover".</summary>
        public string DisplayName;
        /// <summary>The <c>car</c> value of this car's entries in livery manifests, e.g. "Vera" (letters, digits, '_').</summary>
        public string LiveryCar;
        /// <summary>The key of the skin the car wears now; null or "" for none (its own paint).</summary>
        public Func<string> CurrentSkin;
        /// <summary>Wear this skin (a key from <see cref="SkinsFor"/>), or none for null / "". Save the choice yourself.</summary>
        public Action<string> ApplySkin;
    }

    /// <summary>A skin found for a modded car.</summary>
    public sealed class Skin
    {
        /// <summary>"&lt;pack folder&gt;/&lt;entry name&gt;".</summary>
        public string Key { get; internal set; }
        /// <summary>The entry's name (the texture's file name when it has none).</summary>
        public string Name { get; internal set; }
        /// <summary>The pack's folder name.</summary>
        public string Pack { get; internal set; }
        /// <summary>The PNG's full path (checked inside its pack when scanned; check again before you read it).</summary>
        public string TexturePath { get; internal set; }
        /// <summary>Width and height in pixels.</summary>
        public int Side { get; internal set; }
        public long Bytes { get; internal set; }
        /// <summary>The pack's folder (what the texture must stay inside).</summary>
        internal string PackDir { get; set; }
        /// <summary>Where it comes from: a livery pack folder, the car mod itself (bundled), or an installed mod.io mod.</summary>
        public SkinSource Source { get; internal set; }
        /// <summary>The mod.io mod id of a mod.io skin (0 otherwise).</summary>
        public long ModId { get; internal set; }
        /// <summary>A bundled skin's bytes (instead of a file).</summary>
        internal Func<byte[]> Embedded { get; set; }

        /// <summary>Where it comes from, for menus: the pack's folder, "built in" or "mod.io".</summary>
        public string From => Source switch { SkinSource.Bundled => "built in", SkinSource.ModIo => "mod.io", _ => Pack };
    }

    public enum SkinSource
    {
        /// <summary>A livery pack in the game's LocalLiveries folder or BepInEx/config/oth.liveries/.</summary>
        Pack,
        /// <summary>A livery the car's mod bundles (it is also in the garage's Liveries tab).</summary>
        Bundled,
        /// <summary>An installed mod.io livery whose manifest names the car (the garage's Livery Mods tab lists it).</summary>
        ModIo,
    }

    private static readonly Dictionary<string, List<(string name, Func<byte[]> read)>> Bundled = new(StringComparer.OrdinalIgnoreCase);
    private static bool _modIoChanged;

    /// <summary>A modded car's bundled liveries, listed as its skins too (core's car framework, at start).</summary>
    internal static void SetBundled(string carId, List<(string name, Func<byte[]> read)> liveries)
    {
        if (string.IsNullOrEmpty(carId)) return;
        if (liveries == null || liveries.Count == 0) Bundled.Remove(carId);
        else Bundled[carId] = liveries;
        SkinCache.Remove(carId);
    }

    /// <summary>The game's mod livery list changed (a mod.io livery installed or removed, a refresh): the skins are
    /// listed again on the next tick (<see cref="RefreshIfChanged"/>).</summary>
    internal static void ModLiveriesChanged() => _modIoChanged = true;

    /// <summary>Once a tick (core's car framework): lists the skins again after the game's mod livery list changed.</summary>
    internal static void RefreshIfChanged()
    {
        if (!_modIoChanged) return;
        _modIoChanged = false;
        SkinCache.Clear();
        RaiseRescanned();
    }

    private static readonly CarRegistry Registry = new();
    private static CarSkinScanResult _scan;
    private static readonly Dictionary<string, IReadOnlyList<Skin>> SkinCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a modded car (once, from your Load). False (and logged) when refused: an invalid id, a
    /// livery name that isn't letters / digits / '_' or is a stock car's (e.g. "TrailBurner"), a missing callback, or an
    /// id or livery name already taken.</summary>
    public static bool Register(ModdedCar car)
    {
        string problem = car == null
            ? "no car given"
            : Registry.Add(new CarRegistry.Entry
            {
                Id = car.Id, DisplayName = car.DisplayName, LiveryCar = car.LiveryCar, CurrentSkin = car.CurrentSkin, ApplySkin = car.ApplySkin,
            });
        if (problem != null)
        {
            Mods.Log("Cars.Register: " + problem);
            return false;
        }
        // The scan only looks at registered cars' entries: a car registered after a scan needs a fresh one.
        _scan = null;
        SkinCache.Clear();
        return true;
    }

    /// <summary>Takes a registered car out again (core's car framework, when the car can't start after all).</summary>
    internal static void Unregister(string id)
    {
        if (!Registry.Remove(id)) return;
        _scan = null;
        SkinCache.Clear();
    }

    /// <summary>The registered modded cars (copies: changing them changes nothing).</summary>
    public static IReadOnlyList<ModdedCar> Registered
    {
        get
        {
            var list = new List<ModdedCar>(Registry.All.Count);
            foreach (var c in Registry.All)
                list.Add(new ModdedCar { Id = c.Id, DisplayName = c.DisplayName, LiveryCar = c.LiveryCar, CurrentSkin = c.CurrentSkin, ApplySkin = c.ApplySkin });
            return list;
        }
    }

    /// <summary>The skins found for a registered car (empty for an unknown id): its bundled liveries first (in their
    /// order), then the packs' sorted by pack then name, then installed mod.io liveries for its car name.</summary>
    public static IReadOnlyList<Skin> SkinsFor(string carId)
    {
        var car = Registry.Find(carId);
        if (car == null) return Array.Empty<Skin>();
        if (SkinCache.TryGetValue(car.Id, out var cached)) return cached;
        var scan = Scan();
        var list = new List<Skin>();
        foreach (var s in scan.For(car.LiveryCar))
            list.Add(new Skin { Key = s.Key, Name = s.Name, Pack = s.Pack, TexturePath = s.TexturePath, Side = s.Side, Bytes = s.Bytes, PackDir = s.PackDir });
        list.Sort((a, b) =>
        {
            int c = string.Compare(a.Pack, b.Pack, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in list) keys.Add(s.Key);
        if (Bundled.TryGetValue(car.Id, out var bundled))
        {
            var own = new List<Skin>();
            foreach (var (name, read) in bundled)
            {
                string key = OverTheHillPlus.Cars.CarLiveries.SkinKey(car.Id, name);
                if (!CarSkinKey.IsValid(key) || !keys.Add(key)) continue;
                own.Add(new Skin
                {
                    Key = key, Name = name.Trim(), Pack = "*" + car.Id, TexturePath = "embedded:" + key, Side = 2048, Source = SkinSource.Bundled,
                    Embedded = read,
                });
            }
            list.InsertRange(0, own);
        }
        AddModIo(car, list, keys);
        SkinCache[car.Id] = list;
        return list;
    }

    /// <summary>
    /// Installed mod.io liveries whose manifest names this car (the game keeps them by car name, so the garage's Livery
    /// Mods tab lists them for it): each is a skin with key "*mod.io &lt;mod id&gt;/&lt;name&gt;", its texture checked
    /// like a pack's (a square PNG of an allowed size, inside its folder). Local liveries are left out: those are the
    /// packs' (scanned above). Players who have the same mod.io livery installed see it on your car.
    /// </summary>
    private static void AddModIo(CarRegistry.Entry car, List<Skin> list, HashSet<string> keys)
    {
        try
        {
            var service = global::ModManager.Instance?.Liveries;
            if (service == null) return;
            var found = service.GetLiveriesForCar(car.LiveryCar);
            if (found == null) return;
            // The interop interface has the indexer but not Count (that is IReadOnlyCollection's).
            var asList = found.TryCast<Il2CppSystem.Collections.Generic.List<ModLivery>>();
            int n = asList != null ? asList.Count : new Il2CppSystem.Collections.Generic.IReadOnlyCollection<ModLivery>(found.Pointer).Count;
            for (int i = 0; i < n; i++)
            {
                var l = found[i];
                if (l == null || l.IsLocal) continue;
                string path = l.TexturePath;
                if (string.IsNullOrEmpty(path)) continue;
                string name = string.IsNullOrWhiteSpace(l.DisplayName) ? Path.GetFileNameWithoutExtension(path) : l.DisplayName.Trim();
                string key = CarSkinKey.Make("*mod.io " + l.ModId, name);
                if (key == null || !keys.Add(key)) continue;
                string dir = Path.GetDirectoryName(path);
                string problem = CarSkinScan.CheckFile(dir, path, out long bytes, out int side);
                if (problem != null)
                {
                    if (Logged.Add("modio|" + key)) Core.CorePlugin.Logger?.LogInfo($"[Cars] skipped mod.io livery '{name}' for {car.LiveryCar}: {problem}");
                    continue;
                }
                list.Add(new Skin
                {
                    Key = key, Name = name, Pack = "*mod.io " + l.ModId, TexturePath = path, PackDir = dir, Side = side, Bytes = bytes,
                    Source = SkinSource.ModIo, ModId = l.ModId,
                });
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "Cars.AddModIo"); }
    }

    /// <summary>The skin with this key for a registered car, or null.</summary>
    public static Skin FindSkin(string carId, string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var s in SkinsFor(carId))
            if (string.Equals(s.Key, key, StringComparison.Ordinal)) return s;
        return null;
    }

    /// <summary>Reads the pack folders again (after packs were added or changed), then raises <see cref="Rescanned"/>.</summary>
    public static void Rescan()
    {
        _scan = null;
        SkinCache.Clear();
        Scan();
        RaiseRescanned();
    }

    private static void RaiseRescanned()
    {
        var hooks = Rescanned;
        if (hooks == null) return;
        foreach (Action h in hooks.GetInvocationList())
        {
            try { h(); }
            catch (Exception e) { DebugLog.Error("core", e, "Cars.Rescan"); Mods.Log("Cars.Rescanned handler threw: " + e.Message); }
        }
    }

    /// <summary>After <see cref="Rescan"/>: skins may have changed (files you read before may differ now).</summary>
    public static event Action Rescanned;

    /// <summary>The key of the skin a registered car wears now (null for none or an unknown id).</summary>
    public static string CurrentSkin(string carId)
    {
        var car = Registry.Find(carId);
        if (car == null) return null;
        try
        {
            string k = car.CurrentSkin();
            return string.IsNullOrEmpty(k) ? null : k;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "Cars.CurrentSkin");
            Mods.Log($"Cars: \"{car.Id}\" CurrentSkin threw: {e.Message}");
            return null;
        }
    }

    /// <summary>Puts a skin on a registered car (null or "" for none). False for an unknown car, a key that isn't one
    /// of <see cref="SkinsFor"/>, or a callback that threw.</summary>
    public static bool ApplySkin(string carId, string key)
    {
        var car = Registry.Find(carId);
        if (car == null) return false;
        if (!string.IsNullOrEmpty(key) && FindSkin(car.Id, key) == null) return false;
        try
        {
            car.ApplySkin(string.IsNullOrEmpty(key) ? null : key);
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "Cars.ApplySkin");
            Mods.Log($"Cars: \"{car.Id}\" ApplySkin threw: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Adds a whole car the game doesn't have, built by the Mod Manager's car framework from
    /// <paramref name="definition"/> (see docs/modding-api.md, "Adding a car"): the network prefab, the garage entry,
    /// the save filter, the join rules, the tuning, the colours, skins and engine sound and their sync. Call it once from
    /// your Load, after <see cref="Mods.Register"/> (with <c>Side = ModSide.Server</c>) and <see cref="Mods.IsEnabled"/>.
    /// <paramref name="assets"/> holds the model as embedded resources "&lt;AssetName&gt;/&lt;AssetName&gt;.mesh",
    /// "..._albedo_base.png", "_ao.png", "_lights.png", "_mask.png" (and the optional "_zones.png", "_icon.png", "_picture.png": the picture in the garage and car lists, 512 x 512, shaded side view, front to the right).
    /// Null when the car starts, otherwise why not (logged, and your mod is marked failed with it).
    /// <para>Every car needs a key in the Mod Manager's fixed network prefab order (every player's game must add the
    /// same cars in the same order), and that list ships with the Mod Manager: a definition whose RegistryKey isn't in
    /// it is refused. Ask for a key to be added in a Mod Manager release.</para>
    /// </summary>
    public static string AddCar(OverTheHillPlus.Cars.CarDefinition definition, System.Reflection.Assembly assets,
        BepInEx.Logging.ManualLogSource log = null)
    {
        string problem = definition == null ? "no car definition" : ApiGuard.Problem(definition.Id);
        var mod = problem == null ? Core.Manager.ModManager.Find(definition.Id) : null;
        if (problem == null && mod == null) problem = $"\"{definition.Id}\" is not registered (call Mods.Register first)";
        if (problem == null && mod.Side != Core.Manager.ModSide.Server)
            problem = $"\"{definition.Id}\" must be registered with Side = ModSide.Server (everyone in the session needs a car mod)";
        if (problem != null)
        {
            Mods.Log("Cars.AddCar: " + problem);
            return problem;
        }
        return OverTheHillPlus.Cars.ModdedCars.Start(definition, assets, Core.Manager.ModManager.ConfigFor(definition.Id), log);
    }

    /// <summary>The car's rows for your page (its state, "Drive the &lt;car&gt;", the garage switch, paint, trim, engine
    /// sound and skin): call it from your ModInfo.DrawSimple.</summary>
    public static void DrawCarRows(OverTheHillPlus.Cars.CarDefinition definition) => OverTheHillPlus.Cars.ModdedCars.DrawPage(definition);

    /// <summary>Where skins are looked for, in order (a pack folder name found in an earlier one wins).</summary>
    public static IReadOnlyList<string> SkinFolders
    {
        get
        {
            var list = new List<string>();
            string local = null;
            try { local = ModLiveryService.GetLocalLiveriesDirectory(); } catch (Exception ex) { DebugLog.Error("core", ex, "Cars.SkinFolders"); }
            if (string.IsNullOrEmpty(local))
            {
                try
                {
                    local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "over the hill", "LocalLiveries");
                }
                catch (Exception ex) { DebugLog.Error("core", ex, "Cars.SkinFolders"); }
            }
            if (!string.IsNullOrEmpty(local)) list.Add(local);
            list.Add(Path.Combine(BepInEx.Paths.ConfigPath, "oth.liveries"));
            return list;
        }
    }

    private static CarSkinScanResult Scan()
    {
        if (_scan != null) return _scan;
        var cars = new List<string>();
        foreach (var c in Registry.All) cars.Add(c.LiveryCar);
        try { _scan = CarSkinScan.Scan(SkinFolders, cars); }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "Cars.Scan");
            Mods.Log("Cars: the skin scan failed: " + e.Message);
            _scan = new CarSkinScanResult();
        }
        // One line per pack or skin left out, once per session (a rescan doesn't repeat it).
        foreach (string line in _scan.Skipped)
            if (Logged.Add(line)) Core.CorePlugin.Logger?.LogInfo("[Cars] skipped " + line);
        return _scan;
    }

    private static readonly HashSet<string> Logged = new();
}
