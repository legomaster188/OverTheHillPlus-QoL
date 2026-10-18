using OverTheHillPlus.Features.Public.Photo;
using OverTheHillPlus.Features.Public.Spectate;

namespace OverTheHillPlus.Photo;

/// <summary>
/// What the rest of the mods need to know about the photo tools and the spectate camera, told through core: no mod
/// IMGUI while a screenshot is taken, the on-screen panels hidden in clean view, and the local player counts as present
/// while spectating (the host's AFK rule).
/// </summary>
internal static class PhotoFrame
{
    internal static void Install()
    {
        CoreBehaviour.AddHideGuiCheck(() => PhotoCapture.Capturing);
        CoreBehaviour.AddCleanViewCheck(() => PhotoCapture.CleanViewActive);
        PlayerActivity.AddSpectatingCheck(() => SpectateCamera.Active);
    }
}
