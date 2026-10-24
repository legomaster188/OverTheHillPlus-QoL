using System;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.BigLobbies;

namespace OverTheHillPlus.Multiplayer;

/// <summary>
/// Makes the chosen lobby size reach the network session: the patches re-apply the size picked in Create Server
/// at each step on the way from that screen to the running session.
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
            DebugLog.Info(BigLobbiesPlugin.ModId, $"{what}: player capacity {sim.PlayerCount} -> {players}");
            if (DevDetails.On) BigLobbiesPlugin.Logger.LogInfo($"[Lobby] {what} player capacity {sim.PlayerCount} -> {players}");
            sim.PlayerCount = players;
        }
        try { Raise("Fusion global config", NetworkProjectConfig.Global); } catch (Exception e) { DebugLog.Error("biglobbies", e, "PlayerLimit.RaiseFusionCapacity"); BigLobbiesPlugin.Logger.LogWarning("[Lobby] global config: " + e.Message); }
        try { Raise("Fusion config asset", NetworkProjectConfigAsset.Global?.Config); } catch (Exception e) { DebugLog.Error("biglobbies", e, "PlayerLimit.RaiseFusionCapacity"); BigLobbiesPlugin.Logger.LogWarning("[Lobby] config asset: " + e.Message); }
    }
}

[HarmonyPatch(typeof(ContextUICreateServer), nameof(ContextUICreateServer.SetGameSetupContextValues))]
internal static class SetupContextPatch
{
    private static void Postfix(ContextUICreateServer __instance, ref GameSetupContext gameSetupContext)
    {
        try
        {
            if (!BigLobbiesConfig.ExpandLobbySize.Value || gameSetupContext == null) return;
            var options = __instance.maxPlayersOptionsRange;
            int index = __instance.maxPlayersOptionsSelectedIndex;
            if (options == null || index < 0 || index >= options.Count || !int.TryParse(options[index], out int chosen)) return;
            DebugLog.Info(BigLobbiesPlugin.ModId, $"Create Server: chosen {chosen} players, the game set MaxPlayers {gameSetupContext.MaxPlayers}" + (gameSetupContext.MaxPlayers != chosen ? $" (changed to {chosen})" : ""));
            if (DevDetails.On) BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Create Server: chosen {chosen}, game set MaxPlayers={gameSetupContext.MaxPlayers}");
            if (gameSetupContext.MaxPlayers != chosen) gameSetupContext.MaxPlayers = chosen;
            PlayerLimit.Pending = chosen;
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SetupContextPatch.Postfix"); BigLobbiesPlugin.Logger.LogWarning("[Lobby] SetGameSetupContextValues patch: " + e.Message); }
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
            if (DevDetails.On)
            {
                var sim = NetworkProjectConfig.Global?.Simulation;
                int configPlayers = sim?.PlayerCount ?? -1;
                BigLobbiesPlugin.Logger.LogInfo($"[Lobby] StartGame: mode={gameSetupContext.GameMode} MaxPlayers={gameSetupContext.MaxPlayers} fusionConfigPlayers={configPlayers} room='{gameSetupContext.RoomName}'");
            }
            if (!BigLobbiesConfig.ExpandLobbySize.Value || gameSetupContext.GameMode == GameMode.Client) return;

            if (PlayerLimit.Pending > 0 && gameSetupContext.MaxPlayers != PlayerLimit.Pending)
                gameSetupContext.MaxPlayers = PlayerLimit.Pending;
            int custom = BigLobbiesConfig.CustomMaxPlayers.Value;
            if (custom >= 2) gameSetupContext.MaxPlayers = Math.Min(custom, LobbyRules.MaxCustomPlayers);
            int want = gameSetupContext.MaxPlayers;
            if (want <= 0) return;
            PlayerLimit.Pending = want;
            PlayerLimit.RaiseFusionCapacity(want);
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "OverlandStartGamePatch.Prefix"); BigLobbiesPlugin.Logger.LogWarning("[Lobby] StartGame patch: " + e.Message); }
    }
}

/// <summary>The session's player count. (Styling the session name is not this mod's job: see RoomNaming in Server Creator+.)</summary>
[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.StartGame))]
internal static class FusionStartGamePatch
{
    private static void Prefix(StartGameArgs args)
    {
        try
        {
            if (DevDetails.On)
            {
                string count = args.PlayerCount.HasValue ? args.PlayerCount.Value.ToString() : "unset";
                BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Fusion StartGame: mode={args.GameMode} PlayerCount={count} session='{args.SessionName}'");
            }
            if (!BigLobbiesConfig.ExpandLobbySize.Value || args.GameMode == GameMode.Client || PlayerLimit.Pending <= 0) return;
            PlayerLimit.RaiseFusionCapacity(PlayerLimit.Pending);
            // Required: without this the room opens at 4 even with the config raised (verified in-game). Reading the
            // value back through the interop wrapper shows 0, but the write does reach Fusion.
            args.PlayerCount = new Il2CppSystem.Nullable<int>(PlayerLimit.Pending);
            BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Fusion StartGame PlayerCount -> {PlayerLimit.Pending}");
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "FusionStartGamePatch.Prefix"); BigLobbiesPlugin.Logger.LogWarning("[Lobby] Fusion StartGame patch: " + e.Message); }
        finally { PlayerLimit.Pending = 0; }
    }
}

/// <summary>Logs the size Photon actually gave the room, once the session is up (with <see cref="DevDetails"/> on).</summary>
[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.Spawned))]
internal static class SessionSizeLogPatch
{
    private static bool Valid(Fusion.SessionInfo info)
    {
        try { return OverlandSessionInfo.IsSessionValid(info); } catch { return false; }
    }

    private static void Postfix()
    {
        // Always: one line per session, and it says whether the browser can list the server.
        try
        {
            var r = GameState.Runner ?? UnityEngine.Object.FindObjectOfType<NetworkRunner>();
            var info = r?.SessionInfo;
            if (info != null) BigLobbiesPlugin.Logger.LogInfo($"[Lobby] Session '{info.Name}' is open for {info.MaxPlayers} players ({info.PlayerCount} in now); listed in the browser: {(info.IsVisible ? "yes" : "NO (hidden)")}, joinable: {(info.IsOpen ? "yes" : "no")}, region {info.Region}, the game's browser accepts it: {(Valid(info) ? "yes" : "NO")}");
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SessionSizeLogPatch.Postfix"); BigLobbiesPlugin.Logger.LogDebug("[Lobby] session size: " + e.Message); }
    }
}
