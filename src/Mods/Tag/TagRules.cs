using System;
using System.Collections.Generic;
using System.IO;

namespace OverTheHillPlus.Tag;

// Pure round logic (no Unity, no game types): compiled into the mod and linked into src/Tests. Players are ids (the
// host uses PlayerRef.RawEncoded); times are seconds on the host's clock. Only the host runs a TagRound; guests get a
// TagSnapshot of it over the mod network.

public enum TagMode : byte { Tag = 0, HideAndSeek = 1 }

/// <summary>Idle: no round. HeadStart: hide and seek's hiding time (seekers wait). Playing. Over: results shown.</summary>
public enum TagPhase : byte { Idle = 0, HeadStart = 1, Playing = 2, Over = 3 }

public enum ContactResult { Tagged, Found, NotPlaying, NotIt, Cooldown, SameSide, UnknownPlayer }

public enum TagEventKind { Tagged, Found, SeekersReleased, NewIt, RoundOver }

/// <summary>Something the host announces. Tagged/Found: A did it to B. NewIt: B is it now. RoundOver: see Winners.</summary>
public readonly struct TagEvent
{
    public readonly TagEventKind Kind;
    public readonly int A, B;
    public TagEvent(TagEventKind kind, int a = -1, int b = -1) { Kind = kind; A = a; B = b; }
}

public sealed class TagSettings
{
    public const float MaxRoundSeconds = 3600f, MaxHeadStartSeconds = 600f, MaxTagBackSeconds = 60f;
    /// <summary>A touch reaches the host several times (both cars' collision messages, the distance check every
    /// 0.1 s): without a tag-back window the same touch would hand "it" straight back.</summary>
    public const float MinTagBackSeconds = 1f;
    public float RoundSeconds = 300f;
    public float HeadStartSeconds = 60f;
    /// <summary>The newly tagged player can't tag the one who tagged them for this long (at least
    /// <see cref="MinTagBackSeconds"/>).</summary>
    public float TagBackSeconds = 5f;

    internal static float Clamp(float v, float min, float max) => float.IsNaN(v) ? min : Math.Min(max, Math.Max(min, v));

    internal TagSettings Sanitised() => new()
    {
        RoundSeconds = Clamp(RoundSeconds, 10f, MaxRoundSeconds),
        HeadStartSeconds = Clamp(HeadStartSeconds, 0f, MaxHeadStartSeconds),
        TagBackSeconds = Clamp(TagBackSeconds, MinTagBackSeconds, MaxTagBackSeconds),
    };
}

public sealed class TagPlayer
{
    public int Id;
    /// <summary>Hide and seek: seeking (from the start, or since found, or since joining mid-round).</summary>
    public bool Seeker;
    /// <summary>Hide and seek: a seeker when the round started (only they win when every hider is found).</summary>
    public bool OriginalSeeker;
    public bool Found;
    public bool Winner;
    public int Tags, Finds;
    public float ItSeconds;
}

/// <summary>One round of tag or hide and seek, run by the host.</summary>
public sealed class TagRound
{
    public const int MaxPlayers = 64;

    public TagMode Mode { get; private set; }
    public TagPhase Phase { get; private set; }
    /// <summary>Tag: who is it (-1: nobody).</summary>
    public int It { get; private set; } = -1;
    /// <summary>Tag: the last tagger, whom <see cref="It"/> can't tag back until <see cref="TagBackUntil"/>.</summary>
    public int TagBackFrom { get; private set; } = -1;
    public float TagBackUntil { get; private set; }
    public float HeadStartEndsAt { get; private set; }
    public float EndsAt { get; private set; }
    public readonly List<TagPlayer> Players = new();
    public readonly List<int> Winners = new();

    private TagSettings _s;
    private float _itSince;

    private TagRound() { }

    // ---------------------------------------------------------------- starting

    public static TagRound StartTag(IEnumerable<int> players, int it, float now, TagSettings settings, out string why)
    {
        var r = new TagRound { Mode = TagMode.Tag, _s = (settings ?? new TagSettings()).Sanitised() };
        r.AddAll(players);
        why = null;
        if (r.Players.Count < 2) { why = "needs at least 2 players"; return null; }
        if (r.Find(it) == null) { why = "the chosen player is not in the round"; return null; }
        r.It = it;
        r._itSince = now;
        r.Phase = TagPhase.Playing;
        r.EndsAt = now + r._s.RoundSeconds;
        return r;
    }

    public static TagRound StartHideAndSeek(IEnumerable<int> players, IEnumerable<int> seekers, float now, TagSettings settings, out string why)
    {
        var r = new TagRound { Mode = TagMode.HideAndSeek, _s = (settings ?? new TagSettings()).Sanitised() };
        r.AddAll(players);
        why = null;
        if (r.Players.Count < 2) { why = "needs at least 2 players"; return null; }
        foreach (int s in seekers ?? Array.Empty<int>())
        {
            var p = r.Find(s);
            if (p != null) p.Seeker = p.OriginalSeeker = true;
        }
        int seeking = r.Count(p => p.Seeker);
        if (seeking == 0) { why = "needs at least one seeker"; return null; }
        if (seeking == r.Players.Count) { why = "needs at least one hider"; return null; }
        r.HeadStartEndsAt = now + r._s.HeadStartSeconds;
        r.EndsAt = r.HeadStartEndsAt + r._s.RoundSeconds;
        r.Phase = r._s.HeadStartSeconds > 0f ? TagPhase.HeadStart : TagPhase.Playing;
        return r;
    }

    private void AddAll(IEnumerable<int> players)
    {
        foreach (int id in players ?? Array.Empty<int>())
            if (Find(id) == null && Players.Count < MaxPlayers) Players.Add(new TagPlayer { Id = id });
    }

    /// <summary>A player who joined mid-round: a runner in tag, a seeker in hide and seek. False if already in.</summary>
    public bool AddPlayer(int id)
    {
        if (Find(id) != null || Players.Count >= MaxPlayers || Phase == TagPhase.Idle) return false;
        Players.Add(new TagPlayer { Id = id, Seeker = Mode == TagMode.HideAndSeek });
        return true;
    }

    // ---------------------------------------------------------------- queries

    public TagPlayer Find(int id)
    {
        foreach (var p in Players) if (p.Id == id) return p;
        return null;
    }

    private int Count(Func<TagPlayer, bool> f)
    {
        int n = 0;
        foreach (var p in Players) if (f(p)) n++;
        return n;
    }

    public bool Running => Phase == TagPhase.HeadStart || Phase == TagPhase.Playing;

    public float TimeLeft(float now) => Running ? Math.Max(0f, EndsAt - now) : 0f;

    public float HeadStartLeft(float now) => Phase == TagPhase.HeadStart ? Math.Max(0f, HeadStartEndsAt - now) : 0f;

    /// <summary>Hide and seek: a seeker waiting out the head start (screen blacked out, car held).</summary>
    public bool IsBlinded(int id, float now) => Phase == TagPhase.HeadStart && Find(id)?.Seeker == true && now < HeadStartEndsAt;

    /// <summary>Hide and seek: <paramref name="other"/> is not shown on <paramref name="viewer"/>'s map.</summary>
    public bool HiddenFrom(int viewer, int other) => Running && Mode == TagMode.HideAndSeek && SnapshotRules.Hidden(Find(viewer)?.Seeker, Find(other)?.Seeker);

    // ---------------------------------------------------------------- play

    /// <summary>Two cars touched (host-side contact detection). Returns what to announce.</summary>
    public List<TagEvent> Contact(int a, int b, float now, out ContactResult result)
    {
        var events = new List<TagEvent>();
        var pa = Find(a);
        var pb = Find(b);
        if (pa == null || pb == null || a == b) { result = ContactResult.UnknownPlayer; return events; }
        if (Phase != TagPhase.Playing || now >= EndsAt) { result = ContactResult.NotPlaying; return events; }

        if (Mode == TagMode.Tag)
        {
            if (It != a && It != b) { result = ContactResult.NotIt; return events; }
            var tagger = It == a ? pa : pb;
            var tagged = It == a ? pb : pa;
            if (tagged.Id == TagBackFrom && now < TagBackUntil) { result = ContactResult.Cooldown; return events; }
            AccrueIt(now);
            tagger.Tags++;
            It = tagged.Id;
            TagBackFrom = tagger.Id;
            TagBackUntil = now + _s.TagBackSeconds;
            result = ContactResult.Tagged;
            events.Add(new TagEvent(TagEventKind.Tagged, tagger.Id, tagged.Id));
            return events;
        }

        if (pa.Seeker == pb.Seeker) { result = ContactResult.SameSide; return events; }
        var seeker = pa.Seeker ? pa : pb;
        var hider = pa.Seeker ? pb : pa;
        seeker.Finds++;
        hider.Found = true;
        hider.Seeker = true;
        result = ContactResult.Found;
        events.Add(new TagEvent(TagEventKind.Found, seeker.Id, hider.Id));
        CheckHideAndSeekEnd(now, events);
        return events;
    }

    /// <summary>Phase changes and the round's end. Call a few times a second.</summary>
    public List<TagEvent> Tick(float now)
    {
        var events = new List<TagEvent>();
        if (Phase == TagPhase.Playing && Mode == TagMode.Tag) AccrueIt(Math.Min(now, EndsAt));
        if (Phase == TagPhase.HeadStart && now >= HeadStartEndsAt)
        {
            Phase = TagPhase.Playing;
            events.Add(new TagEvent(TagEventKind.SeekersReleased));
        }
        if (Phase == TagPhase.Playing && now >= EndsAt)
        {
            if (Mode == TagMode.Tag)
            {
                AccrueIt(EndsAt);
                float best = float.MaxValue;
                foreach (var p in Players) best = Math.Min(best, p.ItSeconds);
                var w = new List<int>();
                foreach (var p in Players) if (p.ItSeconds <= best + 0.01f) w.Add(p.Id);
                End(w, events);
            }
            else End(Hiders(), events);
        }
        return events;
    }

    /// <summary>A player left the session. <paramref name="pickIt"/> chooses the new it from the remaining ids.</summary>
    public List<TagEvent> PlayerLeft(int id, float now, Func<IList<int>, int> pickIt)
    {
        var events = new List<TagEvent>();
        var p = Find(id);
        if (p == null) return events;
        if (Running && Mode == TagMode.Tag && It == id) AccrueIt(now);
        Players.Remove(p);
        if (!Running) return events;

        if (Mode == TagMode.Tag)
        {
            if (Players.Count < 2) { End(new List<int>(), events); return events; }
            if (TagBackFrom == id) TagBackFrom = -1;
            if (It == id)
            {
                var ids = new List<int>();
                foreach (var q in Players) ids.Add(q.Id);
                int pick = pickIt != null ? pickIt(ids) : ids[0];
                It = ids.Contains(pick) ? pick : ids[0];
                _itSince = now;
                TagBackFrom = -1;
                events.Add(new TagEvent(TagEventKind.NewIt, -1, It));
            }
            return events;
        }

        if (Count(q => q.Seeker) == 0) { End(Hiders(), events); return events; }
        CheckHideAndSeekEnd(now, events);
        return events;
    }

    /// <summary>The host stopped the round: it ends without winners.</summary>
    public void Stop(float now)
    {
        if (!Running) return;
        if (Mode == TagMode.Tag) AccrueIt(now);
        Phase = TagPhase.Over;
        Winners.Clear();
    }

    private List<int> Hiders()
    {
        var list = new List<int>();
        foreach (var p in Players) if (!p.Seeker) list.Add(p.Id);
        return list;
    }

    private void CheckHideAndSeekEnd(float now, List<TagEvent> events)
    {
        int hiders = Count(p => !p.Seeker);
        int startedHiders = Count(p => !p.Seeker || p.Found);
        if (hiders == 0)
        {
            // Everyone was found: the seekers who started the round win (not those found, nor late joiners).
            var seekers = new List<int>();
            foreach (var p in Players) if (p.OriginalSeeker) seekers.Add(p.Id);
            End(seekers, events);
        }
        else if (hiders == 1 && startedHiders > 1) End(Hiders(), events);
    }

    private void AccrueIt(float now)
    {
        var p = Find(It);
        if (p != null && now > _itSince) p.ItSeconds += now - _itSince;
        _itSince = now;
    }

    private void End(List<int> winners, List<TagEvent> events)
    {
        Phase = TagPhase.Over;
        Winners.Clear();
        Winners.AddRange(winners);
        foreach (var p in Players) p.Winner = winners.Contains(p.Id);
        events.Add(new TagEvent(TagEventKind.RoundOver));
    }

    // ---------------------------------------------------------------- wire

    /// <summary>The round as guests see it (times relative to <paramref name="now"/>, so clocks need not agree).</summary>
    public byte[] Encode(float now, int roundNumber)
    {
        float itSoFar = Running && Mode == TagMode.Tag ? Math.Max(0f, now - _itSince) : 0f;
        var rows = new List<TagSnapshot.Row>(Players.Count);
        foreach (var p in Players)
            rows.Add(new TagSnapshot.Row
            {
                Id = p.Id, Seeker = p.Seeker, Found = p.Found, Winner = p.Winner, Tags = p.Tags, Finds = p.Finds,
                ItSeconds = p.ItSeconds + (p.Id == It ? itSoFar : 0f),
            });
        return TagSnapshot.Write(roundNumber, Mode, Phase, It, TagBackFrom, Math.Max(0f, TagBackUntil - now),
            HeadStartLeft(now), TimeLeft(now), rows);
    }
}

internal static class SnapshotRules
{
    /// <summary>Seekers' maps hide hiders (null = not in the round: nothing hidden).</summary>
    public static bool Hidden(bool? viewerSeeks, bool? otherSeeks) => viewerSeeks == true && otherSeeks == false;
}

/// <summary>A decoded round, as a guest (or the host's own HUD) sees it.</summary>
public sealed class TagSnapshot
{
    public const byte WireVersion = 1;
    /// <summary>Byte offset of the player count (for tests).</summary>
    internal const int PlayerCountOffset = 1 + 4 + 1 + 1 + 4 + 4 + 4 + 4 + 4;
    private const int RowBytes = 4 + 1 + 2 + 2 + 4;

    public sealed class Row
    {
        public int Id;
        public bool Seeker, Found, Winner;
        public int Tags, Finds;
        public float ItSeconds;
    }

    public int Round;
    public TagMode Mode;
    public TagPhase Phase;
    public int It = -1, TagBackFrom = -1;
    public float TagBackLeft, HeadStartLeft, TimeLeft;
    public readonly List<Row> Players = new();

    public bool Running => Phase == TagPhase.HeadStart || Phase == TagPhase.Playing;

    public Row Find(int id)
    {
        foreach (var p in Players) if (p.Id == id) return p;
        return null;
    }

    public bool HiddenFrom(int viewer, int other) => Running && Mode == TagMode.HideAndSeek && SnapshotRules.Hidden(Find(viewer)?.Seeker, Find(other)?.Seeker);

    public static byte[] EncodeIdle() => Write(0, TagMode.Tag, TagPhase.Idle, -1, -1, 0f, 0f, 0f, new List<Row>());

    internal static byte[] Write(int round, TagMode mode, TagPhase phase, int it, int tagBackFrom, float tagBackLeft,
        float headStartLeft, float timeLeft, List<Row> rows)
    {
        using var ms = new MemoryStream(PlayerCountOffset + 1 + rows.Count * RowBytes);
        using var w = new BinaryWriter(ms);
        w.Write(WireVersion);
        w.Write(round);
        w.Write((byte)mode);
        w.Write((byte)phase);
        w.Write(it);
        w.Write(tagBackFrom);
        w.Write(Safe(tagBackLeft, TagSettings.MaxTagBackSeconds));
        w.Write(Safe(headStartLeft, TagSettings.MaxHeadStartSeconds));
        w.Write(Safe(timeLeft, TagSettings.MaxRoundSeconds));
        int n = Math.Min(rows.Count, TagRound.MaxPlayers);
        w.Write((byte)n);
        for (int i = 0; i < n; i++)
        {
            var r = rows[i];
            w.Write(r.Id);
            w.Write((byte)((r.Seeker ? 1 : 0) | (r.Found ? 2 : 0) | (r.Winner ? 4 : 0)));
            w.Write((ushort)Math.Min(ushort.MaxValue, Math.Max(0, r.Tags)));
            w.Write((ushort)Math.Min(ushort.MaxValue, Math.Max(0, r.Finds)));
            w.Write(Safe(r.ItSeconds, 100000f));
        }
        w.Flush();
        return ms.ToArray();
    }

    private static float Safe(float v, float max) => float.IsFinite(v) ? Math.Min(max, Math.Max(0f, v)) : max;

    /// <summary>Null when the bytes are not a whole, sane snapshot of this version.</summary>
    public static TagSnapshot Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < PlayerCountOffset + 1 || bytes[0] != WireVersion) return null;
        try
        {
            using var ms = new MemoryStream(bytes, false);
            using var rd = new BinaryReader(ms);
            rd.ReadByte();
            var s = new TagSnapshot { Round = rd.ReadInt32() };
            byte mode = rd.ReadByte(), phase = rd.ReadByte();
            if (mode > (byte)TagMode.HideAndSeek || phase > (byte)TagPhase.Over) return null;
            s.Mode = (TagMode)mode;
            s.Phase = (TagPhase)phase;
            s.It = rd.ReadInt32();
            s.TagBackFrom = rd.ReadInt32();
            s.TagBackLeft = Safe(rd.ReadSingle(), TagSettings.MaxTagBackSeconds);
            s.HeadStartLeft = Safe(rd.ReadSingle(), TagSettings.MaxHeadStartSeconds);
            s.TimeLeft = Safe(rd.ReadSingle(), TagSettings.MaxRoundSeconds);
            int n = rd.ReadByte();
            if (n > TagRound.MaxPlayers || ms.Length - ms.Position < n * (long)RowBytes) return null;
            for (int i = 0; i < n; i++)
            {
                var r = new Row { Id = rd.ReadInt32() };
                byte f = rd.ReadByte();
                r.Seeker = (f & 1) != 0;
                r.Found = (f & 2) != 0;
                r.Winner = (f & 4) != 0;
                r.Tags = rd.ReadUInt16();
                r.Finds = rd.ReadUInt16();
                r.ItSeconds = Safe(rd.ReadSingle(), 100000f);
                if (s.Find(r.Id) != null) return null;
                s.Players.Add(r);
            }
            return s;
        }
        catch (EndOfStreamException) { return null; }
    }
}
