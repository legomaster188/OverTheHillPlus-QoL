using System;
using OverTheHillPlus.UI;
using OverTheHillPlus.UI.HudKit;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.GearboxPlus;

/// <summary>
/// The gear indicator: R N 1 2 3 4 with the gear you are in lit, and 4H / 4L beside it. Read once per frame from your
/// own car (the vehicle physics' gear, the car's forward table and its transfer case), drawn on Repaint only in the
/// mods' HUD panel look (HudLayout / Theme, as HUD+). Nothing is sent to anyone.
/// </summary>
internal static class GearboxHud
{
    private const float Cell = 26f, Height = 40f, Pad = 10f, RangeW = 36f, Gap = 6f, DiffH = 22f, DiffW = 214f;

    private static bool _has;
    private static int _gear, _gears = GearMath.StockGears;
    private static bool _low;
    private static string[] _slots = GearMath.Slots(GearMath.StockGears);
    private static int _slotsFor = GearMath.StockGears;
    private static float _nextCount, _nextDiffLine;
    private static string _diffLine;
    private static bool _loggedError;

    /// <summary>Once per frame: your car's gear, forward gear count and transfer case.</summary>
    internal static void Tick()
    {
        try
        {
            _has = false;
            if (!GearboxConfig.ShowIndicator.Value) return;
            var car = GameState.LocalCar;
            if (car == null) return;
            _gear = car.data.Get(Channel.Vehicle, VehicleData.GearboxGear);
            _has = true;
            var input = Shifter.LocalInput;
            if (input != null)
            {
                _low = (bool)input.GearboxIsLowTransferCase;
                if (Time.unscaledTime >= _nextCount)
                {
                    _nextCount = Time.unscaledTime + 1f;
                    int n = CarGears.GearCount(input);
                    _gears = n >= 1 ? Math.Min(n, GearMath.MaxGears) : GearMath.StockGears;
                }
            }
            if (_gears != _slotsFor) { _slotsFor = _gears; _slots = GearMath.Slots(_gears); }
        }
        catch (Exception e)
        {
            _has = false;
            DebugLog.Error(GearboxPlugin.ModId, e, "GearboxHud.Tick");
            if (_loggedError) return;
            _loggedError = true;
            GearboxPlugin.Logger.LogWarning("Gearbox+: reading the car for the indicator failed (further errors not logged): " + e.Message);
        }
    }

    /// <summary>False while <see cref="Draw"/> returns at once (its first checks; pure managed reads), so core skips it.</summary>
    internal static bool Wanted() => _has && GearboxConfig.ShowIndicator.Value;

    internal static void Draw()
    {
        if (!HudDraw.Repaint) return;
        if (!_has || !GearboxConfig.ShowIndicator.Value || CameraView.HudHidden) return;
        if (!HudDraw.Begin(GearboxPlugin.ModId)) return; // GearboxFrame closes the group
        Theme.Ensure();
        float scale = Mathf.Clamp(GearboxConfig.Scale.Value, 0.6f, 2f);
        var screen = HudLayout.Begin(scale);
        float bgA = HudLayout.Opacity(GearboxConfig.Opacity.Value, 0.2f);
        var bg = new Color(0f, 0f, 0f, PanelAlpha(bgA)); // the game's HUD panel black (40% at the default opacity)

        float w = Pad * 2f + _slots.Length * Cell + Gap + RangeW;
        // a second line with the differentials, while a setting other than the game's is in use
        bool diffs = GearboxConfig.ShowDiffs.Value && CarDiffs.HasLocal && !CarDiffs.Local.IsStock;
        float height = diffs ? Height + DiffH : Height;
        if (diffs) w = Mathf.Max(w, DiffW);
        var r = HudLayout.Place(GearboxConfig.Corner.Value, screen, w, height);
        Theme.Fill(r, bg, HudLayout.PanelRadius);
        int active = GearMath.ActiveSlot(_gear, _gears);
        float x = r.x + Pad, y = r.y + (Height - 28f) * 0.5f;
        for (int i = 0; i < _slots.Length; i++)
        {
            var cell = new Rect(x + i * Cell, y, Cell - 2f, 28f);
            bool on = i == active;
            // the gear you are in is the one lit in the game's yellow with dark text (the accent is for what is selected)
            if (on) Theme.Fill(cell, HudLook.Yellow, HudLayout.PanelRadius);
            Text(cell, _slots[i], on ? 18 : 15, on ? HudLook.Ink : HudLayout.Grey, true);
        }
        var range = new Rect(x + _slots.Length * Cell + Gap, y, RangeW, 28f);
        Text(range, GearMath.RangeText(_low), 15, _low ? HudLook.Amber : HudLayout.Cream, true);
        if (diffs)
        {
            var local = CarDiffs.Local;
            bool locked = local.LockAll;
            // Worked out a few times a second: a setting changes from the page, not from frame to frame.
            if (_diffLine == null || Time.unscaledTime >= _nextDiffLine)
            {
                _nextDiffLine = Time.unscaledTime + 0.25f;
                _diffLine = locked ? "DIFF LOCK ON" : "DIFF  " + DiffMath.Line(local, CarDiffs.LocalHasCentre);
            }
            Text(new Rect(r.x + Pad, r.y + Height - 2f, r.width - Pad * 2f, DiffH - 6f), _diffLine, 12, locked ? HudLook.Amber : HudLayout.Grey, true);
        }
        HudLayout.End();
    }

    /// <summary>The black's alpha for the Opacity setting: its default (0.82) is the game's 40%, below it fades out, above it
    /// goes up to solid at 1 (so a player's saved value keeps its meaning).</summary>
    internal static float PanelAlpha(float opacity) => HudRules.PanelAlpha(opacity);

    private static void Text(Rect r, string text, int size, Color color, bool bold)
    {
        var style = Theme.Value;
        if (HudDraw.Native)
        {
            // The game's gear readout: its heaviest HUD font.
            HudDraw.Label(r, text, style, new HudText(HudRules.NativeSize(size), color, TextAnchor.MiddleCenter, bold ? HudFont.Heavy : HudFont.Regular));
            return;
        }
        int prev = style.fontSize;
        var align = style.alignment;
        var prevColor = style.normal.textColor;
        style.fontSize = size;
        style.alignment = TextAnchor.MiddleCenter;
        style.normal.textColor = color;
        GUI.Label(r, bold ? "<b>" + text + "</b>" : text, style);
        style.fontSize = prev;
        style.alignment = align;
        style.normal.textColor = prevColor;
    }
}
