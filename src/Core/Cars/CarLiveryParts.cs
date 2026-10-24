using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Shared;
using UnityEngine;
using ItemType = InventoryItemScriptableObject.ItemType;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cars;

/// <summary>
/// A modded car's liveries as the game's own livery parts (<see cref="CarLiveries"/>): the garage's Liveries tab lists,
/// buys (free) and mounts them like a stock car's, and the game sends the mounted one to other players in its
/// mounted-parts bitset. Each entry is a copy of one of the base car's own <c>Livery</c> parts (so every field the game
/// reads is a real livery's), with its own item id, name, description, icon and this car as its car. The copy keeps the
/// stock livery's effect: when the game mounts it, it puts the base car's livery texture on the car for a moment and
/// core's livery guard puts the car's own look straight back (<see cref="CarLooks"/>), which shows the livery from the
/// mounted bit, laid over the player's paint.
/// <para>What a player owns and has mounted is kept out of the game's save with the car (CarSave: the entries' item ids
/// are the car's own parts) and comes back from its sidecar.</para>
/// </summary>
internal sealed class CarLiveryParts
{
    internal sealed class Entry
    {
        public int Index;
        public string Id;
        public string Name;
        public string Description;
        public uint ItemId;
        /// <summary>The bit it takes in the mounted-parts bitset (-1: not in the list).</summary>
        public int Bit = -1;
        public Livery Part;
        /// <summary>The livery's image as a look (null for the paint entry, or when its PNG is missing).</summary>
        public CarLooks.SkinFile File;
        /// <summary>The key the image shares with the bundled skin of the same livery (one texture for both).</summary>
        public string ImageKey;
    }

    private readonly RuntimeCar _car;
    private readonly List<Entry> _entries = new();
    private readonly HashSet<uint> _ids = new();
    private int[] _bits = Array.Empty<int>();

    public CarLiveryParts(RuntimeCar car)
    {
        _car = car;
        var def = car.Def;
        var ids = CarLiveries.EntryIds(def.Liveries);
        for (int i = 0; i < ids.Count; i++)
        {
            var preset = i == 0 ? null : def.Liveries[i - 1];
            var e = new Entry
            {
                Index = i,
                Id = ids[i],
                Name = preset?.Name.Trim() ?? CarLiveries.PaintName,
                Description = preset?.Description ?? CarLiveries.PaintDescription,
                ItemId = CarLiveries.ItemId(def.Id, ids[i]),
            };
            if (preset != null)
            {
                e.ImageKey = CarLiveries.SkinKey(def.Id, e.Name);
                string file = CarLiveries.TextureFile(def.AssetName, e.Id);
                e.File = new CarLooks.SkinFile { Path = "embedded:" + file, Reader = () => ReadEmbedded(file), Owned = true };
            }
            _entries.Add(e);
            _ids.Add(e.ItemId);
        }
    }

    public IReadOnlyList<Entry> Entries => _entries;
    public bool Any => _entries.Count > 0;

    /// <summary>The entries' item ids (the car's own parts, for the save filter).</summary>
    public IReadOnlyCollection<uint> ItemIds => _ids;

    /// <summary>The PNG of a bundled livery, checked like a skin's (worker thread).</summary>
    private CarLooks.SkinRead ReadEmbedded(string file)
    {
        try
        {
            var bytes = _car.Assets.ReadAsset(file);
            if (bytes == null) return new CarLooks.SkinRead { Problem = file + " is not in the DLL" };
            string problem = CarSkinScan.CheckPng(bytes, out int side);
            if (problem != null) return new CarLooks.SkinRead { Problem = problem };
            return new CarLooks.SkinRead { Bytes = bytes, Hash = CarSkinScan.Hash8(bytes), Side = side };
        }
        catch (Exception e) { DebugLog.Error(_car.Id, e, "CarLiveryParts.ReadEmbedded"); return new CarLooks.SkinRead { Problem = e.Message }; }
    }

    /// <summary>The bundled liveries as skins too (the MODS page lists them), for Api.Cars: (name, reader).</summary>
    public List<(string name, Func<byte[]> read)> AsSkins()
    {
        var l = new List<(string, Func<byte[]>)>();
        foreach (var e in _entries)
        {
            if (e.File == null) continue;
            string file = CarLiveries.TextureFile(_car.Def.AssetName, e.Id);
            l.Add((e.Name, () => _car.Assets.ReadAsset(file)));
        }
        return l;
    }

    // ---------------------------------------------------------------- building (CarCatalog.Prepare)

    /// <summary>
    /// Appends the entries to the car's own upgrade list (<paramref name="lists"/>, already filtered: the base car's
    /// liveries are out), each a copy of one of the base car's livery parts. Main thread. Returns a line for the log.
    /// </summary>
    public string Build(CarInfo baseInfo, CarInfo info, UpgradesLists lists, Il2CppSystem.Collections.Generic.List<VehiclePartSO> defaults)
    {
        if (!Any) return "no liveries";
        var template = Template(baseInfo);
        if (template == null) return $"no liveries: the base car {baseInfo?.name} has no livery part to copy";
        var parts = lists.Parts;
        var existing = new List<uint>(parts.Count);
        for (int i = 0; i < parts.Count; i++) existing.Add(parts[i] != null ? parts[i].ItemId : 0u);
        var entryIds = new List<uint>(_entries.Count);
        foreach (var e in _entries) entryIds.Add(e.ItemId);
        _bits = CarLiveries.PlanBits(existing, entryIds);
        int made = 0;
        var skipped = new List<string>();
        foreach (var e in _entries)
        {
            int bit = _bits[e.Index];
            if (bit < 0) { skipped.Add(e.Id); continue; }
            if (bit < parts.Count) { e.Bit = bit; e.Part = parts[bit]?.TryCast<Livery>(); continue; } // already in (a second Prepare)
            var l = Make(template, info, e);
            parts.Add(l);
            e.Bit = parts.Count - 1;
            e.Part = l;
            made++;
        }
        if (defaults != null && _entries[0].Part != null) defaults.Add(_entries[0].Part);
        return $"{made} livery part(s) after {existing.Count} kept part(s) (bits {_entries[0].Bit}..{_entries[^1].Bit}), copied from {template.name}" +
               (skipped.Count > 0 ? $"; left out (the bitset is full): {string.Join(", ", skipped)}" : "");
    }

    /// <summary>The base car's plain livery to copy: a Livery of type Livery without DLC if there is one.</summary>
    private static Livery Template(CarInfo baseInfo)
    {
        var parts = baseInfo?.UpgradesLists?.Parts;
        if (parts == null) return null;
        Livery any = null;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (p == null || p.Type != ItemType.Livery) continue;
            var l = p.TryCast<Livery>();
            if (l == null) continue;
            if (l.RequiredDlc == DlcId.None) return l;
            any ??= l;
        }
        return any;
    }

    private Livery Make(Livery template, CarInfo info, Entry e)
    {
        var def = _car.Def;
        var l = Object.Instantiate(template);
        l.name = $"{def.CarName}_Livery_{e.Id}";
        l.ItemId = e.ItemId;
        l.Price = 0;
        l.RequiredDlc = DlcId.None;
        l.CarInfo = info;
        l.UnlockKilometers = 0;
        // Its links were to the base car's parts.
        l.LinkedParts = new Il2CppSystem.Collections.Generic.List<VehiclePartSO>();
        string key = def.TextKey("LIVERY_" + e.Id.ToUpperInvariant());
        l.Name = _car.Text.Extra(template.Name, key + "_NAME", e.Name);
        l.Description = _car.Text.Extra(template.Description ?? template.Name, key + "_DESCRIPTION", e.Description);
        var icon = Icon(e);
        if (icon != null) l.Icon = Sprite.Create(icon, new Rect(0, 0, icon.width, icon.height), new Vector2(0.5f, 0.5f));
        l.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(l);
        return l;
    }

    /// <summary>The entry's garage icon (&lt;asset&gt;_livery_&lt;id&gt;_icon.png; the car's own icon when missing).</summary>
    private Texture2D Icon(Entry e)
    {
        try
        {
            var bytes = _car.Assets.ReadAsset(CarLiveries.IconFile(_car.Def.AssetName, e.Id));
            if (bytes == null) return _car.Assets.Icon;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false) { name = $"{_car.Def.CarName}_livery_{e.Id}_icon" };
            if (!ImageConversion.LoadImage(t, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(bytes), true))
            {
                Object.Destroy(t);
                return _car.Assets.Icon;
            }
            t.wrapMode = TextureWrapMode.Clamp;
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Object.DontDestroyOnLoad(t);
            return t;
        }
        catch (Exception ex) { DebugLog.Error(_car.Id, ex, "CarLiveryParts.Icon"); return _car.Assets.Icon; }
    }

    // ---------------------------------------------------------------- the item catalog (with the car)

    /// <summary>Why the entries can't go in the catalog (an id another item uses), or null.</summary>
    public string Clash(InventoryItemsCatalog cat)
    {
        foreach (var e in _entries)
        {
            if (e.Part == null) continue;
            if (cat.IdToSO.ContainsKey(e.ItemId))
            {
                var have = cat.IdToSO[e.ItemId];
                if (have == null || have.Pointer != e.Part.Pointer) return $"another item already uses the livery {e.Id}'s id 0x{e.ItemId:X8}";
            }
        }
        return null;
    }

    public void AddTo(InventoryItemsCatalog cat)
    {
        foreach (var e in _entries)
        {
            if (e.Part == null) continue;
            if (cat.IdToSO.ContainsKey(e.ItemId))
            {
                var have = cat.IdToSO[e.ItemId];
                if (have != null && have.Pointer != e.Part.Pointer) continue; // another item's id (logged by Clash)
            }
            if (cat.NameToId.ContainsKey(e.Part.name) && cat.NameToId[e.Part.name] != e.ItemId) continue;
            cat.IdToSO[e.ItemId] = e.Part;
            cat.IdToName[e.ItemId] = e.Part.name;
            cat.NameToId[e.Part.name] = e.ItemId;
        }
    }

    public void RemoveFrom(InventoryItemsCatalog cat)
    {
        foreach (var e in _entries)
        {
            if (e.Part == null || !cat.IdToSO.ContainsKey(e.ItemId) || cat.IdToSO[e.ItemId]?.Pointer != e.Part.Pointer) continue;
            cat.IdToSO.Remove(e.ItemId);
            cat.IdToName.Remove(e.ItemId);
            cat.NameToId.Remove(e.Part.name);
        }
    }

    // ---------------------------------------------------------------- which livery a car wears

    /// <summary>The entry a networked car has mounted, from its bitset (0: the paint, or nothing readable).</summary>
    public int Mounted(VehicleCustomizationController vcc)
    {
        if (_entries.Count < 2 || vcc == null) return 0;
        BitSet256 bits;
        try { bits = vcc.EnabledUpgrades; }
        catch (Exception ex) { DebugLog.Error(_car.Id, ex, "CarLiveryParts.Mounted"); return 0; }
        var b = new int[_entries.Count];
        foreach (var e in _entries) b[e.Index] = e.Bit;
        return CarLiveries.MountedEntry(b, i => bits.IsSet(i));
    }

    /// <summary>The entry of a Livery part, or -1 when it isn't one of this car's.</summary>
    public int EntryOf(VehiclePartSO part)
    {
        if (part == null) return -1;
        foreach (var e in _entries)
            if (e.Part != null && e.Part.Pointer == part.Pointer) return e.Index;
        return -1;
    }

    /// <summary>The entry the garage shows on the car's model: the livery picked in the Liveries tab while it is
    /// open, otherwise the one the garage's car has mounted.</summary>
    public int ForGarage(StateMerchantMenu menu)
    {
        if (_entries.Count < 2 || menu == null) return 0;
        int preview = EntryOf(GaragePreview.Current);
        if (preview >= 0) return preview;
        try
        {
            var mounted = menu.currentCar?.GetCurrentLivery();
            int i = EntryOf(mounted);
            return i < 0 ? 0 : i;
        }
        catch (Exception ex) { DebugLog.Error(_car.Id, ex, "CarLiveryParts.ForGarage"); return 0; }
    }

    public Entry this[int index] => index >= 0 && index < _entries.Count ? _entries[index] : null;
}

/// <summary>The livery the player is looking at in the garage's Liveries tab (shown on the garage's model until another
/// screen opens or the garage closes).</summary>
internal static class GaragePreview
{
    private static Livery _current;

    public static Livery Current
    {
        get
        {
            try { return _current != null && _current.Pointer != IntPtr.Zero ? _current : null; }
            catch (Exception ex) { DebugLog.Error("core", ex, "GaragePreview.Current"); return null; }
        }
    }

    public static void Set(Livery l) => _current = l;
    public static void Clear() => _current = null;
}
