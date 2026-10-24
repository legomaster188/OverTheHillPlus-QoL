using System;
using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus;

/// <summary>
/// The driving HUD (speed, RPM, gear; drawn by Map &amp; HUD) as the other mods see it, through core so they don't
/// reference that mod: its settings, so others can place their panels around it or show its switch, and one extra line
/// another mod may add at its bottom.
/// </summary>
internal static class DrivingHud
{
    /// <summary>[General] ShowHud of Map &amp; HUD, set when that mod binds it; null while it is switched off.</summary>
    internal static ConfigEntry<bool> ShowSetting;

    /// <summary>[General] HudKey of Map &amp; HUD (shows / hides the HUD); null while that mod is switched off.</summary>
    internal static ConfigEntry<Key> KeySetting;

    /// <summary>True while the HUD is switched on (it sits in the top right corner, see <see cref="Height"/>).</summary>
    internal static bool Shown => ShowSetting != null && ShowSetting.Value;

    /// <summary>The HUD panel's height in 1080p units, from 24 px below the top of the screen.</summary>
    internal const float Height = 150f;

    /// <summary>
    /// Text (rich text) for the HUD's last line, asked for on each repaint; null or empty leaves the line blank. Set
    /// by at most one mod, e.g. a live tyre readout.
    /// </summary>
    internal static Func<string> ExtraLine;
}
