using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.AltTab;

/// <summary>
/// No parking brake when you alt-tab. What the game does (decoded with Cpp2IL, see SpectateCamera): whenever the
/// input map changes, VehicleInput.OnInputMapChanged engages the auto handbrake if GameInput.MapShouldTriggerAutoHandbrake
/// is true for the new map. Losing focus switches the input map, so alt-tabbing parks your car. While the game window
/// isn't focused this answers "no" for that check, so the car just keeps rolling (no throttle, no steering, as before).
/// Patched by name at start-up: if the game renames the method, the option does nothing instead of breaking the mod.
/// </summary>
internal sealed class AltTabHandbrake : IFeature
{
    public static ConfigEntry<bool> Enabled;
    public static bool Installed { get; private set; }
    public static int Skipped { get; private set; }
    private static bool _logged;

    public void Register()
    {
        Enabled = ModConfig.File.Bind("QoL", "NoHandbrakeWhenAltTabbed", true,
            "Don't put the parking brake on when the game window loses focus (alt-tab).");
        Install();
    }

    private static void Install()
    {
        try
        {
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(AltTabHandbrake), nameof(Postfix)));
            var h = new Harmony(Plugin.Guid + ".alttab");
            foreach (var m in typeof(GameInput).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (m.Name != "MapShouldTriggerAutoHandbrake" || m.ReturnType != typeof(bool)) continue;
                h.Patch(m, postfix: postfix);
                Installed = true;
            }
            if (!Installed) Plugin.Logger.LogWarning("[QoL] GameInput.MapShouldTriggerAutoHandbrake not found: the alt-tab handbrake option does nothing");
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[QoL] Could not hook the auto handbrake: " + e.Message); }
    }

    private static void Postfix(ref bool __result)
    {
        if (!__result || Enabled == null || !Enabled.Value) return;
        bool focused;
        try { focused = Application.isFocused; } catch { return; }
        if (focused) return;
        __result = false;
        Skipped++;
        if (_logged) return;
        _logged = true;
        Plugin.Logger.LogInfo("[QoL] Window lost focus: kept the parking brake off (logged once)");
    }
}
