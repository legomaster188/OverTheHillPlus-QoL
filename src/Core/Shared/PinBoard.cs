using System;
using System.Collections.Generic;
using System.Text;
using OverTheHillPlus.Features.Public.ModNet;

namespace OverTheHillPlus.Shared;

/// <summary>One pin on a <see cref="PinBoard"/>.</summary>
internal sealed class BoardPin
{
    public ushort Id;
    public string Name = "";
    public float X, Y, Z;
    /// <summary>Index into the mods' pin colours (0 .. <see cref="PinBoard.ColorCount"/>-1).</summary>
    public byte Color;
    /// <summary>Who added it (display name, already cleaned).</summary>
    public string Owner = "";
    /// <summary>The adding player's PlayerRef.RawEncoded (0 = the host's own), so a guest may remove only its own.</summary>
    public int OwnerKey;
}

/// <summary>
/// The pins shared in a session, kept by the host and mirrored by every modded guest (pure logic, no game types).
///
/// <para><b>Sync model.</b> The host owns the board: guests ask it to add or remove a pin, the host checks and applies
/// the change and sends everyone a full snapshot (<see cref="Encode"/>). A snapshot carries an epoch (random per hosted
/// session) and a revision (bumped on every change); a guest applies it only when the epoch is new or the revision is
/// higher (<see cref="ApplySnapshot"/>), so a late or reordered message never rolls the board back. The host's "push
/// to everyone" is part of the snapshot too (<see cref="PushedId"/> plus a counter), so a late joiner sees the pushed
/// pin without being told about an old push again.</para>
///
/// <para>Everything read from the wire is cleaned (finite, bounded coordinates; names without markup or control
/// characters, cut to length). Names shown to other players still go through the name filter on the host.</para>
/// </summary>
internal sealed class PinBoard
{
    public const byte WireVersion = 1;
    public const int MaxPins = 32;
    public const int MaxName = 24;
    public const int MaxOwner = 24;
    public const int ColorCount = 8;
    /// <summary>
    /// The largest snapshot <see cref="Encode"/> writes: a full board whose names and owners are all 3-byte characters
    /// (UTF-8 never takes more than 3 bytes per UTF-16 char): 16 + 32 × (2 + 2+72 + 12 + 1 + 2+72 + 4) = 5360.
    /// </summary>
    public const int MaxSnapshotBytes = 5376;
    /// <summary>Coordinates beyond this (metres from the map origin) are refused.</summary>
    public const float MaxCoordinate = 200000f;

    private readonly List<BoardPin> _pins = new();
    private ushort _nextId = 1;

    /// <summary>A guest's empty mirror (epoch 0: any host snapshot is newer).</summary>
    public PinBoard() { }

    /// <summary>The host's board for one session.</summary>
    public PinBoard(int epoch) { Epoch = epoch; }

    public int Epoch { get; private set; }
    public int Revision { get; private set; }
    public IReadOnlyList<BoardPin> Pins => _pins;
    /// <summary>The pin the host pushed to everyone (0 = none).</summary>
    public ushort PushedId { get; private set; }
    /// <summary>Counts the host's pushes, so a guest can tell a new push from the one it already saw.</summary>
    public int PushSerial { get; private set; }

    public BoardPin Find(ushort id)
    {
        foreach (var p in _pins) if (p.Id == id) return p;
        return null;
    }

    public int CountOwnedBy(int ownerKey)
    {
        int n = 0;
        foreach (var p in _pins) if (p.OwnerKey == ownerKey) n++;
        return n;
    }

    // ---------------------------------------------------------------- host edits

    /// <summary>Adds a pin (host). Null with <paramref name="error"/> when refused.</summary>
    public BoardPin Add(string name, float x, float y, float z, byte color, string owner, int ownerKey, out string error)
    {
        error = null;
        if (!ValidCoordinate(x) || !ValidCoordinate(y) || !ValidCoordinate(z)) { error = "the spot is off the map"; return null; }
        if (_pins.Count >= MaxPins) { error = $"the session already has {MaxPins} shared pins"; return null; }
        name = CleanText(name, MaxName);
        if (name.Length == 0) name = "Pin " + (_pins.Count + 1);
        var pin = new BoardPin
        {
            Id = NewId(),
            Name = name,
            X = x, Y = y, Z = z,
            Color = (byte)(color % ColorCount),
            Owner = CleanText(owner, MaxOwner),
            OwnerKey = ownerKey,
        };
        _pins.Add(pin);
        Revision++;
        return pin;
    }

    /// <summary>Removes a pin: the host may remove any, a guest only its own. False when nothing was removed.</summary>
    public bool Remove(ushort id, int byOwnerKey, bool asHost)
    {
        var p = Find(id);
        if (p == null || (!asHost && p.OwnerKey != byOwnerKey)) return false;
        _pins.Remove(p);
        if (PushedId == id) PushedId = 0;
        Revision++;
        return true;
    }

    /// <summary>Removes every pin a player added (they left). Returns how many.</summary>
    public int RemoveOwnedBy(int ownerKey)
    {
        int n = _pins.RemoveAll(p => p.OwnerKey == ownerKey);
        if (n == 0) return 0;
        if (PushedId != 0 && Find(PushedId) == null) PushedId = 0;
        Revision++;
        return n;
    }

    /// <summary>The host pushes a pin to everyone (their route switches to it). False for an unknown id.</summary>
    public bool Push(ushort id)
    {
        if (Find(id) == null) return false;
        PushedId = id;
        PushSerial++;
        Revision++;
        return true;
    }

    public void Clear()
    {
        if (_pins.Count == 0 && PushedId == 0) return;
        _pins.Clear();
        PushedId = 0;
        Revision++;
    }

    private ushort NewId()
    {
        for (int tries = 0; tries < 70000; tries++)
        {
            ushort id = _nextId++;
            if (_nextId == 0) _nextId = 1;
            if (id != 0 && Find(id) == null) return id;
        }
        return 1; // unreachable with MaxPins pins
    }

    // ---------------------------------------------------------------- snapshots

    /// <summary>The whole board for the wire.</summary>
    public byte[] Encode()
    {
        var w = new NetWriter(64 + _pins.Count * 48)
            .Byte(WireVersion).Int(Epoch).Int(Revision).UShort(PushedId).Int(PushSerial)
            .Byte((byte)_pins.Count);
        foreach (var p in _pins)
            w.UShort(p.Id).String(p.Name, MaxName).Float(p.X).Float(p.Y).Float(p.Z).Byte(p.Color)
             .String(p.Owner, MaxOwner).Int(p.OwnerKey);
        return w.ToArray();
    }

    public readonly struct ApplyResult
    {
        public readonly bool Applied, NewPush;
        public ApplyResult(bool applied, bool newPush) { Applied = applied; NewPush = newPush; }
    }

    /// <summary>
    /// Guest: replaces the board with a host snapshot when it is newer (another epoch, or a higher revision).
    /// <see cref="ApplyResult.NewPush"/> is true when the snapshot carries a push this board had not seen yet.
    /// </summary>
    public ApplyResult ApplySnapshot(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 16) return default;
        var r = new NetReader(bytes);
        if (r.Byte() != WireVersion) return default;
        int epoch = r.Int(), revision = r.Int();
        ushort pushed = r.UShort();
        int serial = r.Int();
        int count = r.Byte();
        if (!r.Ok || count > MaxPins) return default;
        if (epoch == Epoch && revision <= Revision) return default;

        var pins = new List<BoardPin>(count);
        var seen = new HashSet<ushort>();
        for (int i = 0; i < count; i++)
        {
            var p = new BoardPin
            {
                Id = r.UShort(),
                Name = CleanText(r.String(MaxName), MaxName),
                X = r.Float(), Y = r.Float(), Z = r.Float(),
                Color = r.Byte(),
                Owner = CleanText(r.String(MaxOwner), MaxOwner),
                OwnerKey = r.Int(),
            };
            if (!r.Ok) return default;
            if (p.Id == 0 || !seen.Add(p.Id)) continue;
            if (!ValidCoordinate(p.X) || !ValidCoordinate(p.Y) || !ValidCoordinate(p.Z)) continue;
            if (p.Color >= ColorCount) p.Color = 0;
            if (p.Name.Length == 0) p.Name = "Pin";
            pins.Add(p);
        }

        bool newEpoch = epoch != Epoch;
        bool newPush = pushed != 0 && (newEpoch || serial != PushSerial) && seen.Contains(pushed);
        Epoch = epoch;
        Revision = revision;
        PushSerial = serial;
        _pins.Clear();
        _pins.AddRange(pins);
        PushedId = Find(pushed) != null ? pushed : (ushort)0;
        return new ApplyResult(true, newPush);
    }

    /// <summary>Forgets everything (left the session).</summary>
    public void Reset()
    {
        _pins.Clear();
        Epoch = 0;
        Revision = 0;
        PushedId = 0;
        PushSerial = 0;
    }

    // ---------------------------------------------------------------- guest requests

    public static byte[] EncodeAdd(string name, float x, float y, float z, byte color) =>
        new NetWriter(48).String(CleanText(name, MaxName), MaxName).Float(x).Float(y).Float(z).Byte(color).ToArray();

    public static bool TryDecodeAdd(byte[] bytes, out string name, out float x, out float y, out float z, out byte color)
    {
        var r = new NetReader(bytes);
        name = CleanText(r.String(MaxName), MaxName);
        x = r.Float(); y = r.Float(); z = r.Float();
        color = r.Byte();
        return r.Ok;
    }

    public static byte[] EncodeId(ushort id) => new NetWriter(2).UShort(id).ToArray();

    public static bool TryDecodeId(byte[] bytes, out ushort id)
    {
        var r = new NetReader(bytes);
        id = r.UShort();
        return r.Ok;
    }

    // ---------------------------------------------------------------- helpers

    public static bool ValidCoordinate(float v) => float.IsFinite(v) && Math.Abs(v) <= MaxCoordinate;

    /// <summary>One plain line: no control characters, no markup brackets, trimmed and cut to <paramref name="max"/>.</summary>
    public static string CleanText(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max + 8));
        bool space = false;
        foreach (char c in s)
        {
            if (c == '<' || c == '>') continue;
            if (char.IsControl(c) || char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        string t = sb.ToString().Trim();
        return t.Length > max ? t.Substring(0, max).TrimEnd() : t;
    }
}

/// <summary>A named spot one mod offers to the others (pure).</summary>
internal readonly struct NamedPoint
{
    public readonly string Name;
    public readonly float X, Y, Z;
    /// <summary>Where it comes from, for lists ("saved", "session", "map pin").</summary>
    public readonly string Source;
    /// <summary>Index into the mods' pin colours (0 .. PinBoard.ColorCount-1), or <see cref="NoColor"/>.</summary>
    public readonly byte Color;

    public const byte NoColor = 255;

    public NamedPoint(string name, float x, float y, float z, string source, int color = NoColor)
    {
        Name = name ?? "";
        X = x; Y = y; Z = z;
        Source = source ?? "";
        Color = color >= 0 && color < PinBoard.ColorCount ? (byte)color : NoColor;
    }

    public bool HasColor => Color != NoColor;
}

internal static class PinSources
{
    private static readonly Dictionary<string, NamedPoint[]> ByOwner = new(StringComparer.Ordinal);
    private static readonly SortedDictionary<string, NamedPoint> Targets = new(StringComparer.Ordinal);
    private static List<NamedPoint> _all;

    /// <summary>Sets (or with <paramref name="on"/> false, clears) the spot <paramref name="owner"/>'s route leads to.</summary>
    public static void SetTarget(string owner, bool on, NamedPoint target)
    {
        if (string.IsNullOrEmpty(owner)) return;
        if (!on || !PinBoard.ValidCoordinate(target.X) || !PinBoard.ValidCoordinate(target.Y) || !PinBoard.ValidCoordinate(target.Z))
        {
            Targets.Remove(owner);
            return;
        }
        Targets[owner] = target;
    }

    /// <summary>The route target of the first owner (by name) that has one.</summary>
    public static bool TryTarget(out NamedPoint target)
    {
        foreach (var kv in Targets)
        {
            target = kv.Value;
            return true;
        }
        target = default;
        return false;
    }

    /// <summary>Replaces what <paramref name="owner"/> offers.</summary>
    public static void Publish(string owner, IReadOnlyCollection<NamedPoint> points)
    {
        if (string.IsNullOrEmpty(owner)) return;
        if (points == null || points.Count == 0) ByOwner.Remove(owner);
        else
        {
            var copy = new NamedPoint[points.Count];
            int i = 0;
            foreach (var p in points) copy[i++] = p;
            ByOwner[owner] = copy;
        }
        _all = null;
    }

    /// <summary>Every published spot, by owner name then in the owner's order.</summary>
    public static IReadOnlyList<NamedPoint> All()
    {
        if (_all != null) return _all;
        var keys = new List<string>(ByOwner.Keys);
        keys.Sort(StringComparer.Ordinal);
        var list = new List<NamedPoint>();
        foreach (var k in keys) list.AddRange(ByOwner[k]);
        return _all = list;
    }

    public static void Clear()
    {
        ByOwner.Clear();
        Targets.Clear();
        _all = null;
    }
}
