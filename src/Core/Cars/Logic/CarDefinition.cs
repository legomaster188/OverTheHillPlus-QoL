using System;
using System.Collections.Generic;
using OverTheHillPlus.Api;
using OverTheHillPlus.Core.Shared;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Everything that makes one modded car (the Vera Crossover, the Terrier 4WD Wagon, ...): its identity, the stock cars
/// it is built from, its assets, how it drives, its colours and engine sounds and where the game's cosmetics go on it.
/// Core's car framework (<see cref="ModdedCars"/>) does the rest the same way for every car: the network prefab, the
/// garage entry, the save filter, the join rules, the tuning, the look and its sync, the MODS page.
/// <para>The identity values are part of the network protocol and of the save sidecar: they never change once a car is
/// released. Pure (unit-tested): <see cref="Validate"/> checks it.</para>
/// </summary>
public sealed class CarDefinition
{
    // ---------------------------------------------------------------- identity

    /// <summary>The car's mod id (its config file oth.&lt;id&gt;.cfg, its MODS page entry, its look messages).</summary>
    public string Id;
    /// <summary>Shown in the garage and on the MODS page, e.g. "Vera Crossover".</summary>
    public string DisplayName;
    /// <summary>The garage description.</summary>
    public string Description;
    /// <summary>The line under the car's heading on its MODS page.</summary>
    public string PageNote;
    /// <summary>The inventory item id. Stock ids are hash-like uints; pick one by hand (it is checked against the live
    /// catalog at startup and the car switches itself off on a clash).</summary>
    public uint ItemId;
    /// <summary>CarInfo.name: the game builds Prefabs/Cars/Car_&lt;name&gt; and the garage model's address from it,
    /// livery packs name the car by it (<c>"car": "&lt;name&gt;"</c>) and the MODS page buttons say "Drive the &lt;name&gt;".
    /// Letters, digits and '_', not a stock car's name.</summary>
    public string CarName;
    /// <summary>NetworkObjectGuid of the car's prefab entry: 32 hex digits, generated once.</summary>
    public string FusionGuid;
    /// <summary>The car's key in core's network prefab order (<see cref="NetPrefabPlan.Order"/>).</summary>
    public string RegistryKey;
    /// <summary>Bumped whenever the template's network layout changes (another donor, other behaviours, other tuning):
    /// part of the layout in core's join hash, so peers with another protocol are refused at connect.</summary>
    public int Protocol = 1;

    // ---------------------------------------------------------------- base cars

    /// <summary>The Fusion prefab the network and physics skeleton is copied from (its body, wheels and cosmetics are
    /// replaced), e.g. "Car_TrailBurner". Players may set another one in the config.</summary>
    public string DefaultDonor;
    /// <summary>The stock car whose garage data is copied: vehicle class, upgrade lists (liveries left out) and garage
    /// model, e.g. "TrailBurner".</summary>
    public string DefaultGarageBase;
    /// <summary>The car has a load bed (its base car's bed cosmetics are kept). Usually false.</summary>
    public bool HasBed;

    // ---------------------------------------------------------------- assets

    /// <summary>The embedded assets' prefix: resources "&lt;n&gt;/&lt;n&gt;.mesh", "&lt;n&gt;/&lt;n&gt;_albedo_base.png",
    /// "_ao.png", "_lights.png", "_mask.png", and the optional "_zones.png", "_icon.png" (white line drawing) and
    /// "_picture.png" (the car's picture in the garage and car lists; the icon is used when there is none) (a-z, 0-9, '_').</summary>
    public string AssetName;

    /// <summary>The suffix of the car's picture (<see cref="AssetFile"/>("picture") = "&lt;n&gt;_picture.png").</summary>
    public const string PictureSuffix = "picture";

    // ---------------------------------------------------------------- driving

    /// <summary>How it drives compared with the donor.</summary>
    public CarTuningProfile Tuning = new();
    public string GearSet = "";
    public byte[] Diffs;

    // ---------------------------------------------------------------- look

    /// <summary>Main paint presets; the first is the default.</summary>
    public PaintPreset[] Paints;
    /// <summary>The custom paint colour's default (#RRGGBB).</summary>
    public string PaintCustomDefault = "#2E4F7A";
    /// <summary>Second-colour presets; the first is the default.</summary>
    public TrimPreset[] Trims;
    public string TrimCustomDefault = "#3A3A3C";
    /// <summary>What the second colour paints, for the config ("Lower trim (cladding)").</summary>
    public string TrimWhat = "Trim";
    /// <summary>The Trim row's hint on the MODS page.</summary>
    public string TrimHint = "The second colour: a preset, the body colour or your own.";
    /// <summary>Stock engine sounds offered; <see cref="DefaultEngine"/> is the default.</summary>
    public EngineSound[] EngineSounds;
    public byte DefaultEngine;
    /// <summary>The Engine sound row's hint on the MODS page.</summary>
    public string EngineHint = "Which stock car's engine it sounds like. Other players with this mod hear your pick.";
    /// <summary>The liveries the mod bundles (<see cref="CarLiveries"/>), in the order the garage lists them: empty for
    /// none. Each one's PNG and icon are embedded assets. Part of the network layout: peers need the same list.</summary>
    public LiveryPreset[] Liveries = Array.Empty<LiveryPreset>();

    // ---------------------------------------------------------------- cosmetics

    /// <summary>Where the game's cosmetic slots go (null: <see cref="CarCosmetics.Standard"/>).</summary>
    public CarCosmeticAnchor[] Cosmetics;
    /// <summary>More item anchors for this car on top of the table (anchor to item path, e.g. a cosm_* anchor for
    /// "Visual/Roof/Kayak"): data only, no code change.</summary>
    public CosmeticItemAnchor[] ItemAnchors = Array.Empty<CosmeticItemAnchor>();
    /// <summary>Slots always hidden on this car, besides the rollcage.</summary>
    public string[] HiddenSlots = Array.Empty<string>();
    /// <summary>Single items always hidden on this car (paths under the cosmetic root, e.g. "Visual/Roof/Christmas"):
    /// shaped for the donor's body in a way no anchor fixes. Their garage parts are left out when nothing else shows them.</summary>
    public string[] HiddenItems = Array.Empty<string>();
    /// <summary>Per-anchor fine tuning: metres added to a <c>cosm_*</c> anchor (car-root space, Unity axes) at runtime.</summary>
    public Dictionary<string, MeshVec3> CosmeticTweaks = new(StringComparer.Ordinal);
    /// <summary>Per-item fine tuning: metres added to one item's position (a <c>&lt;group&gt;/&lt;slot&gt;/&lt;item&gt;</c> path, car-root
    /// space, Unity axes) after its slot is placed, for an item the slot's anchor can't put right without moving the rest
    /// of the slot (a tow bar under a rear beam). Data only: no model change.</summary>
    public Dictionary<string, MeshVec3> ItemOffsets = new(StringComparer.Ordinal);

    // ---------------------------------------------------------------- body physics and donor objects

    /// <summary>Extra box colliders of the body (model space), for the parts the model's collider part leaves out, e.g. a
    /// tractor's frame and deck behind the cab. Part of the network layout (bump <see cref="Protocol"/>).</summary>
    public CarBodyBox[] BodyBoxes = Array.Empty<CarBodyBox>();
    /// <summary>The car's own rules for the donor's runtime objects (<see cref="CarDonorObjects"/>): an anchor to move one
    /// to, or an empty anchor to switch it off. Empty: the defaults.</summary>
    public DonorObjectOverride[] DonorObjects = Array.Empty<DonorObjectOverride>();

    // ---------------------------------------------------------------- derived

    /// <summary>Fusion's prefab table name, "Car_&lt;CarName&gt;".</summary>
    public string PrefabName => "Car_" + CarName;
    /// <summary>Fusion prefab path: never a real asset, only the table's key.</summary>
    public string AssetPath => "Assets/Mods/OTHP/" + PrefabName + ".prefab";
    /// <summary>The string-table key of the garage name or description ("OTHP_VERA_NAME").</summary>
    public string TextKey(string what) => "OTHP_" + (CarName ?? "").ToUpperInvariant() + "_" + what;
    /// <summary>The car's anchor table: <see cref="Cosmetics"/> (or the standard one) with its <see cref="ItemAnchors"/>.</summary>
    public IReadOnlyList<CarCosmeticAnchor> CosmeticTable =>
        ItemAnchors == null || ItemAnchors.Length == 0 ? Cosmetics ?? CarCosmetics.Standard : CarCosmetics.With(Cosmetics ?? CarCosmetics.Standard, ItemAnchors);
    public string MeshFile => AssetName + ".mesh";
    public string AssetFile(string suffix) => AssetName + "_" + suffix + ".png";

    public byte EngineIndexOf(string label) => EngineSounds == null ? (byte)0 : EngineSoundList.IndexOf(EngineSounds, label, DefaultEngine);
    public string EngineLabel(byte index) => EngineSoundList.Label(EngineSounds, index, DefaultEngine);

    /// <summary>A garage base setting as a CarInfo name ("Car_TrailBurner" and "TrailBurner" both mean the TrailBurner;
    /// empty: the default).</summary>
    public string GarageBaseName(string setting)
    {
        string v = string.IsNullOrWhiteSpace(setting) ? DefaultGarageBase ?? "" : setting.Trim();
        return v.StartsWith("Car_", StringComparison.OrdinalIgnoreCase) ? v.Substring(4) : v;
    }

    /// <summary>
    /// What every peer's template must agree on beyond the guid, for core's join hash: "v&lt;Protocol&gt;|&lt;donor&gt;|&lt;garage
    /// base&gt;" (the Vera's format from before the framework), then "|parts&lt;n&gt;" for the framework's
    /// <see cref="PartsRevision"/>, then the liveries' part (<see cref="CarLiveries.LayoutPart"/>: nothing for a car without
    /// liveries). The liveries are in it because they take bits in the mounted-parts bitset: a peer with another set would
    /// see other parts. The tuning and everything else built from a car's own code is covered by <see cref="Protocol"/>:
    /// bump it when they change.
    /// </summary>
    public string LayoutFor(string donorSetting, string garageBaseSetting) =>
        $"v{Protocol}|{(string.IsNullOrWhiteSpace(donorSetting) ? DefaultDonor : donorSetting)}|{GarageBaseName(garageBaseSetting)}" +
        (PartsRevision > 1 ? "|parts" + PartsRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") +
        (PhysicsRevision > 1 ? "|phys" + PhysicsRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") +
        CarLiveries.LayoutPart(Liveries);

    /// <summary>How the framework derives a car's physics from its profile and model (<see cref="CarPhysicsFit"/>: wheel
    /// inertia, unsprung mass, bump stops, brakes, converter, engine inertia, inertia box, body colliders): a peer whose
    /// framework derives other values predicts another car, so this is part of every car's <see cref="LayoutFor"/>
    /// (1 added nothing). 2: the physics fit and the collider rules of 2026-10-18.</summary>
    public const int PhysicsRevision = 2;

    /// <summary>Which garage parts the framework gives every modded car (CarCatalog's rules). The car's part list indexes
    /// the networked mounted-parts bitset, so peers whose framework builds another list must not play together: bumped
    /// whenever those rules change, part of every car's <see cref="LayoutFor"/> (1 added nothing, keeping the old hash).
    /// 2: the tool-slot and protection upgrades kept, cosmetics with no place on the body left out.</summary>
    public const int PartsRevision = 2;

    /// <summary>A deep copy (the framework keeps its own, so the caller's object can't change a running car).</summary>
    public CarDefinition Clone()
    {
        var c = (CarDefinition)MemberwiseClone();
        c.Tuning = Tuning == null ? null : (CarTuningProfile)Tuning.Copy();
        c.Diffs = Diffs == null ? null : (byte[])Diffs.Clone();
        c.Paints = Paints == null ? null : Array.ConvertAll(Paints, p => p == null ? null : new PaintPreset(p.Name, p.Rgb));
        c.Trims = Trims == null ? null : Array.ConvertAll(Trims, t => t == null ? null : new TrimPreset { Name = t.Name, Rgb = t.Rgb, BodyColour = t.BodyColour });
        c.EngineSounds = EngineSounds == null ? null : Array.ConvertAll(EngineSounds, e => e == null ? null : new EngineSound(e.Label, e.Name));
        c.Cosmetics = Cosmetics == null ? null : Array.ConvertAll(Cosmetics, a => a == null ? null : new CarCosmeticAnchor
        {
            Anchor = a.Anchor, Target = a.Target, Slot = a.Slot, Optional = a.Optional,
        });
        c.ItemAnchors = ItemAnchors == null ? null : Array.ConvertAll(ItemAnchors, i => i == null ? null : new CosmeticItemAnchor(i.Anchor, i.Item, i.Optional));
        c.HiddenSlots = HiddenSlots == null ? null : (string[])HiddenSlots.Clone();
        c.HiddenItems = HiddenItems == null ? null : (string[])HiddenItems.Clone();
        c.CosmeticTweaks = CosmeticTweaks == null ? null : new Dictionary<string, MeshVec3>(CosmeticTweaks, StringComparer.Ordinal);
        c.ItemOffsets = ItemOffsets == null ? null : new Dictionary<string, MeshVec3>(ItemOffsets, StringComparer.Ordinal);
        c.Liveries = Liveries == null ? null : Array.ConvertAll(Liveries, l => l == null ? null : new LiveryPreset(l.Id, l.Name, l.Description));
        return c;
    }

    /// <summary>The cosmetic tweak for an anchor (zero when none).</summary>
    /// <summary>The offset of one item (zero when none).</summary>
    public MeshVec3 ItemOffsetOf(string item) =>
        item != null && ItemOffsets != null && ItemOffsets.TryGetValue(item, out var v) ? v : default;

    public MeshVec3 TweakOf(string anchor) =>
        anchor != null && CosmeticTweaks != null && CosmeticTweaks.TryGetValue(anchor, out var v) ? v : default;

    /// <summary>Null when the definition can be used, otherwise the first problem.</summary>
    public string Validate()
    {
        if (!ApiRules.IsValidModId(Id)) return $"\"{Id}\" is not a valid car id (2 to 40 characters from a-z, 0-9, '_', '.' and '-')";
        string who = $"car \"{Id}\": ";
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > CarRegistry.MaxDisplayName)
            return who + $"the display name must be 1 to {CarRegistry.MaxDisplayName} characters";
        if (!CarRegistry.IsLiveryCarName(CarName)) return who + "the car name must be 1 to 32 letters, digits or '_'";
        if (CarRegistry.IsStockCarName(CarName)) return who + $"\"{CarName}\" is a stock car's name";
        if (ItemId == 0) return who + "no item id";
        if (!IsHex32(FusionGuid)) return who + "the Fusion guid must be 32 hex digits";
        if (NetPrefabPlan.OrderOf(RegistryKey) < 0) return who + $"the registry key \"{RegistryKey}\" is not in core's network prefab order";
        string owner = NetPrefabPlan.OwnerOf(RegistryKey);
        if (owner != Id) return who + $"the registry key \"{RegistryKey}\" belongs to the mod \"{owner}\"";
        if (Protocol < 1) return who + "the protocol must be 1 or more";
        if (string.IsNullOrWhiteSpace(DefaultDonor)) return who + "no donor prefab";
        if (string.IsNullOrWhiteSpace(DefaultGarageBase)) return who + "no garage base car";
        if (!IsAssetName(AssetName)) return who + "the asset name must be 1 to 32 characters from a-z, 0-9 and '_'";
        if (Tuning == null) return who + "no tuning profile";
        if (GearSet == null || GearSet.Length > 16) return who + "the gear set must be an id of at most 16 characters (or empty)";
        if (Diffs != null)
        {
            if (Diffs.Length != 3) return who + "the differentials are three values: front, rear, centre";
            foreach (byte d in Diffs) if (d > 3) return who + "a differential mode is 0 (the game's), 1 (open), 2 (limited slip) or 3 (locked)";
        }
        if (string.IsNullOrWhiteSpace(Description) || string.IsNullOrWhiteSpace(PageNote)) return who + "no description or page note";
        if (BodyBoxes == null || BodyBoxes.Length > 8) return who + "at most 8 body boxes";
        foreach (var b in BodyBoxes)
        {
            if (b == null) return who + "a body box is empty";
            static bool Ok(float v, float lo, float hi) => !float.IsNaN(v) && v >= lo && v <= hi;
            if (!Ok(b.Size.X, 0.05f, 4f) || !Ok(b.Size.Y, 0.05f, 4f) || !Ok(b.Size.Z, 0.05f, 12f) ||
                !Ok(b.Center.X, -3f, 3f) || !Ok(b.Center.Y, 0f, 5f) || !Ok(b.Center.Z, -8f, 8f))
                return who + "a body box must be 0.05 to 4 m wide and high (12 m long) and sit on the car";
        }
        if (DonorObjects == null) return who + "the donor object rules must be a list (empty for the defaults)";
        foreach (var o in DonorObjects)
            if (o == null || string.IsNullOrWhiteSpace(o.Path) || o.Path.StartsWith("Parts/", StringComparison.Ordinal))
                return who + "a donor object rule needs a path outside Parts/ (the cosmetic slots move with their slot)";

        if (Paints == null || Paints.Length == 0) return who + "no paint presets";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Paints)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Name)) return who + "a paint preset has no name";
            if (string.Equals(p.Name, CarPaint.Custom, StringComparison.OrdinalIgnoreCase)) return who + "\"Custom\" is not a preset name";
            if (!names.Add(p.Name)) return who + $"the paint preset \"{p.Name}\" is listed twice";
        }
        if (Trims == null || Trims.Length == 0) return who + "no trim presets";
        names.Clear();
        foreach (var t in Trims)
        {
            if (t == null || string.IsNullOrWhiteSpace(t.Name)) return who + "a trim preset has no name";
            if (string.Equals(t.Name, CarPaint.Custom, StringComparison.OrdinalIgnoreCase)) return who + "\"Custom\" is not a trim name";
            if (!names.Add(t.Name)) return who + $"the trim \"{t.Name}\" is listed twice";
        }
        if (!CarRgb.TryParse(PaintCustomDefault, out _) || !CarRgb.TryParse(TrimCustomDefault, out _))
            return who + "the custom colour defaults must be #RRGGBB";

        if (EngineSounds == null || EngineSounds.Length == 0 || EngineSounds.Length > 64) return who + "1 to 64 engine sounds are needed";
        names.Clear();
        foreach (var e in EngineSounds)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Label) || string.IsNullOrWhiteSpace(e.Name)) return who + "an engine sound has no label or name";
            if (!names.Add(e.Label)) return who + $"the engine sound {e.Label} is listed twice";
        }
        if (DefaultEngine >= EngineSounds.Length) return who + "the default engine sound is not in the list";

        string liveries = CarLiveries.Check(Liveries);
        if (liveries != null) return who + liveries;

        string cosm = CarCosmetics.Check(CosmeticTable);
        if (cosm != null) return who + cosm;
        if (HiddenItems != null)
            foreach (var h in HiddenItems)
                if (CosmeticItemAnchor.SlotOf(h) == null) return who + $"the hidden item \"{h}\" is not a <group>/<slot>/<item> path";
        if (CosmeticTweaks != null)
            foreach (var kv in CosmeticTweaks)
            {
                bool known = false;
                foreach (var a in CosmeticTable) if (a.Anchor == kv.Key) known = true;
                if (!known) return who + $"the cosmetic tweak {kv.Key} is not an anchor of its table";
                if (Math.Abs(kv.Value.X) > 1f || Math.Abs(kv.Value.Y) > 1f || Math.Abs(kv.Value.Z) > 1f)
                    return who + $"the cosmetic tweak {kv.Key} is over a metre";
            }
        if (ItemOffsets != null)
            foreach (var kv in ItemOffsets)
            {
                if (CosmeticItemAnchor.SlotOf(kv.Key) == null) return who + $"the item offset \"{kv.Key}\" is not a <group>/<slot>/<item> path";
                if (Math.Abs(kv.Value.X) > 1f || Math.Abs(kv.Value.Y) > 1f || Math.Abs(kv.Value.Z) > 1f)
                    return who + $"the item offset {kv.Key} is over a metre";
            }
        return null;
    }

    /// <summary>What two definitions may not share (id, item id, car name, guid, registry key, asset name); null when
    /// they share none of them.</summary>
    public static string Clash(CarDefinition a, CarDefinition b)
    {
        if (a == null || b == null) return null;
        if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)) return $"the car id {a.Id} is taken";
        if (a.ItemId == b.ItemId) return $"the item id 0x{a.ItemId:X8} is taken by {b.Id}";
        if (string.Equals(a.CarName, b.CarName, StringComparison.OrdinalIgnoreCase)) return $"the car name {a.CarName} is taken by {b.Id}";
        if (string.Equals(a.FusionGuid, b.FusionGuid, StringComparison.OrdinalIgnoreCase)) return $"the Fusion guid is taken by {b.Id}";
        if (a.RegistryKey == b.RegistryKey) return $"the registry key {a.RegistryKey} is taken by {b.Id}";
        if (a.AssetName == b.AssetName) return $"the asset name {a.AssetName} is taken by {b.Id}";
        return null;
    }

    private static bool IsHex32(string s)
    {
        if (s == null || s.Length != 32) return false;
        foreach (char c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static bool IsAssetName(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 32) return false;
        foreach (char c in s)
            if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_')) return false;
        return true;
    }
}
