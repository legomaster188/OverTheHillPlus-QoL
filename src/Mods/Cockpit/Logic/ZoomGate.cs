using System;

namespace OverTheHillPlus.Cockpit;

/// <summary>
/// Decides when "zooming in past the closest chase view" has happened: the mouse wheel keeps turning toward zoom
/// while the game's camera no longer gets any closer, for a couple of notches (no game types, unit tested). Also
/// the opposite, for leaving: zooming out while in the cockpit.
/// </summary>
internal sealed class ZoomGate
{
    /// <summary>Wheel units to collect (a notch is about 120).</summary>
    public float Need = 240f;
    /// <summary>Only counts while the camera is this close to the car (metres), so a far view never jumps in.</summary>
    public float MaxDistance = 8f;

    private float _push, _idle, _last = float.NaN;

    public void Reset()
    {
        _push = _idle = 0f;
        _last = float.NaN;
    }

    /// <summary>One frame: the wheel (positive = zoom in), the camera's distance from the car, the frame time. True when it triggers.</summary>
    public bool Step(float scroll, float distance, float dt)
    {
        bool shrinking = !float.IsNaN(_last) && distance < _last - 0.0015f;
        _last = distance;
        if (float.IsNaN(distance) || distance > MaxDistance || scroll < -0.01f || shrinking)
        {
            _push = _idle = 0f;
            return false;
        }
        if (scroll > 0.01f)
        {
            _push += scroll;
            _idle = 0f;
        }
        else
        {
            _idle += Math.Max(0f, dt);
            if (_idle > 1f) _push = 0f;
        }
        if (_push < Need) return false;
        _push = _idle = 0f;
        return true;
    }

    /// <summary>Leaving: collects zooming out (negative wheel) in <paramref name="pushed"/>; true once it reaches <paramref name="need"/>.</summary>
    public static bool LeaveStep(ref float pushed, float scroll, float need)
    {
        if (scroll < -0.01f) pushed += -scroll;
        else if (scroll > 0.01f) pushed = 0f;
        if (pushed < need) return false;
        pushed = 0f;
        return true;
    }
}
