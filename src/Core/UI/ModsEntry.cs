using System;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.UI;

/// <summary>
/// The MODS entry in the game's ESC (pause) menu: a clone of the menu's first button (Resume), so it has the game's
/// look, hover animation and sounds. Decoded from the game (Cpp2IL, ContextUIPauseMenu): the menu's buttons are
/// ResumeButton, RestartTutorialButton, QuitTutorialButton, RestartTrailButton, PhotoModeButton, SettingsMenuButton,
/// FeedbackButton, ReturnToMainMenuButton, QuitGameButton and GuideButton; StatePauseMenu.Open wires their clicks
/// each time the menu opens, so the clone is made there too (after the game's own setup).
/// MODS goes just above "return to main menu" when that button shares the list, otherwise right under the template.
/// The button opens MODS in the game's MOD BROWSER (UI/GameMenu), or the full view when that can't be opened.
/// If the template can't be found, F7 still opens MODS and one warning is logged.
/// </summary>
internal static class ModsEntry
{
    internal const string PauseButtonName = "OTH_ModsPauseButton";
    private static bool _warned;

    internal static void AddToPauseMenu(StatePauseMenu menu)
    {
        try
        {
            var ctx = menu?.contextUIPauseMenu;
            var source = ctx == null ? null : ctx.ResumeButton;
            if (source == null)
            {
                Warn("the ESC menu's buttons were not found");
                return;
            }
            var parent = source.transform.parent;
            var quit = ctx.ReturnToMainMenuButton;
            int index = quit != null && quit.transform.parent == parent
                ? quit.transform.GetSiblingIndex()
                : source.transform.GetSiblingIndex() + 1;
            GameObject button = null;
            button = GameUiButtons.Clone(source, PauseButtonName, "MODS", index,
                () => ModsPage.OpenFromPauseMenu(menu, button));
        }
        catch (Exception e) { DebugLog.Error("core", e, "ModsEntry.AddToPauseMenu"); Warn(e.Message); }
    }

    private static void Warn(string why)
    {
        if (_warned) return;
        _warned = true;
        // While the old plugin is loaded its menu has the open key (CoreBehaviour ignores it then).
        string fallback = Core.Manager.ModManager.Blocked ? "the main menu's MOD BROWSER" : CoreConfig.OpenKey?.Value.ToString();
        Core.CorePlugin.Logger?.LogWarning($"MODS button not added to the ESC menu ({why}); {fallback} still opens MODS.");
    }
}

[HarmonyPatch(typeof(StatePauseMenu), nameof(StatePauseMenu.Open))]
internal static class PauseMenuModsButtonPatch
{
    private static void Postfix(StatePauseMenu __instance) => ModsEntry.AddToPauseMenu(__instance);
}
