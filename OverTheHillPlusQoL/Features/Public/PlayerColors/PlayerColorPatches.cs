using System;
using Fusion;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.PlayerColors;

/// <summary>
/// UIManager.GetPlayerColor is what the compass, map, pause menu, name billboards, join/leave notifications and
/// pinpoint icons use. For a player in the 4-entry PlayerColors table the result is left exactly as the game made
/// it; for anyone else (the game's single fallback colour) the generated palette colour is returned.
/// </summary>
[HarmonyPatch(typeof(UIManager), nameof(UIManager.GetPlayerColor))]
internal static class GetPlayerColorPatch
{
    private static void Postfix(PlayerRef playerRef, ref Color __result)
    {
        try
        {
            if (!PlayerPalette.Enabled || !playerRef.IsRealPlayer) return;
            if (PlayerPalette.TableIndex(playerRef) != -1) return; // players 1-4 (or no table yet): untouched
            if (PlayerPalette.TryGetOverflowColor(playerRef, out var c))
            {
                c.a = __result.a > 0f ? __result.a : 1f;
                __result = c;
            }
        }
        catch (Exception e) { PlayerPalette.LogOnce("GetPlayerColor", e); }
    }
}

/// <summary>
/// WorldSpacePinpoint.InstantiateFlare looks up the pinpoint's owner and calls GetPlayerFlare(owner), which hands
/// every player outside the table player 4's red flare. The owner passed to GetPlayerFlare is remembered and the
/// freshly made flare is recoloured to that player's palette colour.
/// </summary>
[HarmonyPatch(typeof(WorldSpacePinpoint), nameof(WorldSpacePinpoint.GetPlayerFlare))]
internal static class GetPlayerFlarePatch
{
    internal static PlayerRef LastAsked;
    internal static bool Asked;

    private static void Prefix(PlayerRef playerRef)
    {
        LastAsked = playerRef;
        Asked = true;
    }
}

[HarmonyPatch(typeof(WorldSpacePinpoint), nameof(WorldSpacePinpoint.InstantiateFlare))]
internal static class InstantiateFlarePatch
{
    private static void Prefix(WorldSpacePinpoint __instance, out IntPtr __state)
    {
        GetPlayerFlarePatch.Asked = false;
        __state = IntPtr.Zero;
        try
        {
            var current = __instance != null ? __instance.currentFlareGameObject : null;
            if (current != null) __state = current.Pointer;
        }
        catch { }
    }

    private static void Postfix(WorldSpacePinpoint __instance, IntPtr __state)
    {
        try
        {
            if (!GetPlayerFlarePatch.Asked) return;
            GetPlayerFlarePatch.Asked = false;
            if (!PlayerPalette.Enabled || !(PlayerColorsFeature.TintFlares?.Value ?? true)) return;

            var player = GetPlayerFlarePatch.LastAsked;
            if (!player.IsRealPlayer || PlayerPalette.TableIndex(player) != -1) return;

            var flare = __instance != null ? __instance.currentFlareGameObject : null;
            if (flare == null || flare.Pointer == __state) return; // nothing new was made
            if (PlayerPalette.TryGetOverflowColor(player, out var c)) FlareTint.Apply(flare, c);
        }
        catch (Exception e) { PlayerPalette.LogOnce("InstantiateFlare", e); }
    }
}
