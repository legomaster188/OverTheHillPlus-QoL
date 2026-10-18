using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OverTheHillPlus.Cargo;

// Pure job logic (no Unity, no game types): compiled into the mod and linked into src/Tests. Players are ids (the
// host uses PlayerRef.RawEncoded); times are seconds on the host's clock. Only the host keeps a CargoBoard: it sees
// every car (it simulates them), so it alone decides pick-ups and deliveries from the cars' real positions. Guests
// get the board over the mod network and add pay to their own inventory only for a delivery the host confirmed.

public readonly struct Vec3
{
    public readonly float X, Y, Z;
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

    public static float Distance(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public bool Finite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
}

/// <summary>A place jobs start or end at (a point of interest on the map).</summary>
public sealed class CargoPoint
{
    public string Name;
    public Vec3 Pos;
}

/// <summary>Open: on the board. ToPickup: taken, driving to the pick-up. Loaded: driving to the destination.
/// Delivered jobs leave the board.</summary>
public enum JobState : byte { Open = 0, ToPickup = 1, Loaded = 2 }

public sealed class CargoJob
{
    public int Id;
    public string FromName, ToName;
    public Vec3 From, To;
    public float Distance;
    /// <summary>Pay for a delivery on time without the speed bonus, and the most a delivery can pay.</summary>
    public int Pay, MaxPay;
    /// <summary>Seconds from the pick-up for the on-time pay.</summary>
    public float TimeLimit;
    public JobState State;
    public int Carrier = -1;
    public float TakenAt, LoadedAt;
    /// <summary>Guest view only: seconds of <see cref="TimeLimit"/> left when the board was sent.</summary>
    public float TimeLeft;
}

public sealed class CargoSettings
{
    /// <summary>No single delivery pays more than this, whatever the settings say (guests check it too).</summary>
    public const int HardMaxPayPerJob = 5000;

    public float PickupRadius = 30f, DeliveryRadius = 30f;
    public float MinDistance = 400f, MaxDistance = 6000f;
    public int BasePay = 50, PayPerKm = 120, MaxPay = 2000;
    public float SecondsPerKm = 120f, ExtraSeconds = 90f;
    /// <summary>On-time bonus at an instant delivery, as a fraction of the pay (it slides to 0 at the time limit).</summary>
    public float BonusFraction = 0.5f;
    /// <summary>Pay for a late delivery, as a fraction of the pay.</summary>
    public float LatePayFraction = 0.5f;
    /// <summary>A delivery faster than the straight line at this speed (m/s) is refused (no teleported cargo).</summary>
    public float MaxPlausibleSpeed = 60f;
    /// <summary>A taken job goes back on the board when not picked up in this many seconds.</summary>
    public float PickupTimeout = 900f;
    /// <summary>A loaded job goes back on the board after this many times its time limit.</summary>
    public float LoadedTimeoutFactor = 3f;
    public int BoardSize = 6;
}

public enum TakeResult { Ok, NoSuchJob, AlreadyTaken, HaveAJob }

public enum CargoEventKind { Loaded, Delivered, Rejected, TimedOut }

public readonly struct CargoEvent
{
    public readonly CargoEventKind Kind;
    public readonly int JobId, Player, Pay;
    public CargoEvent(CargoEventKind kind, int jobId, int player, int pay = 0) { Kind = kind; JobId = jobId; Player = player; Pay = pay; }
}

public static class CargoRules
{
    public static int BasePayFor(float distance, CargoSettings s)
    {
        double pay = s.BasePay + s.PayPerKm * (distance / 1000.0);
        int max = Math.Min(Math.Max(1, s.MaxPay), CargoSettings.HardMaxPayPerJob);
        if (double.IsNaN(pay) || pay < 1) return 1;
        return pay > max ? max : (int)Math.Round(pay);
    }

    public static float TimeLimitFor(float distance, CargoSettings s) =>
        Math.Max(30f, s.ExtraSeconds + s.SecondsPerKm * distance / 1000f);

    /// <summary>The pay for a delivery <paramref name="elapsed"/> seconds after the pick-up.</summary>
    public static int PayFor(CargoJob job, float elapsed, CargoSettings s)
    {
        if (job.TimeLimit > 0f && elapsed <= job.TimeLimit)
        {
            float bonus = job.Pay * Clamp01(s.BonusFraction) * (1f - Math.Max(0f, elapsed) / job.TimeLimit);
            return Math.Min(job.MaxPay, job.Pay + (int)Math.Round(bonus));
        }
        return Math.Max(1, (int)Math.Round(job.Pay * Clamp01(s.LatePayFraction)));
    }

    /// <summary>
    /// The key a player's hourly pay is counted under: their account id when the session knows it (it stays the same
    /// when they rejoin and get another player slot), else their slot in this session.
    /// </summary>
    public static string LedgerKey(string userId, int playerId) =>
        string.IsNullOrWhiteSpace(userId) ? "slot:" + playerId : "user:" + userId.Trim();

    /// <summary>The least time a real drive from pick-up to destination can take.</summary>
    public static float MinTravelSeconds(float distance, CargoSettings s) => distance / Math.Max(1f, s.MaxPlausibleSpeed);

    internal static float Clamp01(float v) => float.IsNaN(v) ? 0f : Math.Min(1f, Math.Max(0f, v));
}

/// <summary>The host's job board: the shared list every modded player sees.</summary>
public sealed class CargoBoard
{
    public const byte WireVersion = 1;
    public const int MaxJobs = 32, MaxNameChars = 40;
    /// <summary>The most bytes <see cref="Encode"/> writes by default (under the mod network's 8192-byte cap).</summary>
    public const int MaxEncodedBytes = 8000;
    /// <summary>Byte offset of the first job (for tests).</summary>
    internal const int FirstJobOffset = 2;

    public readonly List<CargoJob> Jobs = new();
    private readonly CargoSettings _s;
    private int _nextId;

    /// <param name="firstId">Id of the first job. Pass the previous board's <see cref="NextId"/> when the board is
    /// replaced (new map), so job ids never repeat in a session: a guest pays each job id only once.</param>
    public CargoBoard(CargoSettings settings, int firstId = 1)
    {
        _s = settings ?? new CargoSettings();
        _nextId = Math.Max(1, firstId);
    }

    public CargoSettings Settings => _s;

    /// <summary>The id the next job gets.</summary>
    public int NextId => _nextId;

    /// <summary>Adds one job (no checks besides a sane name and position).</summary>
    public CargoJob Add(CargoPoint from, CargoPoint to)
    {
        float d = Vec3.Distance(from.Pos, to.Pos);
        int pay = CargoRules.BasePayFor(d, _s);
        var job = new CargoJob
        {
            Id = TakeId(), FromName = Name(from.Name), ToName = Name(to.Name), From = from.Pos, To = to.Pos, Distance = d,
            Pay = pay, TimeLimit = CargoRules.TimeLimitFor(d, _s), State = JobState.Open,
        };
        job.MaxPay = Math.Min(CargoSettings.HardMaxPayPerJob, pay + (int)Math.Ceiling(pay * CargoRules.Clamp01(_s.BonusFraction)));
        job.TimeLeft = job.TimeLimit;
        Jobs.Add(job);
        return job;
    }

    private int TakeId()
    {
        int id = _nextId;
        _nextId = _nextId == int.MaxValue ? 1 : _nextId + 1;
        return id;
    }

    private static string Name(string n)
    {
        n = string.IsNullOrWhiteSpace(n) ? "?" : n.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return n.Length > MaxNameChars ? n.Substring(0, MaxNameChars) : n;
    }

    /// <summary>Tops the board up to the board size with jobs between two different points in the distance band.</summary>
    public int Refill(IList<CargoPoint> points, Random rng)
    {
        if (points == null || points.Count < 2 || rng == null) return 0;
        int want = Math.Min(MaxJobs, Math.Max(0, _s.BoardSize)), added = 0;
        for (int tries = 0; Jobs.Count < want && tries < 40 * Math.Max(1, want); tries++)
        {
            var a = points[rng.Next(points.Count)];
            var b = points[rng.Next(points.Count)];
            if (a == null || b == null || ReferenceEquals(a, b) || !a.Pos.Finite || !b.Pos.Finite) continue;
            float d = Vec3.Distance(a.Pos, b.Pos);
            if (d < _s.MinDistance || d > _s.MaxDistance) continue;
            string fa = Name(a.Name), tb = Name(b.Name);
            if (fa == tb) continue;
            bool dup = false;
            foreach (var j in Jobs) if (j.FromName == fa && j.ToName == tb) { dup = true; break; }
            if (dup) continue;
            Add(a, b);
            added++;
        }
        return added;
    }

    public CargoJob JobOf(int player)
    {
        foreach (var j in Jobs) if (j.Carrier == player && j.State != JobState.Open) return j;
        return null;
    }

    public CargoJob Get(int id)
    {
        foreach (var j in Jobs) if (j.Id == id) return j;
        return null;
    }

    public TakeResult Take(int player, int jobId, float now)
    {
        var job = Get(jobId);
        if (job == null) return TakeResult.NoSuchJob;
        if (job.State != JobState.Open) return job.Carrier == player ? TakeResult.HaveAJob : TakeResult.AlreadyTaken;
        if (JobOf(player) != null) return TakeResult.HaveAJob;
        job.State = JobState.ToPickup;
        job.Carrier = player;
        job.TakenAt = now;
        return TakeResult.Ok;
    }

    public bool Abandon(int player)
    {
        var job = JobOf(player);
        if (job == null) return false;
        Reopen(job);
        return true;
    }

    public void PlayerLeft(int player) => Abandon(player);

    private static void Reopen(CargoJob job)
    {
        job.State = JobState.Open;
        job.Carrier = -1;
        job.TimeLeft = job.TimeLimit;
    }

    /// <summary>The host saw <paramref name="player"/>'s car at <paramref name="pos"/>: pick-up or delivery, if any.</summary>
    public CargoEvent? Update(int player, Vec3 pos, float now)
    {
        var job = JobOf(player);
        if (job == null || !pos.Finite) return null;
        if (job.State == JobState.ToPickup)
        {
            if (Vec3.Distance(pos, job.From) > _s.PickupRadius) return null;
            job.State = JobState.Loaded;
            job.LoadedAt = now;
            return new CargoEvent(CargoEventKind.Loaded, job.Id, player);
        }
        if (Vec3.Distance(pos, job.To) > _s.DeliveryRadius) return null;
        float elapsed = now - job.LoadedAt;
        if (elapsed < CargoRules.MinTravelSeconds(job.Distance, _s))
        {
            Reopen(job);
            return new CargoEvent(CargoEventKind.Rejected, job.Id, player);
        }
        Jobs.Remove(job);
        return new CargoEvent(CargoEventKind.Delivered, job.Id, player, CargoRules.PayFor(job, elapsed, _s));
    }

    /// <summary>Jobs nobody moved go back on the board.</summary>
    public List<CargoEvent> Expire(float now)
    {
        var events = new List<CargoEvent>();
        foreach (var j in Jobs)
        {
            bool late = j.State == JobState.ToPickup && now - j.TakenAt > _s.PickupTimeout
                        || j.State == JobState.Loaded && now - j.LoadedAt > j.TimeLimit * Math.Max(1f, _s.LoadedTimeoutFactor);
            if (!late) continue;
            events.Add(new CargoEvent(CargoEventKind.TimedOut, j.Id, j.Carrier));
            Reopen(j);
        }
        return events;
    }

    // ---------------------------------------------------------------- wire

    /// <summary>
    /// The board for guests, at most <paramref name="maxBytes"/> long: taken jobs go first, and open jobs that don't fit
    /// are left out (long names in a many-byte script can make a full board too big for one message).
    /// </summary>
    public byte[] Encode(float now, int maxBytes = MaxEncodedBytes)
    {
        var chunks = new List<byte[]>();
        int total = FirstJobOffset;
        foreach (var pass in new[] { false, true })
            foreach (var j in Jobs)
            {
                if ((j.State == JobState.Open) != pass || chunks.Count >= MaxJobs) continue;
                byte[] c = EncodeJob(j, now);
                if (total + c.Length > maxBytes) continue;
                chunks.Add(c);
                total += c.Length;
            }
        var bytes = new byte[total];
        bytes[0] = WireVersion;
        bytes[1] = (byte)chunks.Count;
        int at = FirstJobOffset;
        foreach (var c in chunks) { Buffer.BlockCopy(c, 0, bytes, at, c.Length); at += c.Length; }
        return bytes;
    }

    private static byte[] EncodeJob(CargoJob j, float now)
    {
        using var ms = new MemoryStream(96);
        using var w = new BinaryWriter(ms);
        w.Write(j.Id);
        w.Write((byte)j.State);
        w.Write(j.Carrier);
        WriteVec(w, j.From);
        WriteVec(w, j.To);
        w.Write(j.Pay);
        w.Write(j.MaxPay);
        w.Write(j.TimeLimit);
        w.Write(j.State == JobState.Loaded ? Math.Max(0f, j.TimeLimit - (now - j.LoadedAt)) : j.TimeLimit);
        WriteName(w, j.FromName);
        WriteName(w, j.ToName);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The board as a guest sees it; null when the bytes are not a whole, sane board.</summary>
    public static List<CargoJob> Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < FirstJobOffset || bytes[0] != WireVersion) return null;
        try
        {
            using var ms = new MemoryStream(bytes, false);
            using var rd = new BinaryReader(ms);
            rd.ReadByte();
            int n = rd.ReadByte();
            if (n > MaxJobs) return null;
            var list = new List<CargoJob>(n);
            for (int i = 0; i < n; i++)
            {
                var j = new CargoJob { Id = rd.ReadInt32() };
                byte state = rd.ReadByte();
                if (state > (byte)JobState.Loaded) return null;
                j.State = (JobState)state;
                j.Carrier = rd.ReadInt32();
                j.From = ReadVec(rd);
                j.To = ReadVec(rd);
                j.Pay = rd.ReadInt32();
                j.MaxPay = rd.ReadInt32();
                j.TimeLimit = rd.ReadSingle();
                j.TimeLeft = rd.ReadSingle();
                j.FromName = ReadName(rd);
                j.ToName = ReadName(rd);
                if (!j.From.Finite || !j.To.Finite || !float.IsFinite(j.TimeLimit) || !float.IsFinite(j.TimeLeft)) return null;
                if (j.Pay < 0 || j.MaxPay < j.Pay || j.FromName == null || j.ToName == null) return null;
                j.Distance = Vec3.Distance(j.From, j.To);
                foreach (var other in list) if (other.Id == j.Id) return null;
                list.Add(j);
            }
            return ms.Position == ms.Length ? list : null;
        }
        catch (EndOfStreamException) { return null; }
    }

    private static void WriteVec(BinaryWriter w, Vec3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }

    private static Vec3 ReadVec(BinaryReader rd) => new(rd.ReadSingle(), rd.ReadSingle(), rd.ReadSingle());

    private static void WriteName(BinaryWriter w, string s)
    {
        s = Name(s);
        byte[] b = Encoding.UTF8.GetBytes(s);
        while (b.Length > 255) { s = s.Substring(0, s.Length - 1); b = Encoding.UTF8.GetBytes(s); }
        w.Write((byte)b.Length);
        w.Write(b);
    }

    private static string ReadName(BinaryReader rd)
    {
        int len = rd.ReadByte();
        byte[] b = rd.ReadBytes(len);
        if (b.Length != len) throw new EndOfStreamException();
        string s = Encoding.UTF8.GetString(b);
        return s.Length > MaxNameChars ? s.Substring(0, MaxNameChars) : s;
    }
}

/// <summary>
/// Pay per player in the last hour, so a session can't farm pebbles (the cap is a setting). Keyed by a stable identity
/// (see <see cref="CargoRules.LedgerKey"/>; a guest counts its own pay under one fixed key), not by the session's
/// player slot, so leaving and rejoining doesn't reset it.
/// </summary>
public sealed class PayLedger
{
    public const float Window = 3600f;
    private readonly Dictionary<string, List<(float at, int amount)>> _paid = new();

    public int PaidInWindow(string key, float now)
    {
        if (key == null || !_paid.TryGetValue(key, out var list)) return 0;
        list.RemoveAll(e => e.at <= now - Window);
        int sum = 0;
        foreach (var e in list) sum += e.amount;
        return sum;
    }

    /// <summary>What is left of <paramref name="cap"/> this hour (cap 0 or less: no cap).</summary>
    public int Left(string key, float now, int cap) => cap <= 0 ? int.MaxValue : Math.Max(0, cap - PaidInWindow(key, now));

    /// <summary>Records and returns the part of <paramref name="amount"/> the cap allows (0: none).</summary>
    public int Grant(string key, int amount, float now, int cap)
    {
        if (amount <= 0 || key == null) return 0;
        int allowed = Math.Min(amount, Left(key, now, cap));
        if (allowed <= 0) return 0;
        if (!_paid.TryGetValue(key, out var list)) _paid[key] = list = new List<(float, int)>();
        list.Add((now, allowed));
        return allowed;
    }

    /// <summary>Takes back a grant that never reached the player (newest payments first).</summary>
    public void Refund(string key, int amount)
    {
        if (amount <= 0 || key == null || !_paid.TryGetValue(key, out var list)) return;
        for (int i = list.Count - 1; i >= 0 && amount > 0; i--)
        {
            var (at, paid) = list[i];
            int back = Math.Min(paid, amount);
            amount -= back;
            if (back == paid) list.RemoveAt(i);
            else list[i] = (at, paid - back);
        }
    }
}

/// <summary>
/// A guest's own check of a payment message from the host: only for the job this player carries (as last seen on
/// the board), at most what that job can pay, and each receipt and job only once.
/// </summary>
public sealed class PayReceipts
{
    private readonly HashSet<int> _receipts = new(), _jobs = new();

    public bool Check(int receipt, int jobId, int amount, CargoJob myJob, out string why)
    {
        why = null;
        if (myJob == null || myJob.Id != jobId) { why = "not your job"; return false; }
        if (amount <= 0 || amount > myJob.MaxPay || amount > CargoSettings.HardMaxPayPerJob) { why = "more than the job pays"; return false; }
        if (_receipts.Contains(receipt) || _jobs.Contains(jobId)) { why = "already paid"; return false; }
        _receipts.Add(receipt);
        _jobs.Add(jobId);
        return true;
    }

    public void Clear() { _receipts.Clear(); _jobs.Clear(); }
}
