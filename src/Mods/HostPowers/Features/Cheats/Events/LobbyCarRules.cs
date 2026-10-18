using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.HostPowers;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Features.Cheats.Events;

/// <summary>
/// Host rules applied to every player's car. The host simulates every car, so these work on vanilla players.
///
/// - Same car for everyone: SpawnManager.RPC_ChangeCarAtCurrentPosition(player, carItemId, upgrades, livery) per player
///   (its body looks the player's object up with TryGetPlayerObject and swaps the car where it stands). Swaps are
///   staggered (one every <see cref="SwapInterval"/> s) so the host doesn't load several cars in one frame. Each
///   player's own car (item, upgrades, livery) is remembered from VehicleCustomizationController so it can be given
///   back. Only base-game cars are offered (no paid DLC car is ever handed to someone).
/// - Damage multiplier: CarDamageSystem.SetDamageModifier (its body only writes damageModifier, which scales impact
///   damage in OnImpactCollision; default 1). Each car's original value is remembered and written back when off.
/// - Grip: VehicleBase.SetWheelTireFrictionMultiplier on every wheel of every player car (nothing in the game sets it
///   except the optional VPTireFrictionMultiplier component, whose value is used as the base when present). Guests
///   predict their own car locally with stock grip, so they may see small corrections: marked experimental.
/// </summary>
internal static class LobbyCarRules
{
    private const float SwapInterval = 1.0f;
    private const float RetryCooldown = 20f;
    private const float EnforceInterval = 5f;

    private sealed class OwnCar
    {
        /// <summary>Who this car belongs to: Fusion reuses player slots, so the PlayerId alone isn't enough.</summary>
        public string Owner;
        public uint ItemId;
        public BitSet256 Upgrades;
        public long LiveryId;
        public int LiverySub;
    }

    private struct Swap
    {
        public PlayerRef Player;
        /// <summary>The player (<see cref="Who"/>) the swap was queued for; skipped if someone else has that slot by then.</summary>
        public string Owner;
        public uint ItemId;
        public BitSet256 Upgrades;
        public long LiveryId;
        public int LiverySub;
    }

    private static readonly Dictionary<int, OwnCar> Originals = new();
    private static readonly Queue<Swap> Pending = new();
    private static readonly HashSet<int> Queued = new();
    private static readonly Dictionary<int, float> NextTry = new();
    private static readonly Dictionary<int, float> LastSeen = new();
    private const float GoneAfter = 10f; // a player missing this long has left (not just swapping cars)
    private static float _nextSwap, _nextEnforce, _nextApply, _nextForget;

    /// <summary>The car everyone is being kept in (0 = no rule).</summary>
    public static uint TargetCar { get; private set; }
    public static string TargetName { get; private set; } = "";
    public static int PendingCount => Pending.Count;
    public static int RememberedCount => Originals.Count;

    private static readonly HashSet<string> Logged = new();

    private static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) HostPowersPlugin.Logger.LogWarning("[Events] " + message);
    }

    // ---- car list (base game only) ----

    private static List<ItemCatalog.Item> _cars;

    public static IReadOnlyList<ItemCatalog.Item> Cars
    {
        get
        {
            if (_cars != null && _cars.Count > 0) return _cars;
            var list = new List<ItemCatalog.Item>();
            try
            {
                foreach (var c in ItemCatalog.Cars())
                    if (IsBaseGame(c.Id)) list.Add(c);
            }
            catch (Exception e) { DebugLog.Error("hostpowers", e, "LobbyCarRules.Cars"); LogOnce("cars", "car list: " + e.Message); }
            return _cars = list;
        }
    }

    /// <summary>True only for a car that needs no DLC (unknown counts as DLC, so nothing paid slips through).</summary>
    private static bool IsBaseGame(uint id)
    {
        try
        {
            var cat = PlayerInventoryManager.InventoryItemsCatalog;
            if (cat?.IdToSO == null || !cat.IdToSO.ContainsKey(id)) return false;
            var so = cat.IdToSO[id];
            if (so == null) return false;
            return DlcManager.GetRequiredDlc(so) == DlcId.None && so.RequiredDlc == DlcId.None;
        }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "LobbyCarRules.IsBaseGame"); return false; }
    }

    private static OwnCar Read(VehicleBase car)
    {
        if (car == null) return null;
        try
        {
            var vcc = car.GetComponent<VehicleCustomizationController>();
            if (vcc == null) return null;
            var info = vcc.CurrentCarInfo;
            if (info == null) return null;
            var own = new OwnCar { ItemId = info.ItemId };
            try { own.Upgrades = vcc.EnabledUpgrades; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "LobbyCarRules.Read"); own.Upgrades = new BitSet256(); }
            try { own.LiveryId = vcc.EquippedModLiveryId; own.LiverySub = vcc.EquippedModLiverySubId; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "LobbyCarRules.Read"); }
            return own;
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "LobbyCarRules.Read"); LogOnce("read", "reading a player's car: " + e.Message); return null; }
    }

    // ---- same car for everyone ----

    public static string EveryoneIn(ItemCatalog.Item car)
    {
        if (!HostTools.Available) return "Blocked: host only.";
        if (car == null || !IsBaseGame(car.Id)) return "Blocked: only base-game cars can be handed out.";
        if (!SpawnReady(out var why)) return why;
        AdvantageFlag.MarkUsed();
        TargetCar = car.Id;
        TargetName = car.Name;
        Pending.Clear();
        Queued.Clear();
        NextTry.Clear();
        int n = QueueMismatched();
        _nextEnforce = Time.unscaledTime + EnforceInterval;
        return n == 0 ? $"Everyone is already in the {car.Name}." : $"Putting {n} player(s) in the {car.Name}…";
    }

    /// <summary>Queues a swap for every player whose car isn't the target, remembering their own car first.</summary>
    private static int QueueMismatched()
    {
        int n = 0;
        float now = Time.unscaledTime;
        ForgetLeft(now);
        foreach (var p in HostTools.Players)
        {
            if (p.Car == null) continue;
            int id = p.Ref.PlayerId;
            if (Queued.Contains(id)) continue;
            if (NextTry.TryGetValue(id, out var t) && now < t) continue;
            var own = Read(p.Car);
            if (own == null || own.ItemId == TargetCar) continue;
            own.Owner = Who(p);
            // A remembered car from someone else who had this slot is replaced by this player's own car.
            if (!Originals.TryGetValue(id, out var known) || known.Owner != own.Owner) Originals[id] = own;
            Pending.Enqueue(new Swap { Player = p.Ref, Owner = own.Owner, ItemId = TargetCar, Upgrades = new BitSet256() });
            Queued.Add(id);
            n++;
        }
        return n;
    }

    public static string GiveCarsBack()
    {
        if (!HostTools.Available) return "Blocked: host only.";
        TargetCar = 0;
        TargetName = "";
        Pending.Clear();
        Queued.Clear();
        NextTry.Clear();
        if (Originals.Count == 0) return "Rule off. No cars to give back.";
        if (!SpawnReady(out var why)) return why;
        int n = 0;
        foreach (var p in HostTools.Players)
        {
            if (!Originals.TryGetValue(p.Ref.PlayerId, out var own)) continue;
            // Only ever hand a car back to the player it was read from (a reused slot must not get someone else's
            // car, which could be a paid DLC car they don't own).
            string who = Who(p);
            if (own.Owner == null || own.Owner != who) continue;
            var now = Read(p.Car);
            if (now != null && now.ItemId == own.ItemId) continue;
            Pending.Enqueue(new Swap { Player = p.Ref, Owner = who, ItemId = own.ItemId, Upgrades = own.Upgrades, LiveryId = own.LiveryId, LiverySub = own.LiverySub });
            Queued.Add(p.Ref.PlayerId);
            n++;
        }
        Originals.Clear();
        LastSeen.Clear();
        AdvantageFlag.MarkUsed();
        return n == 0 ? "Rule off. Everyone already has their own car." : $"Rule off. Giving {n} player(s) their own car back…";
    }

    /// <summary>A player's identity for remembered cars: Photon user id (when the host can read it) plus name.</summary>
    private static string Who(HostTools.Player p)
    {
        string uid = null;
        try { uid = GameState.Runner?.GetPlayerUserId(p.Ref); } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "LobbyCarRules.Who"); }
        return (uid ?? "") + "|" + (p.Name ?? "");
    }

    /// <summary>
    /// Drops remembered cars, retries and queue marks of players who left (missing from the player list for
    /// <see cref="GoneAfter"/> s, so a car swap in progress doesn't count), and entries whose slot now holds someone else.
    /// </summary>
    private static void ForgetLeft(float now)
    {
        var players = HostTools.Players;
        if (players.Count == 0) return; // list unreadable this second: never treat that as everyone leaving
        var names = new Dictionary<int, string>();
        foreach (var p in players)
        {
            names[p.Ref.PlayerId] = Who(p);
            LastSeen[p.Ref.PlayerId] = now;
        }
        List<int> gone = null;
        foreach (var kv in Originals)
        {
            bool here = names.TryGetValue(kv.Key, out var name);
            bool missing = !here && (!LastSeen.TryGetValue(kv.Key, out var seen) || now - seen > GoneAfter);
            if (missing || (here && name != kv.Value.Owner)) (gone ??= new()).Add(kv.Key);
        }
        if (gone != null) foreach (var id in gone) Originals.Remove(id);
        gone = null;
        foreach (var id in LastSeen.Keys)
            if (!names.ContainsKey(id) && now - LastSeen[id] > GoneAfter) (gone ??= new()).Add(id);
        if (gone != null)
            foreach (var id in gone)
            {
                LastSeen.Remove(id);
                NextTry.Remove(id);
                Queued.Remove(id);
            }
    }

    private static bool SpawnReady(out string why)
    {
        why = null;
        try
        {
            var sm = SpawnManager.Instance;
            if (sm != null && sm.Object != null && sm.Object.IsValid) return true;
        }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "LobbyCarRules.SpawnReady"); }
        why = "Failed: load into a world first.";
        return false;
    }

    private static void RunSwaps(float now)
    {
        if (Pending.Count == 0 || now < _nextSwap) return;
        _nextSwap = now + SwapInterval;
        var s = Pending.Dequeue();
        int id = s.Player.PlayerId;
        Queued.Remove(id);
        NextTry[id] = now + RetryCooldown;

        bool present = false;
        foreach (var p in HostTools.Players)
            if (p.Ref.PlayerId == id && p.Car != null && Who(p) == s.Owner) { present = true; break; }
        if (!present || !SpawnReady(out _)) return;
        try
        {
            SpawnManager.Instance.RPC_ChangeCarAtCurrentPosition(s.Player, s.ItemId, s.Upgrades, s.LiveryId, s.LiverySub);
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "LobbyCarRules.RunSwaps"); LogOnce("swap", "car swap: " + e.Message); }
    }

    // ---- damage and grip ----

    private sealed class DamageEntry { public CarDamageSystem System; public float Original; }
    private sealed class GripEntry { public VehicleBase Car; }

    private static readonly Dictionary<IntPtr, DamageEntry> Damage = new();
    private static readonly Dictionary<IntPtr, GripEntry> Grip = new();

    public static int DamageCars => Damage.Count;
    public static int GripCars => Grip.Count;

    private static void ApplyDamage(float mult)
    {
        foreach (var p in HostTools.Players)
        {
            if (p.Car == null) continue;
            try
            {
                var cds = p.Car.GetComponent<CarDamageSystem>();
                if (cds == null) continue;
                if (!Damage.TryGetValue(cds.Pointer, out var e))
                    Damage[cds.Pointer] = e = new DamageEntry { System = cds, Original = cds.damageModifier };
                float want = e.Original * mult;
                if (Mathf.Abs(cds.damageModifier - want) > 0.0001f) cds.SetDamageModifier(want);
            }
            catch (Exception ex) { DebugLog.Error("hostpowers", ex, "LobbyCarRules.ApplyDamage"); LogOnce("dmg", "damage multiplier: " + ex.Message); }
        }
    }

    private static void RestoreDamage()
    {
        foreach (var e in Damage.Values)
        {
            try { if (e.System != null) e.System.SetDamageModifier(e.Original); } catch (Exception ex2) { DebugLog.Error("hostpowers", ex2, "LobbyCarRules.RestoreDamage"); }
        }
        Damage.Clear();
    }

    private static float GripBase(VehicleBase car)
    {
        try
        {
            var m = car.GetComponent<VPTireFrictionMultiplier>();
            if (m != null && m.enabled && m.frictionMultiplier > 0f) return m.frictionMultiplier;
        }
        catch (Exception ex2) { DebugLog.Error("hostpowers", ex2, "LobbyCarRules.GripBase"); }
        return 1f;
    }

    private static void SetGrip(VehicleBase car, float value)
    {
        int wheels = Math.Min(car.wheelCount, 16);
        for (int i = 0; i < wheels; i++) car.SetWheelTireFrictionMultiplier(i, value);
    }

    private static void ApplyGrip(float mult)
    {
        foreach (var p in HostTools.Players)
        {
            if (p.Car == null) continue;
            try
            {
                if (!Grip.ContainsKey(p.Car.Pointer)) Grip[p.Car.Pointer] = new GripEntry { Car = p.Car };
                // Re-applied every second: cheap, and catches a car that was swapped or respawned.
                SetGrip(p.Car, GripBase(p.Car) * mult);
            }
            catch (Exception ex) { DebugLog.Error("hostpowers", ex, "LobbyCarRules.ApplyGrip"); LogOnce("grip", "lobby grip: " + ex.Message); }
        }
    }

    private static void RestoreGrip()
    {
        foreach (var e in Grip.Values)
        {
            try { if (e.Car != null) SetGrip(e.Car, GripBase(e.Car)); } catch (Exception ex2) { DebugLog.Error("hostpowers", ex2, "LobbyCarRules.RestoreGrip"); }
        }
        Grip.Clear();
    }

    private static void Prune()
    {
        List<IntPtr> dead = null;
        foreach (var kv in Damage)
            if (kv.Value.System == null) (dead ??= new()).Add(kv.Key);
        if (dead != null) foreach (var k in dead) Damage.Remove(k);
        dead = null;
        foreach (var kv in Grip)
            if (kv.Value.Car == null) (dead ??= new()).Add(kv.Key);
        if (dead != null) foreach (var k in dead) Grip.Remove(k);
    }

    // ---- per frame ----

    /// <summary>Session changed: the cars are gone, nothing to restore.</summary>
    internal static void Forget()
    {
        Originals.Clear();
        Pending.Clear();
        Queued.Clear();
        NextTry.Clear();
        LastSeen.Clear();
        Damage.Clear();
        Grip.Clear();
        TargetCar = 0;
        TargetName = "";
    }

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        bool host = HostTools.Available;

        if (host) RunSwaps(now);
        if (host && Originals.Count > 0 && now >= _nextForget)
        {
            _nextForget = now + 2f;
            ForgetLeft(now);
        }

        if (host && TargetCar != 0 && EventsFeature.EnforceCar.Value && now >= _nextEnforce)
        {
            _nextEnforce = now + EnforceInterval;
            if (SpawnReady(out _)) QueueMismatched();
        }

        if (now < _nextApply) return;
        _nextApply = now + 1f;
        Prune();

        bool dmgOn = host && EventsFeature.DamageRule.Value;
        if (dmgOn)
        {
            AdvantageFlag.MarkUsed();
            ApplyDamage(Mathf.Clamp(EventsFeature.DamageMultiplier.Value, 0f, 10f));
        }
        else if (Damage.Count > 0) RestoreDamage();

        bool gripOn = host && EventsFeature.GripRule.Value;
        if (gripOn)
        {
            AdvantageFlag.MarkUsed();
            ApplyGrip(Mathf.Clamp(EventsFeature.GripMultiplier.Value, 0.2f, 3f));
        }
        else if (Grip.Count > 0) RestoreGrip();
    }
}
