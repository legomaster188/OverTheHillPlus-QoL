using System;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XInput;

namespace OverTheHillPlus.ControllerFeel;

/// <summary>
/// The only place that writes to the gamepad's motors. The Input System gives every pad two motors
/// (<c>Gamepad.SetMotorSpeeds(low, high)</c>: low = the heavy motor, high = the light one; DualShock and DualSense pads take the
/// same call). Trigger motors (Xbox impulse triggers) exist in the Input System only as <c>IXboxOneRumble</c>, which the
/// Windows XInput pad classes do not implement; this class asks the current pad once and uses the interface only if it
/// answers. DualSense adaptive triggers are not exposed by the Input System at all, so a DualSense gets the two motors.
/// Every write is guarded; <see cref="Stop"/> puts all motors to 0.
/// </summary>
internal static class PadOutput
{
    private static IntPtr _padPtr;
    private static Gamepad _pad;
    private static IXboxOneRumble _impulse;
    private static bool _wrote;
    private static float _lf, _hf, _lt, _rt;
    private static bool _loggedError;

    /// <summary>The current pad has trigger motors the Input System can drive.</summary>
    public static bool TriggersAvailable { get; private set; }

    /// <summary>The pad being driven ("Xbox Controller", "DualSense Wireless Controller", ...), or null.</summary>
    public static string PadName { get; private set; }

    /// <summary>The motors are on (something non-zero was written and not yet stopped).</summary>
    public static bool Running => _wrote;

    public static Gamepad Current()
    {
        try { return Gamepad.current; }
        catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "PadOutput.Current"); return null; }
    }

    private static void Bind(Gamepad pad)
    {
        if (pad == null)
        {
            _padPtr = IntPtr.Zero;
            _pad = null;
            _impulse = null;
            TriggersAvailable = false;
            PadName = null;
            return;
        }
        if (pad.Pointer == _padPtr) return;
        // Another pad: the old one must not be left rumbling.
        if (_wrote && _pad != null) ZeroOn(_pad, _impulse);
        _wrote = false;
        _lf = _hf = _lt = _rt = 0f;
        _padPtr = pad.Pointer;
        _pad = pad;
        _impulse = null;
        try { PadName = pad.displayName; } catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "PadOutput.Bind"); PadName = "gamepad"; }
        try { _impulse = pad.TryCast<IXboxOneRumble>(); }
        catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "PadOutput.Bind"); _impulse = null; }
        TriggersAvailable = _impulse != null;
        DebugLog.Info(ControllerFeelPlugin.ModId, $"pad: {PadName}, trigger motors {(TriggersAvailable ? "available" : "not available (two motors)")}");
    }

    private static void ZeroOn(Gamepad pad, IXboxOneRumble impulse)
    {
        try
        {
            if (impulse != null) impulse.SetMotorSpeeds(0f, 0f, 0f, 0f);
            pad.SetMotorSpeeds(0f, 0f);
        }
        catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "PadOutput.ZeroOn"); }
    }

    /// <summary>
    /// Sets the motors. Written on every call (the game writes its own rumble each frame too, so a value that is the same as
    /// our last one may not be what the pad has now). Returns false when there is no pad.
    /// </summary>
    public static bool Write(in FeelOutput o)
    {
        var pad = Current();
        Bind(pad);
        if (pad == null) return false;
        try
        {
            bool trig = _impulse != null && (o.LeftTrigger > 0.0005f || o.RightTrigger > 0.0005f || _lt > 0f || _rt > 0f);
            if (trig) _impulse.SetMotorSpeeds(o.LF, o.HF, o.LeftTrigger, o.RightTrigger);
            else pad.SetMotorSpeeds(o.LF, o.HF);
            _lf = o.LF;
            _hf = o.HF;
            _lt = o.LeftTrigger;
            _rt = o.RightTrigger;
            _wrote = !o.IsZero;
        }
        catch (Exception ex)
        {
            DebugLog.Error(ControllerFeelPlugin.ModId, ex, "PadOutput.Write");
            if (!_loggedError)
            {
                _loggedError = true;
                ControllerFeelPlugin.Logger?.LogWarning("[Controller Feel] writing to the gamepad failed (further errors not logged): " + ex.Message);
            }
            return false;
        }
        return true;
    }

    /// <summary>All motors to 0 now (pause, menu, car change, quit, mod switched off).</summary>
    public static void Stop(string why)
    {
        bool had = _wrote;
        try
        {
            var pad = _pad ?? Current();
            if (pad != null) ZeroOn(pad, _impulse);
        }
        catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "PadOutput.Stop"); }
        _wrote = false;
        _lf = _hf = _lt = _rt = 0f;
        if (had) DebugLog.Verbose(ControllerFeelPlugin.ModId, () => "motors stopped: " + why);
    }
}
