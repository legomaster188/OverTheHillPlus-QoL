using System;
using BepInEx.Configuration;

namespace OverTheHillPlus.UI.Native;

internal enum MenuStyleKind
{
    Native,
    Classic,
}

/// <summary>
/// Which look the mod menu uses, and the automatic fallback: if anything in the native (game-styled uGUI) renderer
/// throws, the menu switches to the classic IMGUI panel for the rest of the session instead of breaking.
/// Settings are bound by <c>Features.Public.MenuStyle.MenuStyleFeature</c>; everything is null-safe if it didn't
/// register (then the classic look is used).
/// </summary>
internal static class NativeStyle
{
    internal static ConfigEntry<MenuStyleKind> Style;
    internal static ConfigEntry<bool> Sounds;
    internal static ConfigEntry<bool> GameSprites;
    internal static ConfigEntry<bool> KeyboardNav;

    /// <summary>Set when the native renderer failed this session; the menu then uses the classic look.</summary>
    internal static bool Failed { get; private set; }
    internal static string FailReason { get; private set; } = "";

    /// <summary>The user picked the native look and it hasn't failed.</summary>
    internal static bool Wanted => Style != null && Style.Value == MenuStyleKind.Native && !Failed;

    internal static bool SoundsOn => Sounds == null || Sounds.Value;
    internal static bool GameSpritesOn => GameSprites == null || GameSprites.Value;
    internal static bool KeyboardNavOn => KeyboardNav == null || KeyboardNav.Value;

    /// <summary>Falls back to the classic look for this session (logged once with the reason).</summary>
    internal static void Fail(string where, Exception e)
    {
        if (Failed) return;
        Failed = true;
        FailReason = where + ": " + (e?.Message ?? "unknown error");
        Plugin.Logger.LogError($"Native menu style failed ({where}), using the classic look this session: {e}");
        try { NativeCanvas.Hide(); } catch { /* already broken */ }
        try { Menu.SetStatus("Failed: native menu style (" + where + "), switched to Classic"); } catch { }
    }

    /// <summary>Clears a failure and rebuilds the native UI from scratch on the next frame.</summary>
    internal static void Retry()
    {
        Failed = false;
        FailReason = "";
        try { NativeCanvas.Destroy(); } catch (Exception e) { Plugin.Logger.LogDebug("Native menu reset: " + e.Message); }
        NativeLook.Reset();
    }
}
