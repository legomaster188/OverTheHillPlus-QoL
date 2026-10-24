using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Cars;

/// <summary>One placement: the model anchor and the node under the donor's <c>Parts/&lt;Car&gt;_CosmeticParts</c> it moves.</summary>
public sealed class CarCosmeticAnchor
{
    /// <summary>The anchor's name in the car's mesh blob (a point in car-root space, Unity axes, before the layout offset).</summary>
    public string Anchor;
    /// <summary>The node it places, relative to the cosmetic root (e.g. "Visual/Fender/Mudflap_FL").</summary>
    public string Target;
    /// <summary>The slot the node belongs to (e.g. "Visual/Fender"): hidden when any of its required anchors is missing.</summary>
    public string Slot;
    /// <summary>An item anchor the model may leave out: without it the item stays where its slot puts it (the slot still
    /// shows). Slot anchors are never optional.</summary>
    public bool Optional;

    public bool IsSlot => Target == Slot;
}

/// <summary>An item anchor as data: the model anchor and the item it places (car-root position, like the mudflaps and
/// side items), e.g. cosm_Plate_Rear to "Visual/Rear_Bumper/LicensePlates_Rear". The item's slot is its path's first two
/// parts. A car adds its own with <see cref="CarDefinition.ItemAnchors"/>, no code change needed.</summary>
public sealed class CosmeticItemAnchor
{
    public string Anchor;
    /// <summary>"&lt;group&gt;/&lt;slot&gt;/&lt;item&gt;" under the cosmetic root.</summary>
    public string Item;
    /// <summary>Models without the anchor keep the item at its slot's own spot (the slot still shows).</summary>
    public bool Optional;

    public CosmeticItemAnchor() { }
    public CosmeticItemAnchor(string anchor, string item, bool optional = false) { Anchor = anchor; Item = item; Optional = optional; }

    /// <summary>The slot of an item path ("Visual/Rear_Bumper/LicensePlates_Rear" gives "Visual/Rear_Bumper"), null when
    /// the path has fewer than three parts.</summary>
    public static string SlotOf(string item)
    {
        if (string.IsNullOrEmpty(item)) return null;
        int a = item.IndexOf('/');
        int b = a < 0 ? -1 : item.IndexOf('/', a + 1);
        return b <= a + 1 || b >= item.Length - 1 ? null : item.Substring(0, b);
    }
}

/// <summary>
/// Where the game's cosmetics go on a modded car. Every stock car has the same cosmetic slot tree
/// (<c>Parts/&lt;Car&gt;_CosmeticParts/Visual/...</c> and <c>/Performance/...</c>) with mostly shared item meshes; each car
/// only places the slots (and a few items inside them) differently. A modded car's model carries one <c>cosm_*</c>
/// anchor per slot (and per placed item); the runtime moves each target there (position only: rotation and scale stay
/// the donor's), plus the car's per-anchor tweak (<see cref="CarDefinition.CosmeticTweaks"/>).
/// <para>A slot is shown only when every anchor it needs is in the model (an older model without them hides the slot
/// rather than leave it at the donor's position); the rollcage (shaped for the donor's body), the car's own hidden
/// slots and any slot the car's table doesn't know are always hidden. Pure (unit-tested).</para>
/// </summary>
public static class CarCosmetics
{
    public const string Rollcage = "Performance/Rollcage";

    /// <summary>The stock cars' slots (TrailBurner, Brick, ...): eleven visual, three performance.</summary>
    public static readonly string[] Slots =
    {
        "Visual/Front", "Visual/Rear", "Visual/Front_Bumper", "Visual/Rear_Bumper", "Visual/Hood", "Visual/Roof",
        "Visual/Roof_Bar", "Visual/Fender", "Visual/Side", "Visual/Side_Step", "Visual/Antenna",
        Rollcage, "Performance/Snorkel", "Performance/Winch",
    };

    /// <summary>The slot anchors: one per slot that moves as a whole (the rollcage has none; the Fender and Snorkel
    /// slots have none either: their items are placed one by one).</summary>
    public static readonly (string Anchor, string Slot)[] SlotAnchors =
    {
        ("cosm_Front", "Visual/Front"),
        ("cosm_Rear", "Visual/Rear"),
        ("cosm_Front_Bumper", "Visual/Front_Bumper"),
        ("cosm_Rear_Bumper", "Visual/Rear_Bumper"),
        ("cosm_Hood", "Visual/Hood"),
        ("cosm_Roof", "Visual/Roof"),
        ("cosm_Roof_Bar", "Visual/Roof_Bar"),
        ("cosm_Side", "Visual/Side"),
        ("cosm_Side_Step", "Visual/Side_Step"),
        ("cosm_Antenna", "Visual/Antenna"),
        ("cosm_Winch", "Performance/Winch"),
    };

    /// <summary>
    /// The standard item anchors (anchor to item, placed at the anchor's car-root position after their slot moved). An
    /// optional anchor (the rear plate) may be missing from a model without hiding its slot. A car adds more with
    /// <see cref="CarDefinition.ItemAnchors"/>.
    /// </summary>
    public static readonly CosmeticItemAnchor[] ItemAnchors =
    {
        new("cosm_Mudflap_FL", "Visual/Fender/Mudflap_FL"),
        new("cosm_Mudflap_FR", "Visual/Fender/Mudflap_FR"),
        new("cosm_Mudflap_RL", "Visual/Fender/Mudflap_RL"),
        new("cosm_Mudflap_RR", "Visual/Fender/Mudflap_RR"),
        new("cosm_Side_Left", "Visual/Side/Side_Left"),
        new("cosm_Side_Right", "Visual/Side/Side_Right"),
        new("cosm_Snorkel", "Performance/Snorkel/SnorkelLevel01"),
        new("cosm_Snorkel", "Performance/Snorkel/SnorkelLevel02"),
        new("cosm_Plate_Rear", "Visual/Rear_Bumper/LicensePlates_Rear", optional: true),
        // items that mount somewhere else than the rest of their slot: the front plate on the bumper face (the bash
        // bars and skid plates of its slot sit lower), the whip-light poles on the rear corners or bed, the ladder
        // hooked over the roof edge (the spare wheel and racks of the Rear slot hang on the tailgate)
        new("cosm_Plate_Front", "Visual/Front_Bumper/LicensePlates_Front", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_01", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_02", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_03", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_04", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_05", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_06", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_07", optional: true),
        new("cosm_Ladder", "Visual/Rear/Ladder", optional: true),
        // the bush cables run from the nose to the windscreen pillars' tops: on a cab of another length they need their
        // own place and stretch (cosmscale_Bushcables), not the hood slot's
        new("cosm_Bushcables", "Visual/Hood/Bushcables", optional: true),
    };

    /// <summary>The standard anchor table (every car's unless its definition gives its own): the slots, then the items
    /// (an item is placed after its slot has moved).</summary>
    public static readonly CarCosmeticAnchor[] Standard = Build(SlotAnchors, ItemAnchors);

    /// <summary>A table of slot anchors and item anchors (an item whose path has no slot keeps a null slot, which
    /// <see cref="Check"/> refuses).</summary>
    public static CarCosmeticAnchor[] Build(IEnumerable<(string Anchor, string Slot)> slots, IEnumerable<CosmeticItemAnchor> items)
    {
        var list = new List<CarCosmeticAnchor>();
        if (slots != null)
            foreach (var (anchor, slot) in slots) list.Add(Slot(anchor, slot));
        return With(list, items);
    }

    /// <summary><paramref name="table"/> followed by more item anchors.</summary>
    public static CarCosmeticAnchor[] With(IReadOnlyList<CarCosmeticAnchor> table, IEnumerable<CosmeticItemAnchor> items)
    {
        var list = new List<CarCosmeticAnchor>(table ?? Standard);
        if (items != null)
            foreach (var i in items)
                list.Add(i == null ? null : new CarCosmeticAnchor
                {
                    Anchor = i.Anchor, Target = i.Item, Slot = CosmeticItemAnchor.SlotOf(i.Item), Optional = i.Optional,
                });
        return list.ToArray();
    }

    /// <summary>The prefix of a scale anchor: a model anchor named <c>cosmscale_&lt;name&gt;</c> scales the targets of
    /// <c>cosm_&lt;name&gt;</c> (its position is the per-axis scale, Unity axes, in each target's own axes), e.g. a roof
    /// load or bull bar made narrower for a slimmer body. Models without one keep the donor's scale.</summary>
    public const string ScalePrefix = "cosmscale_";
    public const float MinScale = 0.5f;
    public const float MaxScale = 1.5f;

    /// <summary>The scale anchor of a <c>cosm_</c> anchor ("cosm_Roof" gives "cosmscale_Roof"); null for any other name.</summary>
    public static string ScaleAnchorOf(string anchor) =>
        anchor != null && anchor.Length > 5 && anchor.StartsWith("cosm_", StringComparison.Ordinal) ? ScalePrefix + anchor.Substring(5) : null;

    /// <summary>A scale anchor's position as a scale: each axis inside [<see cref="MinScale"/>, <see cref="MaxScale"/>],
    /// 1 where it isn't a positive finite number (a model can't blow an item up or flip it).</summary>
    public static MeshVec3 ScaleOf(MeshVec3 raw)
    {
        static float C(float v) => float.IsNaN(v) || float.IsInfinity(v) || v <= 0f ? 1f : Math.Clamp(v, MinScale, MaxScale);
        return new MeshVec3(C(raw.X), C(raw.Y), C(raw.Z));
    }

    /// <summary>A target's local scale after placing: its donor scale with the slot's scale taken back out (an item with
    /// its own anchor inside a scaled slot keeps its own size; pass 1,1,1 for the slot itself) and its own scale anchor
    /// applied.</summary>
    public static MeshVec3 TargetScale(MeshVec3 local, MeshVec3 slotScale, MeshVec3 own)
    {
        static float D(float v, float s) => s > 0f ? v / s : v;
        return new MeshVec3(D(local.X, slotScale.X) * own.X, D(local.Y, slotScale.Y) * own.Y, D(local.Z, slotScale.Z) * own.Z);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="hidden"/>'s node or under it ("Visual/Roof/Christmas"
    /// hides "Visual/Roof/Christmas" and "Visual/Roof/Christmas/Lights", not "Visual/Roof/Christmas_Front").</summary>
    public static bool IsUnder(string path, string hidden) =>
        path != null && hidden != null && path.StartsWith(hidden, StringComparison.Ordinal) &&
        (path.Length == hidden.Length || path[hidden.Length] == '/');

    public static CarCosmeticAnchor Slot(string anchor, string slot) => new() { Anchor = anchor, Target = slot, Slot = slot };

    public static CarCosmeticAnchor Item(string anchor, string slot, string item, bool optional = false) =>
        new() { Anchor = anchor, Target = slot + "/" + item, Slot = slot, Optional = optional };

    /// <summary>The placements of one slot in <paramref name="table"/>, the slot's own first.</summary>
    public static List<CarCosmeticAnchor> For(IReadOnlyList<CarCosmeticAnchor> table, string slot)
    {
        var list = new List<CarCosmeticAnchor>();
        if (table == null) return list;
        foreach (var a in table)
            if (a != null && a.Slot == slot && a.IsSlot) list.Add(a);
        foreach (var a in table)
            if (a != null && a.Slot == slot && !a.IsSlot) list.Add(a);
        return list;
    }

    /// <summary>Whether the slot shows: never the rollcage, one of <paramref name="hidden"/> or a slot the table
    /// doesn't know, otherwise when the model has every required anchor the slot needs (optional item anchors aside).</summary>
    public static bool Shown(IReadOnlyList<CarCosmeticAnchor> table, string slot, Func<string, bool> hasAnchor,
        IReadOnlyCollection<string> hidden = null)
    {
        if (slot == Rollcage || hasAnchor == null) return false;
        if (hidden != null)
            foreach (var h in hidden)
                if (h == slot) return false;
        var list = For(table, slot);
        if (list.Count == 0) return false;
        foreach (var a in list)
            if (!a.Optional && !hasAnchor(a.Anchor)) return false;
        return true;
    }

    /// <summary>Null when the table is usable: every entry has a <c>cosm_</c> anchor, a slot and a target inside that
    /// slot, no target twice; otherwise what is wrong.</summary>
    public static string Check(IReadOnlyList<CarCosmeticAnchor> table)
    {
        if (table == null || table.Count == 0) return "the cosmetic anchor table is empty";
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in table)
        {
            if (a == null) return "the cosmetic anchor table has an empty entry";
            if (string.IsNullOrEmpty(a.Anchor) || !a.Anchor.StartsWith("cosm_", StringComparison.Ordinal))
                return $"cosmetic anchor \"{a.Anchor}\" must start with cosm_";
            if (string.IsNullOrEmpty(a.Slot) || string.IsNullOrEmpty(a.Target))
                return $"cosmetic anchor {a.Anchor} has no slot or target (an item path is <group>/<slot>/<item>)";
            if (a.IsSlot && a.Optional) return $"the slot anchor {a.Anchor} can't be optional";
            if (!a.IsSlot && !a.Target.StartsWith(a.Slot + "/", StringComparison.Ordinal))
                return $"cosmetic target {a.Target} is not inside its slot {a.Slot}";
            if (!targets.Add(a.Target)) return $"cosmetic target {a.Target} is placed twice";
        }
        return null;
    }
}
