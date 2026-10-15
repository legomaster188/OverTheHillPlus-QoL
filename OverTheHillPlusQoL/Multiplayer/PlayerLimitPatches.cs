using System;
using Fusion;
using HarmonyLib;

namespace OverTheHillPlus.Multiplayer;

/// <summary>
/// Makes the chosen lobby size reach Photon. In testing, an "8 player" lobby still refused the 5th player with
/// GameIsFull, so the number is capped on the way from the Create Server screen to the session. These patches
/// re-apply the chosen size at each step and log every value, so the log shows exactly where it was capped.
/// </summary>
internal static class PlayerLimit
{
    /// <summary>The size picked in Create Server for the session we're about to host (0 = none pending).</summary>
    public static int Pending;

    /// <summary>
    /// Fusion copies its project config when a runner starts, so raise the player capacity on every config
    /// object it might copy from (the global config and the config asset).
    /// </summary>
    public static void RaiseFusionCapacity(int players)
    {
        void Raise(string what, NetworkProjectConfig cfg)
        {
            var sim = cfg?.Simulation;
            if (sim == null || sim.PlayerCount >= players) return;
            Plugin.Logger.LogInfo($"[Lobby] {what} player capacity {sim.PlayerCount} -> {players}");
            sim.PlayerCount = players;
        }
        try { Raise("Fusion global config", NetworkProjectConfig.Global); } catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] global config: " + e.Message); }
        try { Raise("Fusion config asset", NetworkProjectConfigAsset.Global?.Config); } catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] config asset: " + e.Message); }
    }
}

[HarmonyPatch(typeof(ContextUICreateServer), nameof(ContextUICreateServer.SetGameSetupContextValues))]
internal static class SetupContextPatch
{
    private static void Postfix(ContextUICreateServer __instance, ref GameSetupContext gameSetupContext)
    {
        try
        {
            if (!ModConfig.ExpandLobbySize.Value || gameSetupContext == null) return;
            var options = __instance.maxPlayersOptionsRange;
            int index = __instance.maxPlayersOptionsSelectedIndex;
            if (options == null || index < 0 || index >= options.Count || !int.TryParse(options[index], out int chosen)) return;
            Plugin.Logger.LogInfo($"[Lobby] Create Server: chosen {chosen}, game set MaxPlayers={gameSetupContext.MaxPlayers}");
            if (gameSetupContext.MaxPlayers != chosen) gameSetupContext.MaxPlayers = chosen;
            PlayerLimit.Pending = chosen;
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] SetGameSetupContextValues patch: " + e.Message); }
    }
}

[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.StartGame))]
internal static class OverlandStartGamePatch
{
    private static void Prefix(GameSetupContext gameSetupContext)
    {
        try
        {
            if (gameSetupContext == null) return;
            var sim = NetworkProjectConfig.Global?.Simulation;
            int configPlayers = sim?.PlayerCount ?? -1;
            Plugin.Logger.LogInfo($"[Lobby] StartGame: mode={gameSetupContext.GameMode} MaxPlayers={gameSetupContext.MaxPlayers} fusionConfigPlayers={configPlayers}");
            if (!ModConfig.ExpandLobbySize.Value || gameSetupContext.GameMode == GameMode.Client) return;

            if (PlayerLimit.Pending > 0 && gameSetupContext.MaxPlayers != PlayerLimit.Pending)
                gameSetupContext.MaxPlayers = PlayerLimit.Pending;
            int want = gameSetupContext.MaxPlayers;
            if (want <= 0) return;
            PlayerLimit.Pending = want;
            PlayerLimit.RaiseFusionCapacity(want);
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] StartGame patch: " + e.Message); }
    }
}

[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.StartGame))]
internal static class FusionStartGamePatch
{
    private static void Prefix(StartGameArgs args)
    {
        try
        {
            string count = args.PlayerCount.HasValue ? args.PlayerCount.Value.ToString() : "unset";
            Plugin.Logger.LogInfo($"[Lobby] Fusion StartGame: mode={args.GameMode} PlayerCount={count}");
            if (!ModConfig.ExpandLobbySize.Value || args.GameMode == GameMode.Client || PlayerLimit.Pending <= 0) return;
            PlayerLimit.RaiseFusionCapacity(PlayerLimit.Pending);
            if (!args.PlayerCount.HasValue || args.PlayerCount.Value != PlayerLimit.Pending)
            {
                args.PlayerCount = new Il2CppSystem.Nullable<int>(PlayerLimit.Pending);
                Plugin.Logger.LogInfo($"[Lobby] Fusion StartGame PlayerCount -> {PlayerLimit.Pending} (reads back {(args.PlayerCount.HasValue ? args.PlayerCount.Value.ToString() : "unset")})");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[Lobby] Fusion StartGame patch: " + e.Message); }
        finally { PlayerLimit.Pending = 0; }
    }
}

/// <summary>Logs the size Photon actually gave the room, once the session is up.</summary>
[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.Spawned))]
internal static class SessionSizeLogPatch
{
    private static void Postfix()
    {
        try
        {
            var r = GameState.Runner ?? UnityEngine.Object.FindObjectOfType<NetworkRunner>();
            var info = r?.SessionInfo;
            if (info != null) Plugin.Logger.LogInfo($"[Lobby] Session '{info.Name}' is open for {info.MaxPlayers} players ({info.PlayerCount} in now)");
        }
        catch (Exception e) { Plugin.Logger.LogDebug("[Lobby] session size: " + e.Message); }
    }
}
