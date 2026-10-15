using System;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.Stability;

/// <summary>
/// Debounced config saving. ModConfig turns BepInEx's save-on-every-set off (sliders and text fields change values
/// every frame), which left changes made outside the menu (HUD hotkey, settings set by code, entries other features
/// bind after startup) unsaved until the menu closed or the game quit cleanly. This writes the file once things have
/// been quiet for a moment, at most every couple of seconds, and never throws into the caller.
/// </summary>
internal sealed class ConfigAutosave : IFeature
{
    private const float QuietSeconds = 2f;

    private static bool _dirty;
    private static float _lastChange;
    private static bool _errorLogged;

    public void Register()
    {
        var file = ModConfig.File;
        if (file == null) return;
        file.SettingChanged += OnSettingChanged;
        // Entries bound by features after ModConfig's own save aren't in the file yet: write them once at startup.
        _dirty = true;
        Registry.OnTick(Tick);
    }

    private static void OnSettingChanged(object sender, EventArgs e)
    {
        _dirty = true;
        _lastChange = Time.unscaledTime;
    }

    private static void Tick()
    {
        if (!_dirty || Time.unscaledTime - _lastChange < QuietSeconds) return;
        SaveNow();
    }

    /// <summary>Writes the config now (menu close, quit). Safe to call any time; errors are logged once.</summary>
    public static void SaveNow()
    {
        _dirty = false;
        try { ModConfig.File?.Save(); }
        catch (Exception e)
        {
            // File locked by an editor/antivirus, read-only folder, ...: keep the values in memory and retry later.
            _dirty = true;
            _lastChange = Time.unscaledTime + 10f;
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning("Could not save the config (will retry): " + e.Message);
        }
    }
}
