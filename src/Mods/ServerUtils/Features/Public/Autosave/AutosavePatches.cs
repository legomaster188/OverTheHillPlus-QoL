using System;
using HarmonyLib;

namespace OverTheHillPlus.Features.Public.Autosave;

/// <summary>Sees every save the game makes (its own autosaves, shop, exit, and the mod's queued saves).</summary>
[HarmonyPatch(typeof(OverlandSaver), nameof(OverlandSaver.SaveGame))]
internal static class AutosaveSaveGamePatch
{
    private static void Postfix()
    {
        try { AutosaveRuntime.OnGameSaveFinished(); }
        catch (Exception e) { DebugLog.Error("serverutils", e, "AutosaveSaveGamePatch.Postfix"); AutosaveRuntime.LogOnce("postfix", "save notice: " + e.Message); }
    }
}

/// <summary>Quit to menu (pause menu, kicked, disconnected): save first when the game would skip its exit save.</summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.QuitToMenuAsync))]
internal static class AutosaveQuitToMenuPatch
{
    private static void Prefix(OverlandNetworkRunner __instance)
    {
        try { AutosaveRuntime.BeforeQuit(__instance, "quitting to the menu"); }
        catch (Exception e) { DebugLog.Error("serverutils", e, "AutosaveQuitToMenuPatch.Prefix"); AutosaveRuntime.LogOnce("quitmenu", "save before quitting: " + e.Message); }
    }
}

/// <summary>Quit to desktop from a session.</summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.QuitGameAsync))]
internal static class AutosaveQuitGamePatch
{
    private static void Prefix(OverlandNetworkRunner __instance)
    {
        try { AutosaveRuntime.BeforeQuit(__instance, "quitting the game"); }
        catch (Exception e) { DebugLog.Error("serverutils", e, "AutosaveQuitGamePatch.Prefix"); AutosaveRuntime.LogOnce("quitgame", "save before quitting: " + e.Message); }
    }
}
