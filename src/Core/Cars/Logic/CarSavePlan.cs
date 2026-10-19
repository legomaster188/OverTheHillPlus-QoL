using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.Cars;

/// <summary>One inventory entry as the save filter sees it (built from the game's BaseInventoryItem list).</summary>
public sealed class CarSaveItem
{
    public uint ItemId;
    /// <summary>A VehicleItem (an owned car).</summary>
    public bool IsVehicle;
    public bool InUse;
    /// <summary>A VehicleSpecificItem's per-car states (null for other items).</summary>
    public List<CarSaveState> States;
}

public sealed class CarSaveState
{
    public uint VehicleItemId;
    public int Count;
    public bool InUse;
}

/// <summary>
/// What to take out of the inventory before the game writes it to disk, so a save never names a modded car and still
/// loads in a game without its mod (it then just has the fallback car in use). Pure code (unit-tested); the runtime
/// applies the plan around the game's serializer and puts everything back right after.
/// </summary>
public sealed class CarSavePlan
{
    /// <summary>Indices into the item list, ascending.</summary>
    public readonly List<int> RemoveItems = new();
    /// <summary>(item index, state index) pairs, ascending, for items that are kept.</summary>
    public readonly List<(int item, int state)> RemoveStates = new();
    /// <summary>Index of the car to mark in use while the modded car is out of the list (-1: none).</summary>
    public int FallbackInUse = -1;
    /// <summary>The modded car itself was owned / in use (for the sidecar).</summary>
    public bool CarOwned, CarInUse;
    /// <summary>Every state of the car removed (with its item id), for the sidecar.</summary>
    public readonly List<CarSidecarState> Removed = new();

    public bool IsEmpty => RemoveItems.Count == 0 && RemoveStates.Count == 0;

    /// <summary>The plan for car <paramref name="carId"/>. The fallback is <paramref name="preferredFallback"/> when the
    /// inventory owns it, else the first other car; never one of <paramref name="notFallback"/> (the other modded cars,
    /// which their own plans take out of the same save). <paramref name="ownParts"/> are item ids only this car has (its
    /// liveries): such an item always comes out whole, whatever its states say, and its states go to the sidecar as
    /// this car's (an item of it without states as one unmounted part), since a game without the mod doesn't know the id.</summary>
    public static CarSavePlan Make(IReadOnlyList<CarSaveItem> items, uint carId, uint preferredFallback,
        IReadOnlyCollection<uint> notFallback = null, IReadOnlyCollection<uint> ownParts = null)
    {
        var plan = new CarSavePlan();
        if (items == null) return plan;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it == null) continue;
            if (it.ItemId == carId)
            {
                plan.RemoveItems.Add(i);
                if (it.IsVehicle)
                {
                    plan.CarOwned = true;
                    plan.CarInUse |= it.InUse;
                }
                continue;
            }
            if (ownParts != null && it.ItemId != 0 && Contains(ownParts, it.ItemId))
            {
                plan.RemoveItems.Add(i);
                if (it.States == null || it.States.Count == 0)
                    plan.Removed.Add(new CarSidecarState { ItemId = it.ItemId, Count = 1, InUse = it.InUse });
                else
                    foreach (var st in it.States)
                        if (st != null) plan.Removed.Add(new CarSidecarState { ItemId = it.ItemId, Count = st.Count, InUse = st.InUse });
                continue;
            }
            if (it.States == null || it.States.Count == 0) continue;
            int kept = 0;
            var mine = new List<int>();
            for (int s = 0; s < it.States.Count; s++)
            {
                var st = it.States[s];
                if (st != null && st.VehicleItemId == carId)
                {
                    mine.Add(s);
                    plan.Removed.Add(new CarSidecarState { ItemId = it.ItemId, Count = st.Count, InUse = st.InUse });
                }
                else kept++;
            }
            if (mine.Count == 0) continue;
            if (kept == 0) plan.RemoveItems.Add(i);
            else foreach (var s in mine) plan.RemoveStates.Add((i, s));
        }

        if (plan.CarInUse)
        {
            int first = -1, preferred = -1;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it == null || !it.IsVehicle || it.ItemId == carId) continue;
                if (notFallback != null && Contains(notFallback, it.ItemId)) continue;
                if (first < 0) first = i;
                if (it.ItemId == preferredFallback && preferred < 0) preferred = i;
            }
            plan.FallbackInUse = preferred >= 0 ? preferred : first;
        }
        return plan;
    }

    private static bool Contains(IReadOnlyCollection<uint> ids, uint id)
    {
        foreach (var i in ids) if (i == id) return true;
        return false;
    }
}

public sealed class CarSidecarState
{
    public uint ItemId;
    public int Count;
    public bool InUse;
}

/// <summary>
/// What the save filter took out, kept next to the BepInEx config (one small text file per car and profile:
/// oth.&lt;car id&gt;.&lt;profile&gt;.sidecar) so the car, its odometer and its parts come back when the save is loaded
/// with the mod. The header keeps the name of the format's first car (the Vera) for every car. Pure code (unit-tested).
/// </summary>
public sealed class CarSidecar
{
    private const string Header = "vera-sidecar 1";

    public bool Owned;
    public int MetersTravelled;
    public bool InUse;
    /// <summary>The car the save shows in use instead of the modded car.</summary>
    public uint FallbackId;
    public readonly List<CarSidecarState> States = new();
    /// <summary>Decoded: the file ended with its end line (written since files are replaced in one step).</summary>
    public bool Complete { get; private set; }
    /// <summary>Decoded: it had every fixed line (owned, km, inuse, fallback), as every file written so far has.</summary>
    public bool HasFixedFields { get; private set; }
    /// <summary>Safe to restore from: complete, or an older file (no end line) with all its fixed lines. A file cut
    /// short (a crash mid-write before writes were atomic) is not, and is set aside instead.</summary>
    public bool Usable => Complete || HasFixedFields;

    public string Encode()
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        sb.Append("owned=").Append(Owned ? 1 : 0).Append('\n');
        sb.Append("km=").Append(MetersTravelled.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("inuse=").Append(InUse ? 1 : 0).Append('\n');
        sb.Append("fallback=").Append(FallbackId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var s in States)
            sb.Append("state=").Append(s.ItemId.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(s.Count.ToString(CultureInfo.InvariantCulture)).Append(',').Append(s.InUse ? 1 : 0).Append('\n');
        sb.Append("end=1\n");
        return sb.ToString();
    }

    public static CarSidecar Decode(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Header) return null;
        var s = new CarSidecar();
        int fixedSeen = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            string l = lines[i];
            int eq = l.IndexOf('=');
            if (eq <= 0) continue;
            string k = l.Substring(0, eq), v = l.Substring(eq + 1).Trim();
            switch (k)
            {
                case "owned": s.Owned = v == "1"; fixedSeen |= 1; break;
                case "km": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out s.MetersTravelled)) fixedSeen |= 2; break;
                case "inuse": s.InUse = v == "1"; fixedSeen |= 4; break;
                case "fallback": if (uint.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out s.FallbackId)) fixedSeen |= 8; break;
                case "end": s.Complete = v == "1" && i >= lines.Length - 2; break;
                case "state":
                    var p = v.Split(',');
                    if (p.Length == 3 && uint.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id)
                        && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                        s.States.Add(new CarSidecarState { ItemId = id, Count = count, InUse = p[2] == "1" });
                    break;
            }
        }
        s.HasFixedFields = fixedSeen == 15;
        return s;
    }

    /// <summary>A file-name-safe key for a profile name (letters, digits, '-' and '_' only; "default" when empty).</summary>
    public static string FileKey(string profile)
    {
        if (string.IsNullOrEmpty(profile)) return "default";
        var sb = new StringBuilder();
        foreach (char c in profile)
        {
            if (c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_')) sb.Append(c);
            else if (c == ' ') sb.Append('_');
        }
        string k = sb.ToString().Trim('_');
        if (k.Length > 48) k = k.Substring(0, 48);
        return k.Length == 0 ? "default" : k;
    }
}
