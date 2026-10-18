using System;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.HostPowers;
using UnityEngine;

namespace OverTheHillPlus;

/// <summary>Host-side world controls (weather, time, wind), tools for everyone, and tents/campfires.</summary>
internal static class HostWorld
{
    private static float _nextLockCheck;
    private static List<(int id, string name)> _weathers;

    // ---- weather & time ----

    public static IReadOnlyList<(int id, string name)> Weathers
    {
        get
        {
            if (_weathers != null && _weathers.Count > 0) return _weathers;
            var list = new List<(int, string)>();
            try
            {
                var map = TimeManager.Instance?.cozyWeatherProfileIds?.IdToProfileName;
                if (map != null)
                    foreach (var kv in map) list.Add((kv.Key, Pretty(kv.Value)));
            }
            catch (Exception e) { DebugLog.Error("hostpowers", e, "HostWorld"); HostPowersPlugin.Logger.LogDebug("Weather list: " + e.Message); }
            list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
            return _weathers = list;
        }
    }

    public static int CurrentWeatherId
    {
        get { try { return TimeManager.Instance?.CurrentWeatherProfileId ?? -1; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "HostWorld.CurrentWeatherId"); return -1; } }
    }

    public static string WeatherName(int id) => Weathers.FirstOrDefault(w => w.id == id).name ?? $"#{id}";

    private static string Pretty(string s) => string.IsNullOrEmpty(s) ? "?" : s.Replace("_", " ").Replace("Profile", "").Trim();

    private static string Host(Func<string> action)
    {
        if (!HostTools.Available) return "Blocked: host only.";
        var tm = TimeManager.Instance;
        if (tm == null) return "Failed: load into a world first.";
        try { AdvantageFlag.MarkUsed(); return action(); }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "HostWorld.Host"); HostPowersPlugin.Logger.LogWarning("Host world action failed: " + e); return "Failed: " + e.Message; }
    }

    public static string SetWeather(int id) => Host(() =>
    {
        TimeManager.Instance.RPC_SetWeather(id);
        return $"Weather: {WeatherName(id)}";
    });

    public static string SetTime(int hours, int minutes) => Host(() =>
    {
        TimeManager.Instance.Cheat_SetTime(hours, minutes);
        return $"Time set to {hours:00}:{minutes:00}";
    });

    public static string SkipHours(int hours) => Host(() =>
    {
        TimeManager.Instance.ForwardTime(hours);
        return $"Skipped {hours} h";
    });

    public static string PauseTime(bool pause) => Host(() =>
    {
        TimeManager.Instance.Cheat_PauseTime(pause);
        return pause ? "Time paused" : "Time running";
    });

    public static string TimeSpeed(float scale) => Host(() =>
    {
        TimeManager.Instance.RPC_SetTimeScale(scale);
        return $"Time speed {scale:0.##}×";
    });

    public static string Aurora(bool on) => Host(() =>
    {
        TimeManager.Instance.Cheat_SetAurora(on);
        return on ? "Aurora tonight" : "Aurora off";
    });

    public static float Wind
    {
        get { try { return TimeManager.Instance?.WindAmount ?? 0f; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "HostWorld.Wind"); return 0f; } }
    }

    public static string SetWind(float amount) => Host(() =>
    {
        TimeManager.Instance.WindAmount = amount;
        return $"Wind {amount:0.00}";
    });

    /// <summary>Every 10 s while hosting: put the locked weather back if the game changed it.</summary>
    public static void Tick()
    {
        if (!HostPowersConfig.LockWeather.Value || HostPowersConfig.LockedWeatherId.Value < 0 || !HostTools.Available) return;
        if (Time.unscaledTime < _nextLockCheck) return;
        _nextLockCheck = Time.unscaledTime + 10f;
        if (CurrentWeatherId != HostPowersConfig.LockedWeatherId.Value) SetWeather(HostPowersConfig.LockedWeatherId.Value);
    }

    // ---- tools ----

    /// <summary>All usable tools in the game, for every player's car, then refilled.</summary>
    public static string GiveAllTools()
    {
        if (!HostTools.Available) return "Blocked: host only.";
        var tools = ItemCatalog.All.Where(i => i.Type == InventoryItemScriptableObject.ItemType.UsableItem).Select(i => i.Id).ToList();
        if (tools.Count == 0) return "Failed: tool list not loaded yet.";
        int cars = 0;
        AdvantageFlag.MarkUsed();
        foreach (var p in HostTools.Players)
        {
            var inv = p.Car?.GetComponent<CarInventory>();
            if (inv == null) continue;
            try
            {
                var slots = inv.AvailableItems;
                int n = Math.Min(slots.Length, tools.Count);
                for (int i = 0; i < n; i++) slots.Set(i, tools[i]);
                var arr = new Il2CppStructArray<uint>(n);
                for (int i = 0; i < n; i++) arr[i] = tools[i];
                try { inv.RPC_SetAvailableItems(arr); } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "HostWorld.GiveAllTools"); /* direct write above already applies on the host */ }
                inv.RPC_RefillItems();
                cars++;
            }
            catch (Exception e) { DebugLog.Error("hostpowers", e, "HostWorld.GiveAllTools"); HostPowersPlugin.Logger.LogWarning($"Tools for {p.Name}: {e.Message}"); }
        }
        return cars == 0 ? "Failed: no player cars found." : $"Gave all tools to {cars} player(s) ({tools.Count} tool types).";
    }

    // ---- camping ----

    public static bool OwnsSupporterEdition
    {
        get { try { return DlcManager.IsOwned(DlcId.SupporterEdition); } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "HostWorld.OwnsSupporterEdition"); return false; } }
    }

    private static bool InFrontOfCar(float distance, out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = default;
        var car = GameState.LocalCar;
        if (car == null) return false;
        var t = car.transform;
        var fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
        var probe = t.position + fwd * distance + Vector3.up * 20f;
        pos = Physics.Raycast(probe, Vector3.down, out var hit, 60f) ? hit.point : t.position + fwd * distance;
        rot = Quaternion.LookRotation(-fwd, Vector3.up);
        return true;
    }

    public static string SpawnTent(bool supporterStyle)
    {
        if (!HostTools.Available) return "Blocked: host only.";
        if (supporterStyle && !OwnsSupporterEdition) return "Blocked: the Supporter Edition tent needs the Supporter Edition DLC.";
        var cm = CampManager.Instance;
        if (cm == null) return "Failed: camping isn't available here.";
        if (!InFrontOfCar(7f, out var pos, out var rot)) return "Failed: no car.";
        try
        {
            AdvantageFlag.MarkUsed();
            cm.RPC_RequestSpawnTent(pos, rot, supporterStyle);
            return supporterStyle ? "Pitched a Supporter Edition tent." : "Pitched a tent.";
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "HostWorld.SpawnTent"); return "Failed: " + e.Message; }
    }

    public static string SpawnCampfire(bool supporterStyle)
    {
        if (!HostTools.Available) return "Blocked: host only.";
        if (supporterStyle && !OwnsSupporterEdition) return "Blocked: the Supporter Edition campfire needs the Supporter Edition DLC.";
        var cm = CampManager.Instance;
        var me = GameState.LocalPlayer;
        if (cm == null || me == null) return "Failed: camping isn't available here.";
        if (!InFrontOfCar(5f, out var pos, out var rot)) return "Failed: no car.";
        try
        {
            AdvantageFlag.MarkUsed();
            cm.RPC_RequestSpawnCampfire(me.Value, pos, rot, supporterStyle);
            return "Lit a campfire.";
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "HostWorld.SpawnCampfire"); return "Failed: " + e.Message; }
    }

    public static string EndCamping()
    {
        if (!HostTools.Available) return "Blocked: host only.";
        try { CampManager.Instance?.RPC_EndCamping(); return "Camping ended."; }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "HostWorld.EndCamping"); return "Failed: " + e.Message; }
    }
}
