using System;
using System.Collections.Generic;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.Graphics.Water;

/// <summary>
/// How dirty, snowy and wet cars look. From the decompiled code, <c>NetworkedMaterialManager.UpdateDustAmount(amount)</c>
/// (and the Mud/Sand/Snow/Wet/Rain versions) only push the value into the car's PropertyBlockSetter, i.e. the shader.
/// The networked "collected" values (which the game's logic reads) are never changed, so this is a purely local look.
/// </summary>
internal static class CarDirt
{
    private const int Dirt = 0, Snow = 1, Wet = 2;

    private sealed class Info
    {
        public bool IsCar;
        public IntPtr Vehicle;
    }

    private static readonly Dictionary<IntPtr, Info> Cache = new();
    private static float _cacheClearAt;
    private static int _applied; // matches the initial WaterCfg.Version, so nothing runs while off
    private static bool _active;

    public static int CarCount { get; private set; } = -1;

    public static void InstallPatches() => Gfx.EnsurePatched("cardirt", h =>
    {
        var t = typeof(NetworkedMaterialManager);
        var f = new[] { typeof(float) };
        Gfx.Patch(h, t, nameof(NetworkedMaterialManager.UpdateDustAmount), f, typeof(CarDirt), prefix: nameof(DirtPrefix));
        Gfx.Patch(h, t, nameof(NetworkedMaterialManager.UpdateMudAmount), f, typeof(CarDirt), prefix: nameof(DirtPrefix));
        Gfx.Patch(h, t, nameof(NetworkedMaterialManager.UpdateSandAmount), f, typeof(CarDirt), prefix: nameof(DirtPrefix));
        Gfx.Patch(h, t, nameof(NetworkedMaterialManager.UpdateSnowAmount), f, typeof(CarDirt), prefix: nameof(SnowPrefix));
        Gfx.Patch(h, t, nameof(NetworkedMaterialManager.UpdateWetAmount), f, typeof(CarDirt), prefix: nameof(WetPrefix));
        Gfx.Patch(h, t, nameof(NetworkedMaterialManager.UpdateRainAmount), f, typeof(CarDirt), prefix: nameof(WetPrefix));
    });

    public static void Tick()
    {
        bool on = WaterCfg.DirtOn.Value;
        if (on) InstallPatches();
        if (Time.unscaledTime >= _cacheClearAt)
        {
            _cacheClearAt = Time.unscaledTime + 20f;
            Cache.Clear(); // objects come and go with lobbies; pointers can be reused
        }
        if (on != _active)
        {
            _active = on;
            _applied = -1; // turning off also needs one refresh, to put the game's values back
        }
        if (!on && _applied != -1) return;
        if (_applied != WaterCfg.Version && Time.unscaledTime - WaterCfg.LastChange > 0.25f)
        {
            _applied = WaterCfg.Version;
            Refresh();
        }
    }

    private static void DirtPrefix(NetworkedMaterialManager __instance, ref float amount) => Scale(__instance, ref amount, Dirt);
    private static void SnowPrefix(NetworkedMaterialManager __instance, ref float amount) => Scale(__instance, ref amount, Snow);
    private static void WetPrefix(NetworkedMaterialManager __instance, ref float amount) => Scale(__instance, ref amount, Wet);

    private static void Scale(NetworkedMaterialManager mgr, ref float amount, int kind)
    {
        if (!WaterCfg.DirtOn.Value || mgr == null) return;
        try
        {
            var info = Lookup(mgr);
            if (!info.IsCar) return;
            if (WaterCfg.OnlyMine.Value && (info.Vehicle == IntPtr.Zero || info.Vehicle != GameState.LocalCarPtr)) return;
            float f = kind switch { Dirt => WaterCfg.DirtScale.Value, Snow => WaterCfg.CarSnow.Value, _ => WaterCfg.CarWet.Value };
            if (Gfx.Near(f, 1f)) return;
            amount = Mathf.Clamp(amount * f, 0f, Mathf.Max(1f, amount));
        }
        catch (Exception e) { Gfx.Once("dirt", "car dirt hook failed: " + e.Message); }
    }

    private static Info Lookup(NetworkedMaterialManager mgr)
    {
        if (Cache.TryGetValue(mgr.Pointer, out var info)) return info;
        info = new Info();
        var car = mgr.TryCast<CarNetworkedMaterialManager>();
        if (car != null)
        {
            info.IsCar = true;
            try
            {
                var v = car.vehicle;
                info.Vehicle = v == null ? IntPtr.Zero : v.Pointer;
            }
            catch { }
        }
        Cache[mgr.Pointer] = info;
        return info;
    }

    /// <summary>Re-sends each car's current stain values through the patched methods (restores them when off).</summary>
    private static void Refresh()
    {
        Cache.Clear();
        var cars = Gfx.Find<CarNetworkedMaterialManager>();
        CarCount = cars.Count;
        foreach (var c in cars)
        {
            try
            {
                c.UpdateDustAmount();
                c.UpdateMudAmount();
                c.UpdateSandAmount();
                c.UpdateSnowAmount();
                c.UpdateWetAmount();
                c.UpdateRainAmount();
            }
            catch (Exception e) { Gfx.Once("dirt-refresh", "car look refresh skipped (car not spawned yet?): " + e.Message); }
        }
    }
}
