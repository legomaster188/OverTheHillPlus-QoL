using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FunCore.Managers;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using OverTheHillPlus.ServerUtils;

namespace OverTheHillPlus.Features.Public.JoinSmoothing;

/// <summary>
/// When someone joins, the host loads their car (and lobby dummy) synchronously, which freezes the host for a
/// moment. Fusion then catches up several ticks with the host's last input, so the host's car keeps steering
/// ("stuck turning"). Preloading every car into the game's own resource cache in the background while hosting
/// makes those loads instant.
/// </summary>
internal sealed class CarPreloader : IFeature
{
    // Internal car names (prefabs are Prefabs/Cars/Car_<Name>, as seen in the game log).
    private static readonly string[] Cars =
    {
        "BlastWagon", "Boar", "Brick", "Bull", "Burro", "Cumbre", "Ding", "GrandWoodman", "Lynx", "Mammoth", "Marshal",
        "Polyana", "Protector", "Roamer", "RustOx", "Timberbox", "Tinny", "TrailBurner", "Trailrat", "Wombat", "Woodman",
    };

    internal static ConfigEntry<bool> Enabled => _enabled;
    private static ConfigEntry<bool> _enabled;
    internal static ConfigEntry<bool> PreloadEnabled => _enabled; // also shown in the Simple view (Hosting)
    private static readonly Queue<string> Pending = new();
    private static IntPtr _preparedForRunner;
    private static float _next;
    private static int _queued, _missing;

    public void Register()
    {
        _enabled = ServerUtilsConfig.File.Bind("JoinSmoothing", "PreloadCars", true,
            "While hosting, load every car into the game's cache in the background so players joining don't freeze the host.");
        ServerSettings.AddRow(ServerSettingRow.Toggle("smooth", "Smooth Joins", 80, ServerUtilsPlugin.ModId, () => _enabled));
        Registry.OnTick(Tick);
        Registry.Section(Registry.PlusMode, "Lobby", () =>
        {
            Menu.Card("Smooth joins", "Hosting: players joining no longer freeze your game or leave your steering stuck.", () =>
            {
                Menu.Switch(_enabled, "Preload cars while hosting", "Loads all cars in the background, one every half second", true);
                Menu.KeyValue("Cars preloaded", _queued == 0 ? "—" : $"{_queued - Pending.Count - _missing} / {Cars.Length * 2}");
            });
        }, 5);
    }

    private static void Tick()
    {
        if (!_enabled.Value || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.5f;

        var runner = GameState.Runner;
        if (runner == null || !GameState.IsAuthority) return;
        if (runner.Pointer != _preparedForRunner)
        {
            _preparedForRunner = runner.Pointer;
            Pending.Clear();
            _queued = _missing = 0;
            foreach (var car in Cars)
            {
                Enqueue(() => GameConstants.ADDRESSABLES_PATHS.GetCarPrefabAddress(car));
                Enqueue(() => GameConstants.ADDRESSABLES_PATHS.GetCarDummyPrefabAddress(car));
            }
        }
        if (Pending.Count == 0) return;

        string address = Pending.Dequeue();
        try
        {
            var rm = HybridSingleton<ResourcesManager>.Instance;
            if (rm == null || !rm.AddressablesInitialized()) { Pending.Enqueue(address); return; }

            if (!rm.TryResolveResourceLocation(address, out _)) { _missing++; return; }
            rm.LoadAndCacheAddressables(address, new Il2CppStringArray(0)); // async; fills the game's cache
        }
        catch (Exception e)
        {
            DebugLog.Error("serverutils", e, "CarPreloader.Tick");
            _missing++;
            ServerUtilsPlugin.Logger.LogDebug($"[JoinSmoothing] preload {address}: {e.Message}");
        }
    }

    private static void Enqueue(Func<string> address)
    {
        try
        {
            var a = address();
            if (!string.IsNullOrEmpty(a)) { Pending.Enqueue(a); _queued++; }
        }
        catch (Exception ex) { DebugLog.Error("serverutils", ex, "CarPreloader.Enqueue"); }
    }
}
