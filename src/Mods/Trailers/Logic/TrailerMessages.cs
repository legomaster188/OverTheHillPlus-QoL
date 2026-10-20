using System;
using OverTheHillPlus.Features.Public.ModNet;

namespace OverTheHillPlus.Trailers;

/// <summary>What a player asks the host to do with their trailer.</summary>
public enum TrailerOp : byte
{
    Spawn = 1,
    Remove = 2,
    Hitch = 3,
    Unhitch = 4,
    Reset = 5,
    Load = 6,
    Unload = 7,
}

/// <summary>The host's answer.</summary>
public enum TrailerReply : byte
{
    Done = 0,
    Off = 1,
    NoCar = 2,
    TooFast = 3,
    PlayerLimit = 4,
    SessionLimit = 5,
    NoTrailer = 6,
    TooFar = 7,
    NotAHauler = 8,
    NotACar = 9,
    Failed = 10,
    NotReady = 11,
    Busy = 12,
    /// <summary>The car already tows a hitched trailer.</summary>
    BallTaken = 13,
    /// <summary>The car has no tow hitch (the tow bar upgrade) mounted.</summary>
    NoTowHitch = 14,
}

/// <summary>A position in a body's local space (Unity axes), for the wire.</summary>
public readonly struct V3 : IEquatable<V3>
{
    public readonly float X, Y, Z;
    public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public bool Equals(V3 o) => X == o.X && Y == o.Y && Z == o.Z;
    public override bool Equals(object obj) => obj is V3 o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public bool Sane(float max) => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) &&
                                   Math.Abs(X) <= max && Math.Abs(Y) <= max && Math.Abs(Z) <= max;
}

/// <summary>A guest's request (message type <see cref="TrailerMessages.Request"/>).</summary>
public struct TrailerRequest
{
    public TrailerOp Op;
    public TrailerKind Kind;
    /// <summary>Spawn: unused. Load: the car's item id.</summary>
    public uint Arg;
}

/// <summary>
/// One trailer as every modded peer must know it (message type <see cref="TrailerMessages.State"/>, host to all,
/// reliable, replayed to late joiners). A guest finds the object by <see cref="NetId"/>; the owner adds the same hitch
/// joint the host has, from <see cref="CarNetId"/> and the two anchors.
/// </summary>
public sealed class TrailerState
{
    public uint NetId;
    public TrailerKind Kind;
    /// <summary>PlayerRef.RawEncoded of the owner (the trailer's input authority).</summary>
    public int Owner;
    public bool Hitched;
    /// <summary>Network id of the towing car (0 when not hitched).</summary>
    public uint CarNetId;
    /// <summary>Hitch point in the trailer's space.</summary>
    public V3 Anchor;
    /// <summary>Hitch ball in the car's space.</summary>
    public V3 CarAnchor;
    /// <summary>Car hauler: item id of the car shown on the deck (0: empty).</summary>
    public uint CargoCarId;

    public TrailerState Clone() => (TrailerState)MemberwiseClone();

    public bool SameAs(TrailerState o) =>
        o != null && NetId == o.NetId && Kind == o.Kind && Owner == o.Owner && Hitched == o.Hitched &&
        CarNetId == o.CarNetId && Anchor.Equals(o.Anchor) && CarAnchor.Equals(o.CarAnchor) && CargoCarId == o.CargoCarId;
}

/// <summary>The trailers channel's payloads. Fields are appended, never reordered (ModNet's versioning rule).
/// Decoders never throw and refuse anything malformed (null / false). Pure code (unit-tested).</summary>
public static class TrailerMessages
{
    public const byte Request = 1, Reply = 2, State = 3, Removed = 4;
    /// <summary>Anchors further than this from a body's origin are refused, m.</summary>
    public const float MaxAnchor = 12f;

    public static byte[] EncodeRequest(TrailerRequest r) =>
        new NetWriter(8).Byte((byte)r.Op).Byte((byte)r.Kind).Int(unchecked((int)r.Arg)).ToArray();

    public static bool DecodeRequest(byte[] payload, out TrailerRequest r)
    {
        var rd = new NetReader(payload);
        byte op = rd.Byte(), kind = rd.Byte();
        uint arg = unchecked((uint)rd.Int());
        r = default;
        if (!rd.Ok || op < (byte)TrailerOp.Spawn || op > (byte)TrailerOp.Unload || !TrailerSpecs.IsKind(kind)) return false;
        r = new TrailerRequest { Op = (TrailerOp)op, Kind = (TrailerKind)kind, Arg = arg };
        return true;
    }

    public static byte[] EncodeReply(TrailerOp op, TrailerReply code) => new NetWriter(4).Byte((byte)op).Byte((byte)code).ToArray();

    public static bool DecodeReply(byte[] payload, out TrailerOp op, out TrailerReply code)
    {
        var rd = new NetReader(payload);
        byte o = rd.Byte(), c = rd.Byte();
        op = (TrailerOp)o;
        code = (TrailerReply)c;
        return rd.Ok && o >= (byte)TrailerOp.Spawn && o <= (byte)TrailerOp.Unload && c <= (byte)TrailerReply.NoTowHitch;
    }

    public static byte[] EncodeState(TrailerState s)
    {
        var w = new NetWriter(48)
            .Int(unchecked((int)s.NetId)).Byte((byte)s.Kind).Int(s.Owner)
            .Bool(s.Hitched).Int(unchecked((int)s.CarNetId));
        Vec(w, s.Anchor);
        Vec(w, s.CarAnchor);
        return w.Int(unchecked((int)s.CargoCarId)).ToArray();
    }

    public static TrailerState DecodeState(byte[] payload)
    {
        var rd = new NetReader(payload);
        var s = new TrailerState
        {
            NetId = unchecked((uint)rd.Int()),
        };
        byte kind = rd.Byte();
        s.Owner = rd.Int();
        s.Hitched = rd.Bool();
        s.CarNetId = unchecked((uint)rd.Int());
        s.Anchor = Vec(rd);
        s.CarAnchor = Vec(rd);
        s.CargoCarId = unchecked((uint)rd.Int());
        if (!rd.Ok || s.NetId == 0 || !TrailerSpecs.IsKind(kind)) return null;
        s.Kind = (TrailerKind)kind;
        if (s.Hitched && (s.CarNetId == 0 || !s.Anchor.Sane(MaxAnchor) || !s.CarAnchor.Sane(MaxAnchor))) return null;
        if (s.CargoCarId != 0 && s.Kind != TrailerKind.Hauler) return null;
        return s;
    }

    public static byte[] EncodeRemoved(uint netId, DespawnReason why) =>
        new NetWriter(5).Int(unchecked((int)netId)).Byte((byte)why).ToArray();

    public static bool DecodeRemoved(byte[] payload, out uint netId, out DespawnReason why)
    {
        var rd = new NetReader(payload);
        netId = unchecked((uint)rd.Int());
        byte b = rd.Byte();
        why = (DespawnReason)b;
        return rd.Ok && netId != 0 && b <= (byte)DespawnReason.Removed;
    }

    private static void Vec(NetWriter w, V3 v) => w.Float(v.X).Float(v.Y).Float(v.Z);

    private static V3 Vec(NetReader r)
    {
        float x = r.Float(), y = r.Float(), z = r.Float();
        return new V3(x, y, z);
    }
}
