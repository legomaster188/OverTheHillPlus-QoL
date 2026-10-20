using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.Features.Public.ModNet;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.GearboxPlus;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The forward gears of every car in the session. Each input tick the game copies the car's own table
/// (FunselektorFusionVPStandardInput.forwardAllGears: the two ratios of 4H, and of 4L in manual) into the vehicle
/// physics gearbox, and that gearbox's table length is also what the manual gear buttons count up to and what the
/// automatic mode shifts through. So handing the component a longer table gives every car its extra gears with 4H / 4L
/// untouched: 4L in automatic still uses forwardSecondGearOnly (2nd gear's ratio alone), as the game does.
///
/// <para>Every car with that component is changed, not only yours: in a session the host simulates all cars and
/// your game predicts yours, so both need the same table (a Server mod; see <see cref="GearboxPlugin"/>). A car is
/// left alone, and given its own table back, when the mod is not active in the session, when it is kept stock, when it
/// belongs to another player whose game does not run Gearbox+ (the host never changes a car that player predicts with the
/// game's own gears, so it is never pulled back), or when anything about it fails (its ratios are no fixed step, a read
/// throws): driving never depends on this.</para>
/// </summary>
internal static class CarGears
{
    private sealed class Mine
    {
        /// <summary>The table the game made: what the car gets back when it goes stock.</summary>
        public Il2CppStructArray<float> Original;
        public float[] Stock;
        public Il2CppStructArray<float> Table;
        /// <summary>What the table was made for (mode, gear count or set): a table made for the same key needs no new one.</summary>
        public string Key;
    }

    private sealed class CarState
    {
        public bool Broken;
        public int Version = -1;
        public IntPtr TablePtr;
        /// <summary>The car is another player's and that player's game does not run Gearbox+ (looked at once a second).</summary>
        public bool Gated, AppliedGate;
        public float GateAt = -100f;
    }

    private static readonly Dictionary<IntPtr, Mine> Tables = new();
    private static readonly Dictionary<IntPtr, CarState> Cars = new();

    private static int _version;
    private static bool _active;
    private static int _setting = GearMath.StockGears;
    private static GearMode _mode = GearMode.Extend;
    private static string _stockCars = "";
    private static bool _loggedFail;

    /// <summary>Set by the next patch tick after a settings change: the cars to look at again.</summary>
    internal static int Version => _version;

    /// <summary>Gearbox+ runs in this session (solo, hosting, or a host that lists it).</summary>
    internal static bool Active => _active;

    /// <summary>Once per frame: reads the settings and the session; a change makes every car look again at its table.</summary>
    internal static void Refresh()
    {
        bool active = ModManager.ServerModActive(GearboxPlugin.ModId);
        int setting = GearMath.ClampGears(GearboxConfig.Gears.Value);
        string cars = GearboxConfig.StockCars.Value ?? "";
        var mode = GearboxConfig.Mode.Value;
        if (active == _active && setting == _setting && cars == _stockCars && mode == _mode) return;
        _active = active;
        _setting = setting;
        _stockCars = cars;
        _mode = mode;
        _version++;
    }

    /// <summary>The car's name as the game has it ("Car_Burro" without Unity's "(Clone)"), or null.</summary>
    internal static string NameOf(FunselektorFusionVPStandardInput input)
    {
        try
        {
            string n = input?.gameObject?.name;
            if (string.IsNullOrEmpty(n)) return null;
            return n.Replace("(Clone)", "").Trim();
        }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "CarGears.NameOf"); return null; }
    }

    /// <summary>The car's forward table is the one the settings ask for (from the input tick, before the game copies it).</summary>
    internal static void Ensure(FunselektorFusionVPStandardInput input)
    {
        if (input == null) return;
        var ptr = input.Pointer;
        if (!Cars.TryGetValue(ptr, out var car))
        {
            if (Cars.Count > 256) Cars.Clear();
            Cars[ptr] = car = new CarState();
        }
        if (car.Broken) return;
        try
        {
            var cur = input.forwardAllGears;
            if (cur == null) return;
            bool gated = Gated(input, car);
            if (car.Version == _version && car.TablePtr == cur.Pointer && car.AppliedGate == gated) return;
            Apply(input, cur, gated);
            car.Version = _version;
            car.AppliedGate = gated;
            car.TablePtr = input.forwardAllGears.Pointer;
        }
        catch (Exception e)
        {
            // This car goes back to the game's own gears and stays there.
            car.Broken = true;
            try { GiveBackStock(input); } catch (Exception ex) { DebugLog.Error(GearboxPlugin.ModId, ex, "CarGears.Ensure"); }
            DebugLog.Error(GearboxPlugin.ModId, e, "CarGears.Ensure");
            if (!_loggedFail)
            {
                _loggedFail = true;
                GearboxPlugin.Logger.LogWarning("Gearbox+: a car keeps its own gears after an error (further ones go to the debug log): " + e.Message);
            }
        }
    }

    /// <summary>
    /// On the host, a car that another real player drives and whose game does not run Gearbox+ stays as the game has it: that player
    /// predicts it with the game's own gears, so any other table would pull it back. Asked once a second per car. A guest only
    /// ever predicts its own car; driverless cars (AI, spawned) are the host's alone and follow the settings.
    /// </summary>
    private static bool Gated(FunselektorFusionVPStandardInput input, CarState car)
    {
        float now = Time.unscaledTime;
        if (now < car.GateAt && now >= car.GateAt - 1f) return car.Gated;
        car.GateAt = now + 1f;
        bool gated = false;
        try
        {
            if (GameState.IsAuthority && GameState.Runner != null)
            {
                var fp = input.fusionPlayer;
                if (fp != null && fp.Object != null && !fp.HasInputAuthority)
                {
                    var owner = fp.Object.InputAuthority;
                    gated = owner.IsRealPlayer && !ModNet.PeerHas(owner, CarDiffs.Channel);
                }
            }
        }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "CarGears.Gated"); }
        car.Gated = gated;
        return gated;
    }

    private static void Apply(FunselektorFusionVPStandardInput input, Il2CppStructArray<float> cur, bool gated)
    {
        bool curIsMine = Tables.TryGetValue(cur.Pointer, out var mine);
        // A modded car's tuning multiplies the gear table in place: its own ratios go back first, so what is read here (and kept
        // as the car's stock ratios) is what the game made.
        if (!curIsMine) ReleaseTuning(input);
        var original = curIsMine ? mine.Original : cur;
        float[] stock = curIsMine ? mine.Stock : ToArray(cur);

        string name = NameOf(input);
        bool keep = gated || (name != null && GearMath.IsKeptStock(_stockCars, name));
        float[] want;
        string key;
        var set = !keep && _active && _mode == GearMode.Realistic ? GearSets.For(name, ModdedLookup.GearSetOf) : null;
        if (set != null)
        {
            // The real gearbox of this car; a car it does not fit (four or more gears already, odd ratios) keeps its own.
            want = GearSets.Build(set, stock);
            key = "set:" + set.Id;
        }
        else
        {
            int gears = GearMath.GearsFor(_setting, keep, _active, stock.Length);
            want = GearMath.Extend(stock, gears);
            key = "extend:" + gears;
        }

        if (want.Length <= stock.Length)
        {
            if (curIsMine) input.forwardAllGears = original;
            return;
        }
        if (curIsMine && mine.Key == key) return;

        var table = new Il2CppStructArray<float>(want.Length);
        for (int i = 0; i < want.Length; i++) table[i] = want[i];
        // Never cleared: a table forgotten here could not be given back (a few floats per car and settings change).
        Tables[table.Pointer] = new Mine { Original = original, Stock = stock, Table = table, Key = key };
        input.forwardAllGears = table;
    }

    private static void ReleaseTuning(FunselektorFusionVPStandardInput input)
    {
        try { OverTheHillPlus.Cars.ModdedCars.ReleaseGears(input.GetComponent<VPVehicleController>()); }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "CarGears.ReleaseTuning"); }
    }

    /// <summary>The car's forward ratios as they are now, "5.67 : 3.90 : 2.50", or null when unknown.</summary>
    internal static string TableText(FunselektorFusionVPStandardInput input)
    {
        try
        {
            var cur = input?.forwardAllGears;
            if (cur == null) return null;
            var a = new float[cur.Length];
            for (int i = 0; i < a.Length; i++) a[i] = cur[i];
            return GearSets.Describe(a);
        }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "CarGears.TableText"); return null; }
    }

    private static void GiveBackStock(FunselektorFusionVPStandardInput input)
    {
        var cur = input.forwardAllGears;
        if (cur != null && Tables.TryGetValue(cur.Pointer, out var mine)) input.forwardAllGears = mine.Original;
    }

    private static float[] ToArray(Il2CppStructArray<float> arr)
    {
        var a = new float[arr.Length];
        for (int i = 0; i < a.Length; i++) a[i] = arr[i];
        return a;
    }

    /// <summary>The car runs a longer table than the game gave it (the extra gears are in use).</summary>
    internal static bool IsExtended(FunselektorFusionVPStandardInput input)
    {
        try
        {
            var cur = input?.forwardAllGears;
            return cur != null && Tables.TryGetValue(cur.Pointer, out var mine) && mine.Table.Length > mine.Stock.Length;
        }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "CarGears.IsExtended"); return false; }
    }

    /// <summary>How many forward gears the car has now (its table), or 0 when unknown.</summary>
    internal static int GearCount(FunselektorFusionVPStandardInput input)
    {
        try { return input?.forwardAllGears?.Length ?? 0; }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "CarGears.GearCount"); return 0; }
    }
}
