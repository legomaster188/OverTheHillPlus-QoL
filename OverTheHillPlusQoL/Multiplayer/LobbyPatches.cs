using System;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Multiplayer;

/// <summary>
/// Bigger lobbies. The session's player limit itself is just a number the host picks, but the game
/// keeps players in fixed-size networked tables (order, colours, names). Those can't be resized by a
/// mod, so above their capacity we degrade gracefully (fallback spawn points, names) instead of crashing.
/// </summary>
internal static class LobbyRules
{
    public const int StockMaxPlayers = 4;
    public const int MaxPlayers = 16;

    public static int GuardedErrors, SpawnFallbacks;
    private static readonly HashSet<string> Warned = new();

    /// <summary>Largest player count every networked table can hold (stock 4 until measured in-game).</summary>
    public static int SafeCapacity => ModConfig.MeasuredCapacity.Value > 0 ? ModConfig.MeasuredCapacity.Value : StockMaxPlayers;

    public static string OptionLabel(int players) => players > SafeCapacity ? $"{players}  (experimental)" : players.ToString();

    public static Exception Guard(string where, Exception e)
    {
        if (e == null || !ModConfig.LobbyGuards.Value) return e;
        GuardedErrors++;
        if (Warned.Add(where)) Plugin.Logger.LogWarning($"[Lobby] Guarded {where}: {e.GetType().Name}: {e.Message} (further ones from here are counted, not logged)");
        return null;
    }
}

// ---- Create Server screen: 2..16 players ----

[HarmonyPatch(typeof(ContextUICreateServer), nameof(ContextUICreateServer.Start))]
internal static class CreateServerStartPatch
{
    internal static CarouselOption Carousel;
    internal static Il2CppSystem.Collections.Generic.List<string> Options;

    private static void Postfix(ContextUICreateServer __instance)
    {
        try
        {
            if (ModConfig.ExpandLobbySize.Value && __instance.MaxPlayersOptionSelector != null)
            {
                Options = new Il2CppSystem.Collections.Generic.List<string>();
                for (int i = 2; i <= LobbyRules.MaxPlayers; i++) Options.Add(i.ToString()); // plain ints: the game int.Parse()s these
                int index = Mathf.Clamp(ModConfig.DefaultMaxPlayers.Value, 2, LobbyRules.MaxPlayers) - 2;
                __instance.maxPlayersOptionsRange = Options;
                __instance.MaxPlayersOptionSelector.Initialize(Options, index);
                __instance.maxPlayersOptionsSelectedIndex = index;
                Carousel = __instance.MaxPlayersOptionSelector.CarouselOption;
                Relabel();
            }
            CreateServerButton.Add(__instance);
        }
        catch (Exception e) { Plugin.Logger.LogError("Create Server setup failed: " + e); }
    }

    internal static void Relabel()
    {
        try
        {
            if (Carousel == null || Carousel.Option == null) return;
            if (int.TryParse(Carousel.GetSelectedOption(), out int n)) Carousel.Option.text = LobbyRules.OptionLabel(n);
        }
        catch { /* carousel destroyed with the screen */ }
    }
}

[HarmonyPatch(typeof(CarouselOption), nameof(CarouselOption.InternalSelected))]
internal static class CarouselRelabelPatch
{
    private static void Postfix(CarouselOption __instance)
    {
        if (CreateServerStartPatch.Carousel != null && __instance.Pointer == CreateServerStartPatch.Carousel.Pointer)
            CreateServerStartPatch.Relabel();
    }
}

[HarmonyPatch(typeof(ContextUICreateServer), nameof(ContextUICreateServer.OnMaxPlayersChanged))]
internal static class MaxPlayersChangedPatch
{
    private static void Postfix() => CreateServerStartPatch.Relabel();
}

// ---- measuring the real capacity ----

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.Spawned))]
internal static class MeasureCapacityPatch
{
    private static void Postfix(OverlandSessionPlayers __instance)
    {
        try
        {
            int order = __instance.OrderedPlayerRefs.Length;
            int colors = __instance.PlayerColors.Length;
            int names = __instance.PlayerRefToNameDictionary.Capacity;
            int cap = Math.Min(order, Math.Min(colors, names));
            if (cap > 0 && cap != ModConfig.MeasuredCapacity.Value)
            {
                ModConfig.MeasuredCapacity.Value = cap;
                ModConfig.File.Save();
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] Could not measure capacity: " + e.Message); }
    }
}

// ---- graceful degradation above capacity ----

[HarmonyPatch(typeof(global::Lobby), nameof(global::Lobby.GetSpawnPoint))]
internal static class SpawnPointPatch
{
    private static Exception Finalizer(Exception __exception, global::Lobby __instance, PlayerRef playerRef,
        ref int spawnPointIndex, ref SpawnPoint __result)
    {
        if (!ModConfig.LobbyGuards.Value) return __exception;
        if (__exception == null && __result != null) return null;
        try
        {
            var points = __instance.spawnPoints;
            if (points == null || points.Count == 0) return __exception;
            int pick = -1;
            for (int i = 0; i < points.Count && pick < 0; i++)
            {
                var p = points[i];
                if (p != null && !p.IsOccupied && !p.IsInUseForSpawn) pick = i;
            }
            if (pick < 0) pick = Math.Abs(playerRef.AsIndex) % points.Count;
            spawnPointIndex = pick;
            __result = points[pick];
            LobbyRules.SpawnFallbacks++;
            return null;
        }
        catch { return __exception; }
    }
}

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.AddPlayer))]
internal static class AddPlayerGuard
{

    private static Exception Finalizer(Exception __exception, OverlandSessionPlayers __instance, PlayerRef playerRef)
    {
        var result = LobbyRules.Guard("AddPlayer", __exception);
        // The game's AddPlayer stopped at the 4-slot limit: finish the steps it skipped.
        if (__exception != null && result == null) BigLobby.FinishOverflowJoin(__instance, playerRef);
        return result;
    }
}

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.RemovePlayer))]
internal static class RemovePlayerGuard
{
    private static Exception Finalizer(Exception __exception) => LobbyRules.Guard("RemovePlayer", __exception);
}

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.RPC_ConnectedPlayer))]
internal static class ConnectedPlayerGuard
{
    private static Exception Finalizer(Exception __exception) => LobbyRules.Guard("RPC_ConnectedPlayer", __exception);
}

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.RPC_DisconnectedPlayer))]
internal static class DisconnectedPlayerGuard
{
    private static Exception Finalizer(Exception __exception) => LobbyRules.Guard("RPC_DisconnectedPlayer", __exception);
}

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.SetBusyState))]
internal static class BusyStateGuard
{
    private static Exception Finalizer(Exception __exception) => LobbyRules.Guard("SetBusyState", __exception);
}

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.GetPlayerName))]
internal static class PlayerNameGuard
{
    private static Exception Finalizer(Exception __exception, PlayerRef playerRef, ref string __result)
    {
        if (__exception == null && !string.IsNullOrEmpty(__result)) return null;
        if (!ModConfig.LobbyGuards.Value) return __exception;
        if (__exception != null) LobbyRules.Guard("GetPlayerName", __exception);
        __result = $"Player {playerRef.PlayerId}";
        return null;
    }
}
