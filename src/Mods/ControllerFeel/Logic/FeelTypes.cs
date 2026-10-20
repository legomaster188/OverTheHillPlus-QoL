using System;

namespace OverTheHillPlus.ControllerFeel;

/// <summary>Strength of each effect (1 = as designed, 0 = off, up to 2) and the master strength.</summary>
internal sealed class FeelSettings
{
    public float Master = 1f, Surface = 1f, Bumps = 1f, Landing = 1f, Wheelspin = 1f, Lockup = 1f, Idle = 1f, Winch = 1f;

    /// <summary>The pad has trigger motors (Xbox impulse triggers): lock-ups and wheelspin go to the triggers and the main motors take a smaller share.</summary>
    public bool TriggerMotors;

    public const float MaxStrength = 2f;

    public static float Clamp(float v) => float.IsNaN(v) ? 0f : Math.Max(0f, Math.Min(MaxStrength, v));
}

/// <summary>
/// What the car did this frame, summed up by the sampler (game side) so the mixer needs no game types. Speeds are in m/s,
/// slips are the game's wheel slip numbers (ratios, 0 = rolling freely), pedals are 0 to 1.
/// </summary>
internal struct FeelInput
{
    public float Dt;
    public bool EngineOn;
    public float Rpm, Throttle, Brake;
    public float SpeedMs;
    /// <summary>Vertical speed of the car body (negative = falling).</summary>
    public float VerticalSpeed;
    public int Wheels, Grounded;
    /// <summary>Share of the grounded wheels on each <see cref="Surface"/> (sums to 1 when any wheel is down); null = none known.</summary>
    public float[] SurfaceShare;
    /// <summary>Biggest suspension speed any wheel saw since the last frame (m/s, either direction).</summary>
    public float SuspensionPeak;
    /// <summary>0 to 1: how far into its bump stop the deepest wheel went since the last frame.</summary>
    public float BottomOut;
    /// <summary>Biggest slip of a driven, grounded wheel under power.</summary>
    public float DriveSlip;
    /// <summary>Biggest slip of a grounded wheel that is being braked.</summary>
    public float BrakeSlip;
    /// <summary>Biggest sideways slip of a grounded wheel (the game's normalised slip: 1 is the grip limit).</summary>
    public float LateralSlip;
    public bool WinchTaut;
    /// <summary>0 to 1.</summary>
    public float WinchTension;
}

/// <summary>Motor strengths for one frame, 0 to 1. LF is the big (heavy) motor, HF the small (light) one.</summary>
internal readonly struct FeelOutput
{
    public readonly float LF, HF, LeftTrigger, RightTrigger;

    public FeelOutput(float lf, float hf, float lt = 0f, float rt = 0f)
    {
        LF = FeelMath.Clamp01(lf);
        HF = FeelMath.Clamp01(hf);
        LeftTrigger = FeelMath.Clamp01(lt);
        RightTrigger = FeelMath.Clamp01(rt);
    }

    public static readonly FeelOutput Zero = new(0f, 0f);

    public bool IsZero => LF <= 0.0005f && HF <= 0.0005f && LeftTrigger <= 0.0005f && RightTrigger <= 0.0005f;

    /// <summary>Two outputs on the same motors (the game's own rumble and ours): they stack without passing full strength; triggers take the stronger.</summary>
    public static FeelOutput Over(FeelOutput a, FeelOutput b) => new(
        FeelMath.Combine(a.LF, b.LF), FeelMath.Combine(a.HF, b.HF), Math.Max(a.LeftTrigger, b.LeftTrigger), Math.Max(a.RightTrigger, b.RightTrigger));

    public FeelOutput Scaled(float k) => new(LF * k, HF * k, LeftTrigger * k, RightTrigger * k);
}

/// <summary>
/// When the pad may rumble at all. The motors must be quiet in menus, on the pause screen, while the window is not in focus,
/// with no car, and when the player is using keyboard and mouse.
/// </summary>
internal static class FeelGate
{
    public readonly struct State
    {
        public readonly bool ModOn, HasPad, UsingPad, Driving, OnTop, Focused, HasCar, Running;

        public State(bool modOn, bool hasPad, bool usingPad, bool driving, bool onTop, bool focused, bool hasCar, bool running)
        {
            ModOn = modOn;
            HasPad = hasPad;
            UsingPad = usingPad;
            Driving = driving;
            OnTop = onTop;
            Focused = focused;
            HasCar = hasCar;
            Running = running;
        }
    }

    /// <summary>Why the motors stay off, or null when they may run.</summary>
    public static string Closed(in State s)
    {
        if (!s.ModOn) return "switched off";
        if (!s.HasPad) return "no gamepad";
        if (!s.UsingPad) return "keyboard and mouse in use";
        if (!s.Focused) return "game window not in focus";
        if (!s.Running) return "game paused";
        if (!s.HasCar) return "no car";
        if (!s.Driving || !s.OnTop) return "in a menu";
        return null;
    }

    public static bool Open(in State s) => Closed(s) == null;
}
