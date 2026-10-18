using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CommandTerminal;
using Fusion;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.CheatsPlus;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus;

/// <summary>Runs the game's built-in developer commands (CheatsController statics) directly, no console needed.</summary>
internal static class Cheats
{
    // Commands that wipe data or are otherwise not something to fire from a cheat menu.
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "DeleteSaveGamesAndSettings", "ModIoLogOut", "ResetTutorialCompletion", "RunAllFpsBenchmark",
        "ClearOnReload", "IsHost", "PrintEnabledLogTypes",
    };

    // Commands that change the shared world or target other players. Blocked only as a guest in a
    // public lobby; they work solo, hosting, and in friends' private sessions.
    private static readonly HashSet<string> AffectsOthers = new(StringComparer.OrdinalIgnoreCase)
    {
        "SetTime", "PauseTime", "SetWeather", "SetAurora", "LovelyDay", "SpawnAllMeteorites", "SpawnMeteoriteHere",
        "PauseMeteoritesSpawn", "SetCarDamage", "ResetPlayer", "TeleportHere", "SpawnCar", "DespawnCar",
        "ChangeSpawnedCarMaterial", "ChangeSpawnedCarCosmetics", "ScareAllAnimals", "IdleAllAnimals", "DespawnAllAnimals",
        "ClearBannedPlayers", "MechanicSleep", "MechanicWake", "DragByWinchPoint", "SetDustiness", "SetMuddiness",
        "SetSnowiness", "SetWetness", "ResetPointsOfInterest", "LetsKeepRolling", "SetPosition", "SetRotation",
        "ToggleCarVfxUpdate",
    };

    public static bool IsAffectingOthers(string command) => AffectsOthers.Contains(command);

    public static bool IsBlockedHere(string command) => GameState.IsPublicGuest && IsAffectingOthers(command);

    private static Dictionary<string, MethodInfo> _commands;

    public static IReadOnlyDictionary<string, MethodInfo> Commands => _commands ??= Discover();

    private static Dictionary<string, MethodInfo> Discover()
    {
        var argsType = typeof(Il2CppReferenceArray<CommandArg>);
        var result = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in typeof(CheatsController).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (Blocked.Contains(m.Name) || result.ContainsKey(m.Name)) continue;
            var p = m.GetParameters();
            if (p.Length == 1 && p[0].ParameterType == argsType) result[m.Name] = m;
        }
        CheatsPlugin.Logger.LogInfo($"Found {result.Count} built-in cheat commands");
        return result;
    }

    /// <summary>Runs "Command arg1 arg2 ...". Returns a status line for the menu.</summary>
    public static string Run(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return "";
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!Commands.TryGetValue(parts[0], out var method)) return $"Failed: unknown command '{parts[0]}'.";
        if (IsBlockedHere(parts[0]))
        {
            DebugLog.Info(CheatsPlugin.ModId, $"command '{parts[0]}' refused: it affects other players in a public lobby");
            return $"Blocked: {parts[0]} affects other players in a public lobby.";
        }

        var args = new Il2CppReferenceArray<CommandArg>(parts.Length - 1);
        for (int i = 1; i < parts.Length; i++)
        {
            var arg = new CommandArg();
            arg.String = parts[i];
            args[i - 1] = arg;
        }

        try
        {
            AdvantageFlag.MarkUsed();
            method.Invoke(null, new object[] { args });
            DebugLog.Info(CheatsPlugin.ModId, "ran command: " + line);
            CheatsPlugin.Logger.LogInfo("Ran command: " + line);
            return "Ran: " + line;
        }
        catch (Exception e)
        {
            DebugLog.Error("cheats", e, "Cheats.Run");
            var inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
            CheatsPlugin.Logger.LogWarning($"Command '{line}' failed: {inner}");
            return $"Failed: {inner.GetType().Name}: {inner.Message}";
        }
    }

    // ---- Built-in console ----

    public static string TryOpenConsole()
    {
        try
        {
            var t = FindSceneTerminal();
            if (t == null) t = CreateTerminal();
            if (t == null) return "No console available.";
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            if (!t.enabled) t.enabled = true;
            t.SetState(TerminalState.OpenSmall);
            return t.IsClosed ? "Console refused to open (see log)." : "Console opened. ` or \\ toggles it.";
        }
        catch (Exception e)
        {
            CheatsPlugin.Logger.LogError("TryOpenConsole: " + e);
            return "Failed: " + e.Message;
        }
    }

    private static Terminal FindSceneTerminal()
    {
        Terminal fallback = null;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Terminal>()))
        {
            var t = o.TryCast<Terminal>();
            if (t == null || !t.gameObject.scene.IsValid()) continue;
            if (t.gameObject.activeInHierarchy) return t;
            fallback ??= t;
        }
        return fallback;
    }

    private static Terminal CreateTerminal()
    {
        TerminalCommandDB db = null;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TerminalCommandDB>()))
        {
            db = o.TryCast<TerminalCommandDB>();
            if (db != null) break;
        }
        CheatsPlugin.Logger.LogInfo($"Creating console object (command DB found: {db != null})");
        var go = new GameObject("OTHPlus_Terminal");
        Object.DontDestroyOnLoad(go);
        go.SetActive(false);
        var t = go.AddComponent<Terminal>();
        if (db != null) t.commandDB = db;
        go.SetActive(true);
        return t;
    }

    public static string ConsoleStatus()
    {
        try
        {
            int terminals = 0;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Terminal>()))
                if (o.TryCast<Terminal>() is { } t && t.gameObject.scene.IsValid()) terminals++;
            string shell = Terminal.Shell == null ? "no shell" : $"{Terminal.Shell.Commands.Count} console cmds";
            return $"console objects: {terminals}, {shell}, CheatsManager: {(CheatsManager.Instance != null ? "yes" : "no")}";
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "Cheats.ConsoleStatus"); return "status error: " + e.Message; }
    }
}

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.IsDevTeamMember))]
internal static class DevTeamPatch
{
    private static void Postfix(PlayerRef playerRef, ref bool __result)
    {
        if (__result || !CheatsConfig.ForceDevConsole.Value) return;
        var runner = GameState.Runner;
        // Only ever flag the local player; other players are left alone.
        if (runner == null || runner.LocalPlayer.PlayerId == playerRef.PlayerId) __result = true;
    }
}

/// <summary>Applies the same public-lobby rule to commands typed into the built-in console.</summary>
[HarmonyPatch(typeof(CommandShell), nameof(CommandShell.RunCommand), new[] { typeof(string) })]
internal static class ConsoleCommandGuard
{
    private static bool Prefix(string line)
    {
        var name = (line ?? "").Trim().Split(' ')[0];
        if (!Cheats.IsBlockedHere(name)) return true;
        Terminal.Log($"{name} affects other players and is blocked in public lobbies.", new Il2CppReferenceArray<Il2CppSystem.Object>(0));
        return false;
    }
}
