using System;
using UnityEngine;
using UnityEngine.InputSystem;
using VehiclePhysics;

namespace OverTheHillPlus.GearboxPlus;

/// <summary>
/// Your own car's gear buttons: the extra shift keys and the neutral key. They press the game's own gear-up / gear-down
/// handlers on your VehicleInput, so a shift travels as a normal button press in the car's network input (the host and
/// the prediction both see it), exactly as a press of the game's own binding would, and only works where the game does:
/// on the manual gearbox. Neutral is that press repeated, one step every 0.12 s, until the car's selected gear is 0
/// (<see cref="ReadButtonsPatch"/> makes the step between 1 and reverse stop there); it gives up after a few steps.
/// </summary>
internal static class Shifter
{
    private const float StepEvery = 0.12f;
    private const int MaxSteps = 8;

    private static IntPtr _car;
    private static FunselektorFusionVPStandardInput _input;
    private static VehicleInput _vehicleInput;

    private static bool _toNeutral;
    private static int _steps;
    private static float _nextStep;

    /// <summary>The car you drive, or null (its input components are looked up once per car).</summary>
    internal static FunselektorFusionVPStandardInput LocalInput
    {
        get
        {
            Refresh();
            return _input;
        }
    }

    internal static bool NeutralRunning => _toNeutral;

    private static void Refresh()
    {
        var car = GameState.LocalCar;
        var ptr = car != null ? car.Pointer : IntPtr.Zero;
        if (ptr == _car) return;
        _car = ptr;
        _input = null;
        _vehicleInput = null;
        _toNeutral = false;
        if (car == null) return;
        try
        {
            _input = car.GetComponent<FunselektorFusionVPStandardInput>();
            _vehicleInput = car.GetComponent<VehicleInput>();
        }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "Shifter.Refresh"); }
    }

    /// <summary>Once per frame, while driving: carries on with a neutral shift.</summary>
    internal static void Tick()
    {
        if (!_toNeutral) return;
        Refresh();
        if (_input == null || _vehicleInput == null || !GearboxConfig.Neutral.Value) { _toNeutral = false; return; }
        if (Time.unscaledTime < _nextStep) return;
        int step = GearMath.StepToNeutral(_input.SelectedManualGear);
        if (step == 0 || _steps >= MaxSteps) { _toNeutral = false; return; }
        _steps++;
        _nextStep = Time.unscaledTime + StepEvery;
        Press(step);
    }

    /// <summary>One press of the shift-up (+1) or shift-down (-1) button.</summary>
    internal static void Press(int direction)
    {
        Refresh();
        if (_vehicleInput == null || !CarGears.Active) return;
        try
        {
            var ctx = default(InputAction.CallbackContext);
            if (direction > 0) _vehicleInput.GearShiftUpPressed(ctx);
            else if (direction < 0) _vehicleInput.GearShiftDownPressed(ctx);
        }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "Shifter.Press"); }
    }

    /// <summary>The neutral key: steps to N from wherever the manual gearbox is.</summary>
    internal static void StartNeutral()
    {
        Refresh();
        if (_input == null || _vehicleInput == null || !CarGears.Active || !GearboxConfig.Neutral.Value) return;
        _toNeutral = true;
        _steps = 0;
        _nextStep = 0f;
    }

    // ---------------------------------------------------------------- inputs

    /// <summary>The keyboard keys of this frame (core calls this when hotkeys are allowed).</summary>
    internal static void Keys(Keyboard kb)
    {
        if (!CameraView.Driving || !CameraView.DrivingOnTop) return;
        if (CoreBehaviour.Pressed(kb, GearboxConfig.ShiftUpKey.Value)) Press(1);
        if (CoreBehaviour.Pressed(kb, GearboxConfig.ShiftDownKey.Value)) Press(-1);
        if (CoreBehaviour.Pressed(kb, GearboxConfig.NeutralKey.Value)) StartNeutral();
        if (CoreBehaviour.Pressed(kb, GearboxConfig.DiffLockKey.Value)) CarDiffs.ToggleLock();
    }

    /// <summary>The gamepad buttons of this frame.</summary>
    internal static void Pad()
    {
        if (GearboxConfig.ShiftUpPad.Value == PadButton.None && GearboxConfig.ShiftDownPad.Value == PadButton.None &&
            GearboxConfig.NeutralPad.Value == PadButton.None && GearboxConfig.DiffLockPad.Value == PadButton.None) return;
        var pad = Gamepad.current;
        if (pad == null || !CameraView.Driving || !CameraView.DrivingOnTop) return;
        if (Pressed(pad, GearboxConfig.ShiftUpPad.Value)) Press(1);
        if (Pressed(pad, GearboxConfig.ShiftDownPad.Value)) Press(-1);
        if (Pressed(pad, GearboxConfig.NeutralPad.Value)) StartNeutral();
        if (Pressed(pad, GearboxConfig.DiffLockPad.Value)) CarDiffs.ToggleLock();
    }

    private static bool Pressed(Gamepad pad, PadButton button)
    {
        try
        {
            switch (button)
            {
                case PadButton.DpadUp: return pad.dpad.up.wasPressedThisFrame;
                case PadButton.DpadDown: return pad.dpad.down.wasPressedThisFrame;
                case PadButton.DpadLeft: return pad.dpad.left.wasPressedThisFrame;
                case PadButton.DpadRight: return pad.dpad.right.wasPressedThisFrame;
                case PadButton.LeftStickPress: return pad.leftStickButton.wasPressedThisFrame;
                case PadButton.RightStickPress: return pad.rightStickButton.wasPressedThisFrame;
                case PadButton.North: return pad.buttonNorth.wasPressedThisFrame;
                default: return false;
            }
        }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "Shifter.Pressed"); return false; }
    }
}
