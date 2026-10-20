using System;

namespace OverTheHillPlus.ControllerFeel;

/// <summary>
/// Turns what the car does (<see cref="FeelInput"/>) into motor strengths (<see cref="FeelOutput"/>). Every effect has its
/// own state (envelopes, noise) and its own strength setting; the effects stack on the same two motors without ever passing
/// full strength. The parts are public after each step (<see cref="Parts"/>), for the MODS page meter and the tests.
/// No game types.
/// </summary>
internal sealed class FeelMixer
{
    /// <summary>The effects, in the order of <see cref="Parts"/>.</summary>
    public enum Effect { Surface, Bumps, Landing, Wheelspin, Lockup, Idle, Winch }

    public static readonly string[] EffectNames = { "Surface", "Bumps and bottoming", "Landings", "Wheelspin", "Lock-ups and skids", "Engine idle", "Winch strain" };

    /// <summary>How strongly each effect sounded in the last step, 0 to 1 (before its strength setting).</summary>
    public readonly float[] Parts = new float[7];

    private readonly SmoothNoise _surface = new(11), _grit = new(23), _spin = new(37), _creak = new(41), _idle = new(53);
    private readonly Pulse _bumpLF = new(), _bumpHF = new(), _stopLF = new(), _stopHF = new(), _landLF = new(), _landHF = new();

    private float _airTime, _fallSpeed;
    private bool _wasAirborne;
    private float _bottomWas;
    private float _lockPhase, _idlePhase, _idleLevel, _winchLevel;

    public void Reset()
    {
        _surface.Reset();
        _grit.Reset();
        _spin.Reset();
        _creak.Reset();
        _idle.Reset();
        _bumpLF.Reset();
        _bumpHF.Reset();
        _stopLF.Reset();
        _stopHF.Reset();
        _landLF.Reset();
        _landHF.Reset();
        _airTime = _fallSpeed = 0f;
        _wasAirborne = false;
        _bottomWas = 0f;
        _lockPhase = _idlePhase = _idleLevel = _winchLevel = 0f;
        Array.Clear(Parts, 0, Parts.Length);
    }

    public FeelOutput Step(in FeelInput i, FeelSettings s, float dt)
    {
        dt = Math.Max(0.0005f, Math.Min(0.1f, dt));
        float master = FeelSettings.Clamp(s.Master);
        float lf = 0f, hf = 0f, lt = 0f, rt = 0f;

        // ---- Surface texture: what the wheels roll on, by how fast.
        {
            float partLF = 0f, partHF = 0f;
            if (i.Grounded > 0 && i.SurfaceShare != null)
            {
                float low = 0f, high = 0f, hz = 0f, depth = 0f;
                int n = Math.Min(i.SurfaceShare.Length, SurfaceProfiles.Count);
                for (int k = 0; k < n; k++)
                {
                    float w = i.SurfaceShare[k];
                    if (w <= 0f) continue;
                    var p = SurfaceProfiles.Get((Surface)k);
                    low += p.Low * w;
                    high += p.High * w;
                    hz += p.Hz * w;
                    depth += p.Depth * w;
                }
                float gain = SurfaceProfiles.SpeedGain(i.SpeedMs) * (0.5f + 0.5f * i.Grounded / Math.Max(1, i.Wheels));
                // The level wanders around its profile; the mean stays the profile's.
                float wander = (1f - depth) + depth * 2f * _surface.Step(dt, hz);
                float grit = 0.8f + 0.4f * _grit.Step(dt, hz * 1.7f);
                partLF = low * gain * wander;
                partHF = high * gain * wander * grit;
            }
            else
            {
                _surface.Step(dt, 4f);
                _grit.Step(dt, 4f);
            }
            Parts[(int)Effect.Surface] = FeelMath.Clamp01(Math.Max(partLF, partHF));
            float k2 = FeelSettings.Clamp(s.Surface);
            lf = FeelMath.Combine(lf, partLF * k2);
            hf = FeelMath.Combine(hf, partHF * k2);
        }

        // ---- Bumps and bottoming out: the suspension's speed and the bump stop.
        {
            float hit = FeelMath.Smooth(i.SuspensionPeak, 0.35f, 2.4f);
            if (hit > 0f)
            {
                _bumpLF.Fire(0.6f * hit);
                _bumpHF.Fire(0.75f * hit);
            }
            if (i.BottomOut > 0.15f && _bottomWas < 0.05f)
            {
                float st = FeelMath.Clamp01(0.55f + 0.45f * i.BottomOut);
                _stopLF.Fire(0.95f * st);
                _stopHF.Fire(0.6f * st);
            }
            _bottomWas = i.BottomOut;
            float bl = Math.Max(_bumpLF.Step(dt, 0.14f), _stopLF.Step(dt, 0.28f));
            float bh = Math.Max(_bumpHF.Step(dt, 0.10f), _stopHF.Step(dt, 0.16f));
            Parts[(int)Effect.Bumps] = Math.Max(bl, bh);
            float k = FeelSettings.Clamp(s.Bumps);
            lf = FeelMath.Combine(lf, bl * k);
            hf = FeelMath.Combine(hf, bh * k);
        }

        // ---- Landings: the thump when the wheels come back down after a jump or a drop.
        {
            bool air = i.Grounded == 0 && i.Wheels > 0;
            if (air)
            {
                _airTime += dt;
                _fallSpeed = Math.Max(_fallSpeed, -i.VerticalSpeed);
                _wasAirborne = true;
            }
            else if (_wasAirborne)
            {
                if (_airTime >= 0.22f)
                {
                    float st = FeelMath.Clamp01(0.7f * FeelMath.Smooth(_fallSpeed, 2f, 13f) + 0.3f * FeelMath.Smooth(_airTime, 0.25f, 1.6f));
                    if (st > 0.05f)
                    {
                        _landLF.Fire(st);
                        _landHF.Fire(0.65f * st);
                    }
                }
                _wasAirborne = false;
                _airTime = 0f;
                _fallSpeed = 0f;
            }
            float ll = _landLF.Step(dt, 0.50f), lh = _landHF.Step(dt, 0.20f);
            Parts[(int)Effect.Landing] = Math.Max(ll, lh);
            float k = FeelSettings.Clamp(s.Landing);
            lf = FeelMath.Combine(lf, ll * k);
            hf = FeelMath.Combine(hf, lh * k);
        }

        // ---- Wheelspin: a driven wheel spinning faster than the ground under power.
        {
            float spin = FeelMath.Smooth(i.DriveSlip, 0.30f, 1.0f) * FeelMath.Smooth(i.Throttle, 0.2f, 0.8f) * (i.EngineOn ? 1f : 0f);
            float buzz = 0.75f + 0.5f * _spin.Step(dt, 22f);
            Parts[(int)Effect.Wheelspin] = spin;
            float k = FeelSettings.Clamp(s.Wheelspin);
            float share = s.TriggerMotors ? 0.55f : 1f;
            lf = FeelMath.Combine(lf, 0.28f * spin * k * share);
            hf = FeelMath.Combine(hf, 0.55f * spin * buzz * k * share);
            if (s.TriggerMotors) rt = Math.Max(rt, 0.6f * spin * buzz * k);
        }

        // ---- Lock-ups and skids: braked wheels sliding, and the car sliding sideways.
        {
            float brakeOn = FeelMath.Smooth(i.Brake, 0.15f, 0.5f);
            float moving = FeelMath.Smooth(i.SpeedMs, 1.5f, 5f);
            float locked = FeelMath.Smooth(i.BrakeSlip, 0.45f, 0.95f) * brakeOn * moving;
            float skid = FeelMath.Smooth(i.LateralSlip, 0.9f, 1.8f) * FeelMath.Smooth(i.SpeedMs, 3f, 8f);
            _lockPhase += dt * 11f;
            _lockPhase -= (float)Math.Floor(_lockPhase);
            // A judder rather than a steady buzz, as an anti-lock pedal feels.
            float judder = 0.55f + 0.45f * FeelMath.Smooth((float)Math.Sin(_lockPhase * 2f * Math.PI), -0.4f, 0.4f);
            float part = Math.Max(locked, 0.8f * skid);
            Parts[(int)Effect.Lockup] = part;
            float k = FeelSettings.Clamp(s.Lockup);
            float share = s.TriggerMotors ? 0.55f : 1f;
            lf = FeelMath.Combine(lf, (0.42f * locked * judder + 0.12f * skid) * k * share);
            hf = FeelMath.Combine(hf, (0.35f * locked + 0.45f * skid) * k * share);
            if (s.TriggerMotors) lt = Math.Max(lt, 0.8f * locked * judder * k);
        }

        // ---- Engine idle: the shake of a running engine at rest.
        {
            bool idling = i.EngineOn && i.Throttle < 0.12f && i.SpeedMs < 2.5f && i.Rpm > 200f;
            _idleLevel = FeelMath.Approach(_idleLevel, idling ? 1f : 0f, 4f, 2.5f, dt);
            float hz = Math.Max(3f, Math.Min(12f, i.Rpm / 120f));
            _idlePhase += dt * hz;
            _idlePhase -= (float)Math.Floor(_idlePhase);
            float lope = 0.6f + 0.4f * (float)Math.Sin(_idlePhase * 2f * Math.PI);
            float wobble = 0.85f + 0.3f * _idle.Step(dt, 3f);
            float v = _idleLevel * lope * wobble;
            Parts[(int)Effect.Idle] = v;
            float k = FeelSettings.Clamp(s.Idle);
            lf = FeelMath.Combine(lf, 0.12f * v * k);
            hf = FeelMath.Combine(hf, 0.03f * v * k);
        }

        // ---- Winch strain: a taut rope pulling on the car.
        {
            float target = i.WinchTaut ? FeelMath.Smooth(i.WinchTension, 0.05f, 0.9f) : 0f;
            _winchLevel = FeelMath.Approach(_winchLevel, target, 3f, 5f, dt);
            // Past half tension the rope also creaks: bursts on the light motor.
            float creak = _creak.Step(dt, 8f);
            float creakAmount = _winchLevel > 0.5f ? Math.Max(0f, creak - 0.3f) * (_winchLevel - 0.5f) * 2f : 0f;
            Parts[(int)Effect.Winch] = _winchLevel;
            float k = FeelSettings.Clamp(s.Winch);
            lf = FeelMath.Combine(lf, (_winchLevel > 0.01f ? 0.06f + 0.42f * (float)Math.Pow(_winchLevel, 1.3) : 0f) * k);
            hf = FeelMath.Combine(hf, 0.30f * creakAmount * k);
        }

        return new FeelOutput(lf * master, hf * master, lt * master, rt * master);
    }
}
