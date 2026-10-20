using System;
using HarmonyLib;
using OverTheHillPlus.ServerCreator;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.Features.Public.CreateScreen;

/// <summary>
/// Create Server's PLUS LOBBY SETTINGS button, right after Create: opens this mod's MODS page (whose Lobby size row
/// leads on to Lobby+'s page). Normal priority, so it is in place before <see cref="CreateScreenBuilder"/> adds the
/// rows (Lobby+ sets up the lobby size on the same screen).
/// </summary>
[HarmonyPatch(typeof(ContextUICreateServer), nameof(ContextUICreateServer.Start))]
internal static class CreateServerButton
{
    private static void Postfix(ContextUICreateServer __instance) => Add(__instance);

    private static void Add(ContextUICreateServer screen)
    {
        try
        {
            var source = screen.CreateButton;
            if (source == null) return;
            GameUiButtons.Clone(source, CreateScreenBuilder.PlusButtonName, "PLUS LOBBY SETTINGS",
                source.transform.GetSiblingIndex() + 1, () => ModsPage.Open(ServerCreatorPlugin.ModId));
        }
        catch (Exception e) { ServerCreatorPlugin.Logger.LogError("Create Server button failed: " + e); }
    }
}
