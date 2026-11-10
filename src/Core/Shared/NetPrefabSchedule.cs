using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Core.Shared;

/// <summary>
/// When runtime network prefab registration may start: once the game's start-up has settled (the main menu is up and
/// enough frames in a row were short), or after a time limit whatever the frames look like. Registration started on the
/// first frame used to run while the game was still booting and froze that frame for half a minute; and short frames alone
/// come long before the menu does, so the cars' builds ran into the game's own boot and held the menu back. Pure code
/// (unit-tested).
/// </summary>
internal sealed class StartGate
{
    /// <summary>A frame shorter than this counts as a quiet one (seconds).</summary>
    public const float QuietFrame = 0.1f;
    /// <summary>Quiet frames in a row that open the gate.</summary>
    public const int QuietFramesNeeded = 30;
    /// <summary>Seconds after the first frame when the gate opens anyway.</summary>
    public const float MaxWait = 30f;

    private float _first = -1f;
    private int _quiet;

    public bool Open { get; private set; }
    /// <summary>Why it opened ("quiet" or "time"), null while closed.</summary>
    public string OpenedBy { get; private set; }
    /// <summary>Seconds from the first frame to the opening.</summary>
    public float WaitedSeconds { get; private set; }

    /// <summary>One frame: <paramref name="frameSeconds"/> is its length, <paramref name="now"/> the real time,
    /// <paramref name="menuUp"/> says the game's main menu is on screen (frames before it don't count).
    /// True once open (it stays open).</summary>
    public bool Update(float frameSeconds, float now, bool menuUp = true)
    {
        if (Open) return true;
        if (_first < 0f) _first = now;
        _quiet = menuUp && frameSeconds >= 0f && frameSeconds < QuietFrame ? _quiet + 1 : 0;
        if (_quiet >= QuietFramesNeeded) OpenNow("quiet", now);
        else if (now - _first >= MaxWait) OpenNow("time", now);
        return Open;
    }

    /// <summary>Opens it now (a session is starting: everything must register first).</summary>
    public void Force(float now) { if (!Open) OpenNow("session", now); }

    private void OpenNow(string by, float now)
    {
        Open = true;
        OpenedBy = by;
        WaitedSeconds = _first < 0f ? 0f : Math.Max(0f, now - _first);
    }
}

/// <summary>What one step of a <see cref="StagedBuild"/> said.</summary>
internal enum StepKind
{
    /// <summary>Done: on to the next step (in the same frame while the frame's budget lasts).</summary>
    Next,
    /// <summary>Not done yet (an asset is loading in the background): this step again next frame.</summary>
    Wait,
    /// <summary>Not possible yet (the game is still loading): asked again later, from this step.</summary>
    Later,
    /// <summary>Never: the build is given up.</summary>
    Fail,
}

internal readonly struct StepResult
{
    public readonly StepKind Kind;
    /// <summary>What it waits for (<see cref="StepKind.Later"/>) or why it failed (<see cref="StepKind.Fail"/>).</summary>
    public readonly string Text;

    private StepResult(StepKind kind, string text) { Kind = kind; Text = text; }

    public static StepResult Next => new(StepKind.Next, null);
    public static StepResult Wait => new(StepKind.Wait, null);
    public static StepResult Later(string waitingFor) => new(StepKind.Later, waitingFor ?? "the game");
    public static StepResult Fail(string why) => new(StepKind.Fail, why ?? "failed");
}

/// <summary>What <see cref="StagedBuild.Advance"/> did this frame.</summary>
internal enum BuildProgress
{
    /// <summary>Every step is done.</summary>
    Done,
    /// <summary>Some steps are left (the frame's budget ran out, or a step waits for a load): again next frame.</summary>
    Working,
    /// <summary>A step can't run yet (<see cref="StepResult.Later"/>): again later, from that step.</summary>
    Later,
    /// <summary>A step failed: the build is over.</summary>
    Failed,
}

/// <summary>
/// A long main-thread job cut into named steps, so it can be spread over several frames: each call runs steps while the
/// frame's budget lasts (always at least one), and a step that waits for a background load is asked again next frame.
/// In <i>urgent</i> mode (a session is starting) every step is told to finish right away and the whole job runs in one
/// call. Steps run strictly in order, each once after it said <see cref="StepKind.Next"/>. Pure code (unit-tested).
/// </summary>
internal sealed class StagedBuild
{
    private readonly List<(string name, Func<bool, StepResult> run)> _steps = new();

    /// <summary>The step that runs next (the step count once done).</summary>
    public int Position { get; private set; }
    /// <summary>Its name (null once done).</summary>
    public string StepName => Position < _steps.Count ? _steps[Position].name : null;
    public int Count => _steps.Count;
    public bool IsDone => Position >= _steps.Count;
    public bool HasFailed { get; private set; }
    /// <summary>Why it waits (<see cref="BuildProgress.Later"/>) or failed (<see cref="BuildProgress.Failed"/>).</summary>
    public string Text { get; private set; }
    /// <summary>Calls of <see cref="Advance"/> so far (each is one frame of the registry).</summary>
    public int Frames { get; private set; }
    /// <summary>The step the last call ran last (the one that said wait, later or failed, or the last one done).</summary>
    public string LastRan { get; private set; }
    /// <summary>Time each step took in all (milliseconds, summed over its frames), in step order; only with a clock.</summary>
    public double[] StepMs => _ms.ToArray();
    /// <summary>Frames each step was asked in (a waiting step is asked once a frame).</summary>
    public int[] StepCalls => _calls.ToArray();

    private readonly List<double> _ms = new();
    private readonly List<int> _calls = new();
    private readonly Func<double> _clockMs;

    /// <param name="clockMs">A clock in milliseconds for <see cref="StepMs"/> (null: no timings).</param>
    public StagedBuild(Func<double> clockMs = null) { _clockMs = clockMs; }

    /// <summary>"name 12 ms, name 3 frames 40 ms, ...": what each step cost, for the log.</summary>
    public string Summary()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _steps.Count; i++)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(_steps[i].name).Append(' ');
            if (_calls[i] > 1) sb.Append(_calls[i]).Append(" frames ");
            sb.Append(_ms[i].ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append(" ms");
        }
        return sb.ToString();
    }

    /// <summary>Adds a step; <c>run(urgent)</c> must finish its work at once when <c>urgent</c> is true.</summary>
    public StagedBuild Step(string name, Func<bool, StepResult> run)
    {
        if (run == null) throw new ArgumentNullException(nameof(run));
        _steps.Add((name ?? "step " + _steps.Count, run));
        _ms.Add(0);
        _calls.Add(0);
        return this;
    }

    /// <summary>
    /// Runs steps from <see cref="Position"/>. Without <paramref name="urgent"/>, a step after the first runs only while
    /// <paramref name="budgetLeft"/> says yes; with it, every step runs, and a step that still says
    /// <see cref="StepKind.Wait"/> is asked again up to <paramref name="urgentRetries"/> times before the call gives up
    /// with <see cref="BuildProgress.Working"/>.
    /// </summary>
    public BuildProgress Advance(bool urgent, Func<bool> budgetLeft, int urgentRetries = 3)
    {
        if (HasFailed) return BuildProgress.Failed;
        Frames++;
        bool first = true;
        int retries = 0;
        while (Position < _steps.Count)
        {
            if (!first && !urgent && budgetLeft != null && !budgetLeft()) return BuildProgress.Working;
            first = false;
            int at = Position;
            LastRan = _steps[at].name;
            double t0 = _clockMs?.Invoke() ?? 0;
            StepResult r;
            try { r = _steps[at].run(urgent); }
            finally
            {
                if (_clockMs != null) _ms[at] += Math.Max(0, _clockMs() - t0);
                _calls[at]++;
            }
            switch (r.Kind)
            {
                case StepKind.Next:
                    Position++;
                    retries = 0;
                    Text = null;
                    continue;
                case StepKind.Wait:
                    if (urgent && ++retries <= urgentRetries) continue;
                    return BuildProgress.Working;
                case StepKind.Later:
                    Text = r.Text;
                    return BuildProgress.Later;
                default:
                    HasFailed = true;
                    Text = r.Text;
                    return BuildProgress.Failed;
            }
        }
        return BuildProgress.Done;
    }
}

/// <summary>What one registration attempt of <see cref="NetPrefabRegistry"/> came to.</summary>
internal enum RegisterStep
{
    /// <summary>Nothing pending (or the registry is off).</summary>
    Idle,
    /// <summary>An entry went into Fusion's table.</summary>
    Registered,
    /// <summary>The current entry made progress and has steps left.</summary>
    Working,
    /// <summary>An entry was given up (the next one goes next).</summary>
    GaveUp,
    /// <summary>The current entry can't be built yet (the game is still loading): later entries wait for it.</summary>
    Waiting,
    /// <summary>The registry broke (the table changed under it).</summary>
    Broken,
}

internal static class NetPrefabSchedule
{
    /// <summary>Main-thread time per frame for a modded car's build steps after the first (milliseconds).</summary>
    public const double FrameBudgetMs = 8.0;

    /// <summary>
    /// Registers everything that can be, right now (a session is starting: its set must be complete before it can
    /// spawn anything): attempts follow each other until nothing is pending, an entry has to wait for the game, the
    /// registry broke, or <paramref name="maxAttempts"/> ran out (a step that keeps saying "wait"). Returns the
    /// attempts made and the last result.
    /// </summary>
    public static (int attempts, RegisterStep last) Flush(Func<RegisterStep> attempt, int maxAttempts)
    {
        var last = RegisterStep.Idle;
        int n = 0;
        while (n < maxAttempts)
        {
            last = attempt();
            n++;
            if (last is RegisterStep.Idle or RegisterStep.Waiting or RegisterStep.Broken) break;
        }
        return (n, last);
    }

    /// <summary>Attempts a flush may take: every entry through every step, with room for retries.</summary>
    public static int FlushAttempts(int entries, int stepsPerEntry = 16) => Math.Max(1, entries) * Math.Max(1, stepsPerEntry) * 2;
}
