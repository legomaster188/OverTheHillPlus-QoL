using UnityEngine;
using OverTheHillPlus.Features.Public.Chat;

namespace OverTheHillPlus.Chat;

/// <summary>
/// What the rest of the mods need to know about the chat box, told through core: no hotkeys while it is open (and the
/// frame after it closes, so the Enter / Escape that closed it isn't read as a hotkey), and the local player counts
/// as active while typing (the host's AFK rule).
/// </summary>
internal static class ChatFrame
{
    internal static void Install()
    {
        Features.Public.MenuUx.AddHotkeyBlocker(() => ChatOverlay.IsOpen || Time.frameCount - ChatOverlay.ClosedFrame <= 1);
        PlayerActivity.AddChattingCheck(() => ChatOverlay.IsOpen);
    }
}
