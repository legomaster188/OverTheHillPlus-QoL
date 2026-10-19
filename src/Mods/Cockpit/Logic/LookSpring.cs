using System;

namespace OverTheHillPlus.Cockpit;

/// <summary>
/// The free look: the head turns with the mouse or right stick, within limits, and springs back to looking ahead a
/// moment after the input stops (no game types, unit tested). Degrees; yaw to the right and pitch up are positive.
/// </summary>
internal sealed class LookSpring
{
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }

    public float MaxYaw = 110f, MaxPitchUp = 55f, MaxPitchDown = 40f;
    /// <summary>Seconds without input before the head starts coming back.</summary>
    public float ReturnDelay = 0.5f;
    /// <summary>How fast it comes back (higher = quicker).</summary>
    public float ReturnSharpness = 7f;
    public bool SpringBack = true;

    private float _idle;

    public void Reset()
    {
        Yaw = Pitch = 0f;
        _idle = 0f;
    }

    /// <summary>Turns the head by the input of this frame (degrees), then lets it spring back when idle.</summary>
    public void Step(float dYaw, float dPitch, float dt)
    {
        if (float.IsNaN(dYaw) || float.IsNaN(dPitch)) { dYaw = dPitch = 0f; }
        if (Math.Abs(dYaw) + Math.Abs(dPitch) > 0.001f)
        {
            Yaw += dYaw;
            Pitch += dPitch;
            _idle = 0f;
        }
        else if (dt > 0f) _idle += dt;
        Yaw = Math.Max(-MaxYaw, Math.Min(MaxYaw, Yaw));
        Pitch = Math.Max(-MaxPitchDown, Math.Min(MaxPitchUp, Pitch));

        if (!SpringBack || _idle < ReturnDelay || !(dt > 0f)) return;
        float k = 1f - (float)Math.Exp(-Math.Max(0f, ReturnSharpness) * dt);
        Yaw -= Yaw * k;
        Pitch -= Pitch * k;
        if (Math.Abs(Yaw) < 0.05f) Yaw = 0f;
        if (Math.Abs(Pitch) < 0.05f) Pitch = 0f;
    }
}
