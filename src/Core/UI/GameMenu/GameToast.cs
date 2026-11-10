using System;
using UnityEngine;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// A short message in the game's own notification (HUDManager.ShowNotification: the "X joined" / "X left" note at the
/// top of the HUD, with its coloured smile, fade and sound). The sound and timing are the game's own, taken from the
/// last notification it showed (core's ShowNotification prefix); until one was seen, the defaults below and no sound.
/// Only where the HUD is up (in the world, not the main menu); false otherwise, and the caller shows it another way.
/// </summary>
internal static class GameToast
{
    /// <summary>The game's yellow, for warnings.</summary>
    internal static readonly Color Warning = new(1f, 0.847f, 0.396f);

    private static string _sound;
    private static float _in = 0.3f, _hold = 5f, _out = 0.6f;
    private static bool _showing;

    /// <summary>The notification threw once: not tried again this session.</summary>
    internal static bool Broken { get; private set; }

    /// <summary>True while a notification can be shown (the HUD is up).</summary>
    internal static bool Possible
    {
        get
        {
            if (Broken) return false;
            try { return HUDManager.Instance != null; }
            catch { return false; }
        }
    }

    /// <summary>The game showed a notification: its sound and timing (not ours).</summary>
    internal static void Seen(string sound, float fadeIn, float hold, float fadeOut)
    {
        if (_showing) return;
        if (!string.IsNullOrEmpty(sound)) _sound = sound;
        if (fadeIn > 0f && fadeIn < 5f) _in = fadeIn;
        if (hold > 0f && hold < 30f) _hold = hold;
        if (fadeOut > 0f && fadeOut < 5f) _out = fadeOut;
    }

    /// <summary>
    /// The sound to pass: the one the game used last, else the game's own "player joined" sound. Never empty: the game
    /// plays the path from a tween callback without checking it, and an empty one threw there ("DOTWEEN: an error inside
    /// a tween callback", an InvalidKeyException with Keys= empty) on every toast shown before the game's first own
    /// notification, which in solo is every toast. Null when neither is known (then no toast: the caller shows it its
    /// own way).
    /// </summary>
    internal static string SoundPath()
    {
        if (!string.IsNullOrEmpty(_sound)) return _sound;
        try
        {
            string joined = GameConstants.ADDRESSABLES.Audio.PlayerJoined;
            return string.IsNullOrEmpty(joined) ? null : joined;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "GameToast.SoundPath"); return null; }
    }

    /// <summary>Shows <paramref name="text"/> (plain: markup is neutralised) for at least <paramref name="minSeconds"/>.</summary>
    internal static bool Show(string text, Color color, float minSeconds = 8f)
    {
        if (string.IsNullOrEmpty(text) || !Possible) return false;
        string sound = SoundPath();
        if (sound == null) return false;
        _showing = true;
        try
        {
            HUDManager.Instance.ShowNotification(color, ModsScreenModel.Plain(text), sound, _in, Mathf.Max(_hold, minSeconds), _out);
            return true;
        }
        catch (Exception e)
        {
            Broken = true;
            Core.CorePlugin.Logger?.LogWarning("[Core] the game's notification could not be shown (our own box shows instead): " + e.Message);
            return false;
        }
        finally { _showing = false; }
    }
}
