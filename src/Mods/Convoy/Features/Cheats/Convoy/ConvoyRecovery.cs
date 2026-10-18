using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.Convoy;

internal enum StuckKind
{
    None,
    NoProgress, // trying to drive, not moving (mud, rock, hung up on the chassis)
    Flipped,    // on its side or roof
    Water,      // deep water or flooded engine
}

/// <summary>
/// Decides whether a car (AI or player) is stuck, from what it is trying to do and what actually happens:
/// - pushing (throttle) but not moving for a while, or crawling back and forth without getting anywhere;
/// - lying on its side or roof;
/// - sitting in deep water / engine intake flooded.
/// Fed once per frame; all thresholds in seconds come from the settings.
/// </summary>
internal sealed class StuckMonitor
{
    public float NoMoveTime, FlipTime, WaterTime;
    public StuckKind Kind;
    public float StuckSince;
    public Vector3 StuckAt;

    private Vector3 _anchor;
    private float _anchorTime = -1f;
    private float _effortSum, _effortN;
    private bool _crawlStuck;

    public bool IsStuck => Kind != StuckKind.None;

    public void Reset()
    {
        NoMoveTime = FlipTime = WaterTime = 0f;
        Kind = StuckKind.None;
        _anchorTime = -1f;
        _effortSum = _effortN = 0f;
        _crawlStuck = false;
    }

    /// <param name="effort">0..1 how hard the driver is trying to move (|throttle|).</param>
    public void Update(float dt, Vector3 pos, float speed, float effort, float upDot, float waterDepth, bool flooded,
        float stuckSeconds)
    {
        float now = Time.time;
        bool moving = Mathf.Abs(speed) > 0.8f;

        if (effort > 0.25f && !moving) NoMoveTime += dt;
        else NoMoveTime = Mathf.Max(0f, NoMoveTime - dt * (moving ? 3f : 1f));

        FlipTime = upDot < 0.35f ? FlipTime + dt : 0f;
        WaterTime = (waterDepth > 0.85f || flooded) && Mathf.Abs(speed) < 2.5f ? WaterTime + dt : 0f;

        // Crawl check: over a 6 s window, trying most of the time but ending up within 2 m of where it started.
        if (_anchorTime < 0f) { _anchor = pos; _anchorTime = now; _effortSum = _effortN = 0f; }
        _effortSum += effort;
        _effortN += 1f;
        if (now - _anchorTime > 6f)
        {
            float avg = _effortN > 0f ? _effortSum / _effortN : 0f;
            float moved = ConvoyTrail.Flat(pos - _anchor).magnitude;
            _crawlStuck = avg > 0.3f && moved < 2.0f;
            _anchor = pos;
            _anchorTime = now;
            _effortSum = _effortN = 0f;
        }

        var kind = StuckKind.None;
        if (FlipTime > Mathf.Max(1.5f, stuckSeconds * 0.6f)) kind = StuckKind.Flipped;
        else if (WaterTime > stuckSeconds) kind = StuckKind.Water;
        else if (NoMoveTime > stuckSeconds || _crawlStuck) kind = StuckKind.NoProgress;

        if (kind != StuckKind.None && Kind == StuckKind.None)
        {
            StuckSince = now;
            StuckAt = pos;
        }
        // Once stuck, it stays stuck until the car has clearly moved away (or is upright / dry again).
        if (kind == StuckKind.None && Kind != StuckKind.None)
        {
            bool cleared = Kind switch
            {
                StuckKind.Flipped => upDot > 0.7f,
                StuckKind.Water => waterDepth < 0.5f && !flooded && moving,
                _ => moving && ConvoyTrail.Flat(pos - StuckAt).magnitude > 4f,
            };
            if (!cleared) kind = Kind;
            else _crawlStuck = false;
        }
        Kind = kind;
    }

    public float StuckFor => IsStuck ? Time.time - StuckSince : 0f;

    public static string Describe(StuckKind k) => k switch
    {
        StuckKind.NoProgress => "stuck",
        StuckKind.Flipped => "on its roof/side",
        StuckKind.Water => "in deep water",
        _ => "ok",
    };
}

/// <summary>
/// An AI car's own escalation when it is stuck:
/// 0 back off and retry on a different line, 1 rock forward/back (low range, diff locks),
/// 2 ask the convoy for a tow, 3 last resort: put it back on safe ground nearby (host power).
/// </summary>
internal sealed class RecoveryLadder
{
    public int Level;
    public int Step;
    public float StepStarted;
    public float LevelStarted;
    public int Side = 1;          // which way to try the new line
    public bool TowRequested;
    public int Rescues;           // repositions so far (shown in the status)

    public void Start(float lastSteer)
    {
        Level = 0;
        Step = 0;
        StepStarted = LevelStarted = Time.time;
        Side = lastSteer > 0.1f ? -1 : 1;
        TowRequested = false;
    }

    public void NextLevel()
    {
        Level++;
        Step = 0;
        StepStarted = LevelStarted = Time.time;
        Side = -Side;
    }

    public void NextStep()
    {
        Step++;
        StepStarted = Time.time;
    }

    public float InStep => Time.time - StepStarted;
    public float InLevel => Time.time - LevelStarted;

    public string Describe() => Level switch
    {
        0 => "backing off, trying another line",
        1 => "rocking free (low range, diffs locked)",
        2 => "waiting for a tow",
        _ => "being put back on its wheels",
    };
}
