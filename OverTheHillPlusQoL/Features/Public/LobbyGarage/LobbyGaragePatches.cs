using System;
using Fusion;
using HarmonyLib;

namespace OverTheHillPlus.Features.Public.LobbyGarage;

/// <summary>
/// Spawn point for extra players. Runs before the big-lobby fallback (a different free spot on every call, so a
/// car and its label could end up apart) and gives each extra player one stable spot instead.
/// </summary>
[HarmonyPatch(typeof(global::Lobby), nameof(global::Lobby.GetSpawnPoint))]
internal static class LobbyGarageSpawnPointPatch
{
    [HarmonyPriority(Priority.First)]
    private static Exception Finalizer(Exception __exception, global::Lobby __instance, PlayerRef playerRef,
        ref int spawnPointIndex, ref SpawnPoint __result)
    {
        if (!LobbyGarage.Enabled) return __exception;
        try
        {
            bool gameAnswered = __exception == null && __result != null;
            if (LobbyGarage.Resolve(__instance, playerRef, gameAnswered ? spawnPointIndex : -1, gameAnswered ? __result : null,
                    out int idx, out var point))
            {
                spawnPointIndex = idx;
                __result = point;
                return null;
            }
        }
        catch (Exception e) { LobbyGarage.LogOnce("spawn", "spawn point: " + e.Message); }
        return __exception;
    }
}

/// <summary>Lays the extra lobby spots out once the lobby (and the big-lobby clones) exist.</summary>
[HarmonyPatch(typeof(global::Lobby), nameof(global::Lobby.Awake))]
internal static class LobbyGarageAwakePatch
{
    [HarmonyPriority(Priority.Low)] // after the big-lobby postfix that clones the spawn points
    private static void Postfix(global::Lobby __instance) => LobbyGarage.OnLobbyAwake(__instance);
}

/// <summary>
/// Name labels and lobby cars. The game's version throws for the 5th player (4-entry label list), which also
/// skips the lobby fade-in when it runs from UpdateCarsStatusAndFadeIn, so the finalizer keeps that from ever
/// leaving the lobby black, even with the replacement switched off.
/// </summary>
[HarmonyPatch(typeof(StateLobbyMenu), nameof(StateLobbyMenu.UpdateCarsStatus))]
internal static class LobbyGarageCarsStatusPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(StateLobbyMenu __instance)
    {
        if (!LobbyGarage.Enabled) return true;
        try
        {
            LobbyGarage.UpdateCarsStatus(__instance);
            return false;
        }
        catch (Exception e)
        {
            LobbyGarage.LogOnce("status", "lobby labels, using the game's version: " + e.Message);
            return true;
        }
    }

    private static Exception Finalizer(Exception __exception)
    {
        if (__exception == null) return null;
        LobbyGarage.Swallowed++;
        LobbyGarage.LogOnce("status-game", $"UpdateCarsStatus threw {__exception.GetType().Name}: {__exception.Message}");
        return null;
    }
}

/// <summary>A player left (runs on every machine): free their extra spot.</summary>
[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.RPC_DestroyLobbyDummy))]
internal static class LobbyGarageDummyGonePatch
{
    private static void Postfix(PlayerRef playerRef) => LobbyGarage.Release(playerRef);
}
