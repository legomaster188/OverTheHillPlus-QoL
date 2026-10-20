using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Trailers;

/// <summary>The hitch joint's numbers for a trailer, and when a hitch gives up. Pure code (unit-tested).</summary>
public readonly struct HitchLimits
{
    /// <summary>Jackknife stop, pitch and roll either way, degrees (each within what a ConfigurableJoint takes).</summary>
    public readonly float Yaw, Pitch, Roll;
    /// <summary>Force that breaks the hitch on the host, newtons (the owner's copy never breaks: the host decides).</summary>
    public readonly float BreakForce;

    public HitchLimits(float yaw, float pitch, float roll, float breakForce)
    {
        Yaw = yaw; Pitch = pitch; Roll = roll; BreakForce = breakForce;
    }

    /// <summary>Car tilt (angle between its up and the world's) past which the trailer is unhitched.</summary>
    public const float CarRolloverDeg = 60f;
    /// <summary>Trailer tilt past which it is unhitched (it is lying on its side).</summary>
    public const float TrailerRolloverDeg = 70f;
    /// <summary>How long a tilt must last before the unhitch (bumps and jumps don't count).</summary>
    public const float RolloverSeconds = 0.6f;
    /// <summary>How far before a limit the joint starts to push back, degrees (a hard stop with none chatters).</summary>
    public const float ContactDeg = 5f;

    /// <summary>The hitch's rotational friction (N m s/rad): a ball coupler is stiff against the trailer swaying, so it
    /// settles instead of snaking. Follows the trailer's mass.</summary>
    public static float AngularDamper(float massKg)
    {
        float m = float.IsFinite(massKg) ? Math.Max(massKg, 50f) : 500f;
        return 0.6f * m;
    }

    /// <summary>The limits for a trailer: clamped to what a joint takes (yaw 5-170, pitch 5-80, roll 2-60 degrees),
    /// break force 2.5e4 N plus 180 N per kilogram of trailer (about 1.5e5 N for a 700 kg trailer).</summary>
    public static HitchLimits For(TrailerSpec s, float massKg)
    {
        float yaw = Clamp(s?.YawLimit ?? 80f, 5f, 170f);
        float pitch = Clamp(s?.PitchLimit ?? 30f, 5f, 80f);
        float roll = Clamp(s?.RollLimit ?? 20f, 2f, 60f);
        float m = float.IsFinite(massKg) ? Math.Max(massKg, 50f) : 500f;
        return new HitchLimits(yaw, pitch, roll, 2.5e4f + 180f * m);
    }

    /// <summary>True when the trailer should be unhitched: the hitch broke, or the car or the trailer has been tilted
    /// past its limit for <see cref="RolloverSeconds"/>.</summary>
    public static bool ShouldUnhitch(bool jointBroken, float carTiltDeg, float trailerTiltDeg, float tiltedForSeconds)
    {
        if (jointBroken) return true;
        bool over = carTiltDeg > CarRolloverDeg || trailerTiltDeg > TrailerRolloverDeg;
        return over && tiltedForSeconds >= RolloverSeconds;
    }

    private static float Clamp(float v, float lo, float hi) => float.IsFinite(v) ? Math.Min(Math.Max(v, lo), hi) : lo;
}

/// <summary>
/// Overrun (surge) brake: when the car slows down, the trailer runs onto the hitch and that push works its brakes, in
/// proportion. Here the push is read from the towing car's deceleration (open loop, so the trailer's own braking
/// never feeds back into it), with a dead band for coasting and bumps, and no braking while reversing (the real
/// thing has a reverse latch). Unhitched, the parking brake holds the wheels. Pure code (unit-tested).
/// </summary>
public static class OverrunBrake
{
    /// <summary>Deceleration below which nothing brakes (coasting, engine braking, bumps), m/s^2.</summary>
    public const float DeadBand = 0.8f;
    /// <summary>Deceleration at which the brake is fully on, m/s^2.</summary>
    public const float Full = 5.5f;
    /// <summary>Reversing faster than this (m/s, trailer moving backwards) disengages the brake.</summary>
    public const float ReverseLatch = 0.3f;
    /// <summary>Share of the tyre grip the overrun brake may use while hitched: its full push (about 0.45 g) stays under
    /// the car's own deceleration at full brake, so the trailer never drags the car back (which the car's deceleration
    /// would feed into the brake again). Parked, the whole grip holds the wheels.</summary>
    public const float HitchedGripShare = 0.45f;

    /// <summary>How hard the trailer pushes on the ball, as a deceleration (m/s^2): the car's deceleration less what the
    /// slope does to a rolling trailer by itself. Uphill (<paramref name="slopeSin"/> &gt; 0: the trailer's forward axis
    /// points up) gravity slows the trailer as much as the car, so a car losing speed on a climb is not pushed and the
    /// trailer must not brake (it dragged on every hill); downhill the trailer runs onto the car even at a steady speed.</summary>
    public static float Push(float carDecel, float slopeSin)
    {
        if (!float.IsFinite(carDecel)) return 0f;
        if (!float.IsFinite(slopeSin)) return carDecel;
        return carDecel - WheelMath.G * Math.Clamp(slopeSin, -1f, 1f);
    }

    /// <summary>The grip the brake term works with: part of it while hitched, all of it parked.</summary>
    public static float BrakeGrip(bool hitched, float grip) => hitched ? grip * HitchedGripShare : grip;

    /// <summary>Brake fraction 0..1 while hitched. <paramref name="carDecel"/>: how fast the car slows down along the
    /// trailer's forward axis (m/s^2, positive = slowing). <paramref name="trailerSpeed"/>: the trailer's speed along its
    /// forward axis (m/s, negative = reversing).</summary>
    public static float Hitched(float carDecel, float trailerSpeed)
    {
        if (!float.IsFinite(carDecel) || !float.IsFinite(trailerSpeed)) return 0f;
        if (trailerSpeed < -ReverseLatch) return 0f;
        if (carDecel <= DeadBand) return 0f;
        float t = (carDecel - DeadBand) / (Full - DeadBand);
        return t >= 1f ? 1f : t;
    }

    /// <summary>The brake fraction: the overrun brake while hitched, the parking brake (fully on) when not.</summary>
    public static float Fraction(bool hitched, float carDecel, float trailerSpeed) =>
        hitched ? Hitched(carDecel, trailerSpeed) : 1f;
}

/// <summary>What the host answers to a spawn request.</summary>
public enum SpawnVerdict : byte { Ok = 0, PlayerLimit = 1, SessionLimit = 2 }

/// <summary>How many trailers a player and a session may have. Pure code (unit-tested).</summary>
public static class SpawnLimits
{
    public const int MaxPerPlayer = 2, MaxSession = 8;

    public static int ClampPerPlayer(int v) => Math.Min(Math.Max(v, 1), MaxPerPlayer);
    public static int ClampSession(int v) => Math.Min(Math.Max(v, 1), MaxSession);

    /// <summary>May a player who has <paramref name="ownerHas"/> trailers spawn one more in a session with
    /// <paramref name="sessionHas"/>?</summary>
    public static SpawnVerdict Check(int ownerHas, int sessionHas, int perPlayer, int session)
    {
        if (ownerHas >= ClampPerPlayer(perPlayer)) return SpawnVerdict.PlayerLimit;
        if (sessionHas >= ClampSession(session)) return SpawnVerdict.SessionLimit;
        return SpawnVerdict.Ok;
    }
}

/// <summary>One hitched trailer per car: a car's hitch ball takes one coupler. Pure code (unit-tested).</summary>
public static class BallRule
{
    /// <summary>The trailer (network id) hitched to car <paramref name="carNetId"/>, 0 when its ball is free.
    /// <paramref name="except"/>: the trailer asking (re-hitching or resetting it doesn't count against itself).</summary>
    public static uint HitchedTo(IEnumerable<TrailerState> trailers, uint carNetId, uint except = 0)
    {
        if (trailers == null || carNetId == 0) return 0;
        foreach (var t in trailers)
            if (t != null && t.Hitched && t.CarNetId == carNetId && t.NetId != except) return t.NetId;
        return 0;
    }
}

/// <summary>Why the host takes a trailer away.</summary>
public enum DespawnReason : byte
{
    Keep = 0,
    /// <summary>Its owner left the session.</summary>
    OwnerLeft = 1,
    /// <summary>Its owner changed car (or has had none for a while: respawned elsewhere).</summary>
    CarChanged = 2,
    /// <summary>The scene changes (portal, region): everything the session spawned goes.</summary>
    SceneChanged = 3,
    /// <summary>The mod can't serve this session any more.</summary>
    ModOff = 4,
    /// <summary>It fell out of the world.</summary>
    FellOut = 5,
    /// <summary>Its network object is gone already.</summary>
    Lost = 6,
    /// <summary>Its owner or the host removed it.</summary>
    Removed = 7,
}

/// <summary>What the host knows about a trailer when it checks it (twice a second).</summary>
public struct DespawnInput
{
    public bool ObjectAlive;
    public bool ModActive;
    public bool SceneChanging;
    public bool OwnerPresent;
    /// <summary>Network id of the owner's car now (0: none found).</summary>
    public uint OwnerCarNow;
    /// <summary>Network id of the car the trailer was spawned for.</summary>
    public uint OwnerCarAtSpawn;
    /// <summary>How long the owner has had no car, seconds.</summary>
    public float NoCarFor;
    public float Height;
}

/// <summary>The despawn rules, in order. Pure code (unit-tested).</summary>
public static class DespawnRules
{
    /// <summary>An owner without a car for this long has changed car (or lost it): seconds.</summary>
    public const float NoCarGrace = 5f;
    /// <summary>Below this height the trailer has fallen out of the world, m.</summary>
    public const float FloorY = -500f;

    public static DespawnReason Decide(in DespawnInput i)
    {
        if (!i.ObjectAlive) return DespawnReason.Lost;
        if (!i.ModActive) return DespawnReason.ModOff;
        if (i.SceneChanging) return DespawnReason.SceneChanged;
        if (!i.OwnerPresent) return DespawnReason.OwnerLeft;
        if (i.OwnerCarNow != 0 && i.OwnerCarNow != i.OwnerCarAtSpawn) return DespawnReason.CarChanged;
        if (i.OwnerCarNow == 0 && i.NoCarFor >= NoCarGrace) return DespawnReason.CarChanged;
        if (!float.IsFinite(i.Height) || i.Height < FloorY) return DespawnReason.FellOut;
        return DespawnReason.Keep;
    }
}

/// <summary>The raycast wheel's forces, as scalars along the wheel's axes. Pure code (unit-tested).</summary>
public static class WheelMath
{
    public const float G = 9.81f;

    /// <summary>Spring rate (N/m) that sags <paramref name="sag"/> metres under the wheel's share of the weight.</summary>
    public static float SpringRate(float massKg, int wheels, float sag) =>
        massKg * G / Math.Max(wheels, 1) / Math.Max(sag, 0.005f);

    /// <summary>Damper rate (N s/m) for a damping ratio over the wheel's share of the mass.</summary>
    public static float DamperRate(float springRate, float massKg, int wheels, float ratio) =>
        2f * ratio * MathF.Sqrt(Math.Max(springRate, 0f) * massKg / Math.Max(wheels, 1));

    /// <summary>Suspension push (N, never pulling): spring on the compression plus damper on the compression speed
    /// (<paramref name="upSpeed"/>: the contact point's speed along the trailer's up, positive = extending). Capped
    /// at 6 g of the wheel's load so a deep hit can't launch the trailer.</summary>
    public static float Suspension(float compression, float upSpeed, float k, float c, float wheelLoad)
    {
        if (!(compression > 0f) || !float.IsFinite(upSpeed)) return 0f;
        float f = k * compression - c * upSpeed;
        if (f < 0f) return 0f;
        float cap = 6f * Math.Max(wheelLoad, 1f);
        return f > cap ? cap : f;
    }

    /// <summary>Friction force (N) along one axis that stops the wheel's sliding speed <paramref name="slip"/> in about
    /// two steps, at most <paramref name="limit"/> newtons either way.</summary>
    public static float StopSlip(float slip, float massShare, float dt, float limit)
    {
        if (!float.IsFinite(slip) || !(dt > 0f) || !(limit > 0f)) return 0f;
        float f = -slip * massShare / dt * 0.5f;
        return f > limit ? limit : f < -limit ? -limit : f;
    }

    /// <summary>Scales (lateral, longitudinal) down onto the friction circle of radius <paramref name="limit"/>.</summary>
    public static (float lat, float lon) FrictionCircle(float lat, float lon, float limit)
    {
        float m = MathF.Sqrt(lat * lat + lon * lon);
        if (!(limit > 0f)) return (0f, 0f);
        if (m <= limit) return (lat, lon);
        float s = limit / m;
        return (lat * s, lon * s);
    }

    /// <summary>The longitudinal force: rolling resistance plus brakes, opposing the rolling speed.</summary>
    public static float Longitudinal(float speed, float normal, float rolling, float brake, float grip, float massShare, float dt)
    {
        if (!(normal > 0f)) return 0f;
        float limit = normal * (rolling + Math.Clamp(brake, 0f, 1f) * grip);
        return StopSlip(speed, massShare, dt, limit);
    }
}
