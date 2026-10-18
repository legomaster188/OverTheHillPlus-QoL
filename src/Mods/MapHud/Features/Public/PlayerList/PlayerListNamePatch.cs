using Fusion;
using HarmonyLib;

namespace OverTheHillPlus.Features.Public.PlayerList;

/// <summary>
/// The game's name table holds 7 players; past that GetPlayerName returns "". On the host the real name is still known,
/// so use it (runs before the lobby guard's "Player N" fallback, which only applies when the result is still empty).
/// </summary>
[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.GetPlayerName))]
internal static class PlayerListNameFallbackPatch
{
    [HarmonyPriority(Priority.Low)]
    private static void Postfix(PlayerRef playerRef, ref string __result)
    {
        if (!string.IsNullOrEmpty(__result)) return;
        try
        {
            if (PlayerListFeature.HostNames == null || !PlayerListFeature.HostNames.Value) return;
            if (PlayerListRoster.TryKnownName(playerRef, out var name)) __result = name;
        }
        catch (System.Exception ex) { DebugLog.Error("maphud", ex, "PlayerListNameFallbackPatch.Postfix"); }
    }
}
