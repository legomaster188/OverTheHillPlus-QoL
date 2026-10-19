using System;
using System.Collections.Generic;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OverTheHillPlus.Features.Public.GarageLift;

/// <summary>
/// Live ride height preview in the garage. While a suspension lift stage is highlighted in the Performance screen the
/// garage model's body rises (or drops) to that stage's ride height with a short ease, and goes back to the fitted
/// stage's height when the highlight leaves or the garage closes. The model's wheels stay on the floor, so the gap in
/// the arches shows how much the stage lifts. A line under the stage's description says it in centimetres.
///
/// Visual only: the only thing touched is the local position of the garage model's <c>Parts</c> node (body, cosmetics
/// and mounted upgrade objects; the wheels, axles and shadow sit under <c>Axles</c> and the root and stay). The model has
/// no physics, nothing is sent to other players and nothing is saved; the real car is untouched. The same node exists on
/// every stock garage model and on every modded car's (CarBody.Apply builds the model on a copy of its donor's), and
/// a stage adds its modifier to the car's own base travel (CarUpgradeFit.LiftBaseTravel), so it works the same for all.
///
/// The garage model also stands at the height of the fitted stage in the garage's other screens (that is how the car
/// leaves the garage). Numbers and the ease are in <see cref="LiftPreviewMath"/> / <see cref="LiftEase"/>.
/// </summary>
internal static class GarageLift
{
    private const string Mod = DrivingQol.DrivingQolPlugin.ModId;
    private const string PartsNode = "Parts";

    // The open garage and the highlighted stage.
    private static StateMerchantMenu _menu;
    private static UIMerchantButtonSelector _selected;
    private static UIMerchantButtonSelector _suppressed; // a stage the player just took off: no preview until the highlight moves
    private static float _previewLift;
    private static bool _hasPreview;
    private static float _fittedLift;
    private static float _baseTravel = float.NaN;

    // The garage model.
    private static IntPtr _modelPtr;
    private static Transform _parts;
    private static Vector3 _partsHome;
    private static float _applied = float.NaN;
    private static readonly LiftEase Ease = new();
    private static bool _missingLogged;

    // Text.
    private static string _suffix;           // what we appended to the description last
    private static int _reasserts;
    private static string _fallbackText;     // IMGUI line when the description could not take ours
    private static float _nextSlow;

    internal static bool Enabled => GarageLiftFeature.Preview != null && GarageLiftFeature.Preview.Value;

    // ---------------------------------------------------------------- events from the garage's methods

    internal static void Opened(StateMerchantMenu menu)
    {
        if (menu == null) return;
        _menu = menu;
    }

    internal static void Closed()
    {
        _selected = _suppressed = null;
        _hasPreview = false;
        Restore();
        _menu = null;
        _suffix = null;
        _fallbackText = null;
        _modelPtr = IntPtr.Zero;
        _applied = float.NaN;
    }

    /// <summary>Another garage screen (or car) is coming: no stage is highlighted any more.</summary>
    internal static void ScreenChanged(StateMerchantMenu menu)
    {
        Opened(menu);
        _selected = _suppressed = null;
        _hasPreview = false;
        _suffix = null;
        _fallbackText = null;
    }

    /// <summary>The garage highlighted an upgrade button.</summary>
    internal static void Selected(StateMerchantMenu menu, UIMerchantButtonSelector button)
    {
        if (!Enabled) return;
        Opened(menu);
        SetSelected(button, fromHook: true);
    }

    /// <summary>The garage left an upgrade button.</summary>
    internal static void Deselected(UIMerchantButtonSelector button)
    {
        if (button == null || _selected == null || button.Pointer != _selected.Pointer) return;
        _selected = null;
        _hasPreview = false;
        _fallbackText = null;
    }

    /// <summary>The player clicked an upgrade button (bought, fitted or took it off).</summary>
    internal static void Clicked(StateMerchantMenu menu, UIMerchantButtonSelector button)
    {
        if (!Enabled || button == null) return;
        Opened(menu);
        RefreshFitted();
        // Took the highlighted stage off: the car goes back to what is fitted, not on showing the stage that just left.
        if (_hasPreview && _selected != null && _selected.Pointer == button.Pointer && _previewLift > _fittedLift + 0.0005f)
        {
            _suppressed = button;
            _hasPreview = false;
            _fallbackText = null;
        }
    }

    /// <summary>The game wrote a stage's description: our line goes under it.</summary>
    internal static void DescriptionWritten(StateMerchantMenu menu, UIMerchantButtonSelector button)
    {
        if (!Enabled || !GarageLiftFeature.ShowText.Value || button == null) return;
        Opened(menu);
        _suffix = null;
        if (!TryStageLift(button, out float lift)) { _fallbackText = null; return; }
        RefreshFitted();
        Append(LiftPreviewMath.Line(lift, _fittedLift));
    }

    // ---------------------------------------------------------------- per frame

    internal static void Tick()
    {
        var menu = _menu;
        if (menu == null) return;
        try
        {
            if (!Enabled) { Restore(); return; }
            float now = Time.unscaledTime;
            if (now >= _nextSlow)
            {
                _nextSlow = now + 0.1f;
                Slow(menu);
            }
            Move(menu, Time.unscaledDeltaTime);
        }
        catch (Exception e)
        {
            DebugLog.Error(Mod, e, "GarageLift.Tick");
            Restore();
            _menu = null;
        }
    }

    internal static void Gui()
    {
        if (string.IsNullOrEmpty(_fallbackText) || _menu == null) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        try { OverlayDraw.Label(Screen.width * 0.5f, Screen.height - 46f, _fallbackText, 18, Theme.Text); }
        catch (Exception e) { DebugLog.Error(Mod, e, "GarageLift.Gui"); _fallbackText = null; }
    }

    /// <summary>Ten times a second: what is fitted, the highlighted button (a second way to find it, in case the
    /// game's selection callback was missed), and our line still being under the description.</summary>
    private static void Slow(StateMerchantMenu menu)
    {
        RefreshFitted();
        if (InPerformance(menu))
        {
            UIMerchantButtonSelector now = null;
            try
            {
                var go = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                if (go != null) now = go.GetComponent<UIMerchantButtonSelector>();
            }
            catch (Exception e) { DebugLog.Error(Mod, e, "GarageLift.Slow"); }
            if (now != null && (_selected == null || _selected.Pointer != now.Pointer)) SetSelected(now, fromHook: false);
        }
        else if (_hasPreview)
        {
            _hasPreview = false;
            _selected = null;
        }
        if (_suffix != null && _selected != null && GarageLiftFeature.ShowText.Value && _reasserts < 25)
        {
            var text = DescriptionText(menu);
            string cur = null;
            try { cur = text != null ? text.text : null; } catch (Exception e) { DebugLog.Error(Mod, e, "GarageLift.Slow"); }
            if (cur != null && !cur.EndsWith(_suffix, StringComparison.Ordinal))
            {
                // The game wrote the description again (a language change, a refresh): ours goes back under it.
                _reasserts++;
                string line = _suffix.TrimStart('\n');
                _suffix = null;
                Append(line);
            }
        }
    }

    private static void SetSelected(UIMerchantButtonSelector button, bool fromHook)
    {
        if (button == null) return;
        var menu = _menu;
        if (menu == null) return;
        if (_suppressed != null)
        {
            if (_suppressed.Pointer == button.Pointer) return; // the same button again (the game re-selects it after a click)
            _suppressed = null;
        }
        _selected = button;
        if (!InPerformance(menu) || !TryStageLift(button, out float lift))
        {
            _hasPreview = false;
            return;
        }
        RefreshFitted();
        _previewLift = lift;
        _hasPreview = true;
        if (DebugLog.IsVerbose)
            DebugLog.Verbose(Mod, $"garage lift: highlighted stage lifts {lift * 100f:0.#} cm, fitted {_fittedLift * 100f:0.#} cm, car travel " +
                $"{(float.IsNaN(_baseTravel) ? "?" : _baseTravel.ToString("0.###"))} m -> {FormatTravel(LiftPreviewMath.StageTravel(_baseTravel, lift))} m ({(fromHook ? "game callback" : "selection")})");
    }

    private static string FormatTravel(float t) => float.IsNaN(t) ? "?" : t.ToString("0.###");

    // ---------------------------------------------------------------- the model

    private static void Move(StateMerchantMenu menu, float dt)
    {
        GameObject model = null;
        try { model = menu.dummyChosenCarGameObject; }
        catch (Exception e) { DebugLog.Error(Mod, e, "GarageLift.Move"); }
        if (model == null)
        {
            _parts = null;
            _modelPtr = IntPtr.Zero;
            return;
        }
        float target = Target(menu);
        if (model.Pointer != _modelPtr)
        {
            // Another garage model (first one, or another car): find its body node and start at the right height.
            Restore();
            _modelPtr = model.Pointer;
            _applied = float.NaN;
            _parts = model.transform.Find(PartsNode);
            if (_parts == null)
            {
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    DebugLog.Info(Mod, $"garage lift: the garage model {model.name} has no '{PartsNode}' node: no lift preview on it.");
                }
                return;
            }
            _partsHome = _parts.localPosition;
            Ease.Jump(target);
        }
        if (_parts == null) return;

        float height = Ease.Step(target, dt, GarageLiftFeature.EaseSeconds.Value);
        if (!float.IsNaN(_applied) && Mathf.Abs(height - _applied) < 0.00001f) return;
        _applied = height;
        // metres in the model's space (its root could be scaled)
        float scale = _parts.parent != null ? Mathf.Abs(_parts.parent.lossyScale.y) : 1f;
        if (scale < 0.01f) scale = 1f;
        _parts.localPosition = _partsHome + new Vector3(0f, height / scale, 0f);
    }

    /// <summary>The height the garage model should be at: the highlighted stage in the Performance screen, else what is fitted.</summary>
    private static float Target(StateMerchantMenu menu)
    {
        if (_hasPreview && InPerformance(menu)) return _previewLift;
        return IsOwnCarShown(menu) ? _fittedLift : 0f;
    }

    /// <summary>Puts the body back where the game left it.</summary>
    private static void Restore()
    {
        try
        {
            if (_parts != null) _parts.localPosition = _partsHome;
        }
        catch (Exception) { /* the model was destroyed with the garage */ }
        _parts = null;
        _applied = float.NaN;
    }

    // ---------------------------------------------------------------- reading the game

    private static bool InPerformance(StateMerchantMenu menu)
    {
        try { return menu.currentSubMenu == StateMerchantMenu.MerchantSubMenu.PerformanceUpgrades; }
        catch (Exception e) { DebugLog.Error(Mod, e, "GarageLift.InPerformance"); return false; }
    }

    /// <summary>The garage model is the player's selected car (not a car being looked at in the vehicle list).</summary>
    private static bool IsOwnCarShown(StateMerchantMenu menu)
    {
        try
        {
            var sub = menu.currentSubMenu;
            return sub != StateMerchantMenu.MerchantSubMenu.None && sub != StateMerchantMenu.MerchantSubMenu.SelectVehicle;
        }
        catch (Exception e) { DebugLog.Error(Mod, e, "GarageLift.IsOwnCarShown"); return false; }
    }

    /// <summary>The lift of the suspension stage fitted on the selected car.</summary>
    private static void RefreshFitted()
    {
        var menu = _menu;
        if (menu == null) return;
        try
        {
            var car = menu.currentCar;
            if (car == null) { _fittedLift = 0f; return; }
            var mounted = new List<float>();
            var parts = car.MountedParts;
            if (parts != null)
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p != null && TryStageLift(p, out float lift)) mounted.Add(lift);
                }
            _fittedLift = LiftPreviewMath.Fitted(mounted);
            try
            {
                var setup = car.CarInfo?.suspensionSetup;
                if (setup != null) _baseTravel = setup.suspensionTravel;
            }
            catch (Exception e) { DebugLog.Error(Mod, e, "GarageLift.RefreshFitted"); }
        }
        catch (Exception e) { DebugLog.Error(Mod, e, "GarageLift.RefreshFitted"); }
    }

    private static bool TryStageLift(UIMerchantButtonSelector button, out float lift)
    {
        lift = 0f;
        var map = _menu?.buttonByInventoryItemSO;
        if (map == null || button == null || !map.ContainsKey(button)) return false;
        var item = map[button];
        return item != null && TryStageLift(item.TryCast<VehiclePartSO>(), out lift);
    }

    /// <summary>The lift a part gives: its suspension lift effects' modifiers added up. False for a part that is not a
    /// suspension upgrade.</summary>
    private static bool TryStageLift(VehiclePartSO part, out float lift)
    {
        lift = 0f;
        if (part == null) return false;
        var upgrade = part.TryCast<VehicleUpgrade>();
        if (upgrade == null || upgrade.UpgradeType != UpgradeType.Suspension) return false;
        var effects = part.Effects;
        if (effects == null) return false;
        var mods = new List<float>();
        for (int i = 0; i < effects.Count; i++)
        {
            var e = effects[i];
            var lifter = e?.TryCast<SuspensionLiftUpgradeEffect>();
            if (lifter != null) mods.Add(lifter.suspensionModifier);
        }
        if (mods.Count == 0) return false;
        lift = LiftPreviewMath.StageLift(mods);
        return true;
    }

    // ---------------------------------------------------------------- the text

    private static FunTextTMP DescriptionText(StateMerchantMenu menu)
    {
        try { return menu.merchantMenuUIContext?.DescriptionText; }
        catch (Exception e) { DebugLog.Error(Mod, e, "GarageLift.DescriptionText"); return null; }
    }

    private static void Append(string line)
    {
        var menu = _menu;
        var text = menu != null ? DescriptionText(menu) : null;
        if (text == null)
        {
            _fallbackText = line;
            return;
        }
        try
        {
            string cur = text.text ?? "";
            string suffix = "\n\n" + line;
            if (!cur.EndsWith(suffix, StringComparison.Ordinal)) text.text = cur + suffix;
            _suffix = suffix;
            _fallbackText = null;
        }
        catch (Exception e)
        {
            DebugLog.Error(Mod, e, "GarageLift.Append");
            _fallbackText = line;
        }
    }
}
