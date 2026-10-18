using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Shared;

namespace OverTheHillPlus.Race;

/// <summary>Sprint: from the first gate to the last. Circuit: laps of all gates, back through the first.</summary>
internal enum RaceKind : byte { Sprint = 0, Circuit = 1 }

/// <summary>A checkpoint: a circle on the ground (with some height tolerance) that racers drive through.</summary>
internal sealed class RaceGate
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; } = 12f;
}

/// <summary>
/// A race track (pure logic): its gates, kind and laps, plus the gate order racers must follow, a stable id for its
/// leaderboard, and its wire and file formats. Gate 0 is where the race starts.
/// <list type="bullet">
/// <item>Sprint: racers pass gates 1 .. n-1 in order; the last one is the finish.</item>
/// <item>Circuit: every lap is gates 1 .. n-1 then gate 0 again; after the last lap gate 0 is the finish.</item>
/// </list>
/// </summary>
internal sealed class RaceTrack
{
    public const byte WireVersion = 1;
    public const int MaxGates = 40;
    public const int MaxLaps = 20;
    public const float MinRadius = 3f;
    public const float MaxRadius = 60f;
    public const int MaxName = 32;
    public const int MaxArea = 48;

    public string Name { get; set; } = "Track";
    /// <summary>The map the track belongs to (the area key).</summary>
    public string Area { get; set; } = "";
    public RaceKind Kind { get; set; }
    public int Laps { get; set; } = 1;
    public List<RaceGate> Gates { get; set; } = new();

    /// <summary>How many gate passes a full race takes.</summary>
    [JsonIgnore]
    public int SequenceLength => Gates.Count < 2 ? 0 : Kind == RaceKind.Sprint ? Gates.Count - 1 : Gates.Count * Math.Max(1, Laps);

    /// <summary>The gate index racers must pass at step <paramref name="seq"/> (0-based).</summary>
    public int GateAt(int seq)
    {
        int n = Gates.Count;
        if (n < 2) return 0;
        return Kind == RaceKind.Sprint ? Math.Min(seq + 1, n - 1) : (seq % n + 1) % n;
    }

    /// <summary>The lap (1-based) step <paramref name="seq"/> belongs to.</summary>
    public int LapOf(int seq) => Kind == RaceKind.Sprint || Gates.Count < 2 ? 1 : seq / Gates.Count + 1;

    /// <summary>Step <paramref name="seq"/> completes a lap (circuits only).</summary>
    public bool IsLapEnd(int seq) => Kind == RaceKind.Circuit && Gates.Count >= 2 && (seq + 1) % Gates.Count == 0;

    /// <summary>Null when the track can be raced, else why not.</summary>
    public string Validate()
    {
        if (Gates == null || Gates.Count < 2) return "a track needs at least 2 checkpoints (the start and one more)";
        if (Gates.Count > MaxGates) return $"a track has at most {MaxGates} checkpoints";
        foreach (var g in Gates)
        {
            if (g == null || !PinBoard.ValidCoordinate(g.X) || !PinBoard.ValidCoordinate(g.Y) || !PinBoard.ValidCoordinate(g.Z))
                return "a checkpoint is off the map";
            if (!float.IsFinite(g.Radius)) return "a checkpoint has no size";
        }
        return null;
    }

    /// <summary>Clamps everything into range (after loading a file or a message).</summary>
    public void Clean()
    {
        Name = PinBoard.CleanText(Name, MaxName);
        if (Name.Length == 0) Name = "Track";
        Area = PinBoard.CleanText(Area, MaxArea);
        if (Kind != RaceKind.Sprint && Kind != RaceKind.Circuit) Kind = RaceKind.Sprint;
        Laps = Math.Clamp(Laps, 1, MaxLaps);
        Gates ??= new List<RaceGate>();
        Gates.RemoveAll(g => g == null);
        if (Gates.Count > MaxGates) Gates.RemoveRange(MaxGates, Gates.Count - MaxGates);
        foreach (var g in Gates)
            g.Radius = float.IsFinite(g.Radius) ? Math.Clamp(g.Radius, MinRadius, MaxRadius) : 12f;
    }

    /// <summary>
    /// Stable id for the leaderboard: the file-safe name plus a hash of the map, kind, laps and the gates rounded to
    /// a metre, so moving a gate makes a new board while float noise does not.
    /// </summary>
    [JsonIgnore]
    public string Id
    {
        get
        {
            unchecked
            {
                uint h = 2166136261;
                void Mix(int v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (i * 8)); h *= 16777619; } }
                foreach (char c in Area ?? "") Mix(c);
                Mix((int)Kind);
                Mix(Kind == RaceKind.Circuit ? Laps : 1);
                foreach (var g in Gates ?? new List<RaceGate>())
                {
                    Mix((int)MathF.Round(g.X));
                    Mix((int)MathF.Round(g.Y));
                    Mix((int)MathF.Round(g.Z));
                    Mix((int)MathF.Round(g.Radius));
                }
                return FileNameFor(Name) + "-" + h.ToString("x8");
            }
        }
    }

    /// <summary>A file-safe lower-case name: letters, digits and dashes only.</summary>
    public static string FileNameFor(string name, int max = 40)
    {
        var sb = new StringBuilder();
        bool dash = false;
        foreach (char c in (name ?? "").ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                if (dash && sb.Length > 0) sb.Append('-');
                dash = false;
                sb.Append(c);
            }
            else dash = true;
            if (sb.Length >= max) break;
        }
        return sb.Length == 0 ? "track" : sb.ToString();
    }

    /// <summary>
    /// The file (without .json) to save a track named <paramref name="name"/> in: its file-safe name, or with -2, -3, ...
    /// when that file already holds a different track. <paramref name="nameInFile"/> gives the track name stored in a
    /// file, or null when there is no such file. The same name (ignoring case) is the same track: saved over.
    /// </summary>
    public static string FileFor(string name, Func<string, string> nameInFile)
    {
        string clean = PinBoard.CleanText(name, MaxName);
        string baseName = FileNameFor(clean);
        for (int n = 1; n < 1000; n++)
        {
            string file = n == 1 ? baseName : baseName + "-" + n;
            string there = nameInFile?.Invoke(file);
            if (there == null) return file;
            string thereClean = PinBoard.CleanText(there, MaxName);
            if (thereClean.Length > 0 && string.Equals(thereClean, clean, StringComparison.OrdinalIgnoreCase)) return file;
        }
        return baseName + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    public RaceTrack Copy()
    {
        var t = new RaceTrack { Name = Name, Area = Area, Kind = Kind, Laps = Laps };
        foreach (var g in Gates) t.Gates.Add(new RaceGate { X = g.X, Y = g.Y, Z = g.Z, Radius = g.Radius });
        return t;
    }

    // ---------------------------------------------------------------- wire

    public byte[] Encode()
    {
        var w = new NetWriter(32 + Gates.Count * 16)
            .Byte(WireVersion).String(Name, MaxName).String(Area, MaxArea).Byte((byte)Kind).Byte((byte)Math.Clamp(Laps, 1, MaxLaps))
            .Byte((byte)Math.Min(Gates.Count, MaxGates));
        for (int i = 0; i < Gates.Count && i < MaxGates; i++)
            w.Float(Gates[i].X).Float(Gates[i].Y).Float(Gates[i].Z).Float(Gates[i].Radius);
        return w.ToArray();
    }

    /// <summary>A track from the wire, cleaned and valid, or null.</summary>
    public static RaceTrack Decode(byte[] bytes)
    {
        var r = new NetReader(bytes);
        if (r.Byte() != WireVersion) return null;
        var t = new RaceTrack { Name = r.String(MaxName), Area = r.String(MaxArea), Kind = (RaceKind)r.Byte(), Laps = r.Byte() };
        int n = r.Byte();
        if (!r.Ok || n > MaxGates) return null;
        for (int i = 0; i < n; i++)
            t.Gates.Add(new RaceGate { X = r.Float(), Y = r.Float(), Z = r.Float(), Radius = r.Float() });
        if (!r.Ok) return null;
        t.Clean();
        return t.Validate() == null ? t : null;
    }

    // ---------------------------------------------------------------- files

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>A track from a file, cleaned and valid, or null.</summary>
    public static RaceTrack FromJson(string json)
    {
        try
        {
            var t = JsonSerializer.Deserialize<RaceTrack>(json ?? "");
            if (t == null) return null;
            t.Clean();
            return t.Validate() == null ? t : null;
        }
        catch (Exception ex) { DebugLog.Error("race", ex, "RaceTrack.FromJson"); return null; }
    }
}
