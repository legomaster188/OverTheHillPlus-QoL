using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Rules;

/// <summary>A vote's state. Values are on the wire: append new ones, never renumber.</summary>
internal enum VoteOutcome : byte
{
    Open = 0,
    /// <summary>Removed (after the grace below).</summary>
    Passed = 1,
    Failed = 2,
    Vetoed = 3,
    Cancelled = 4,
    /// <summary>The majority voted yes; the kick waits <see cref="VoteBook.PassGrace"/> so the host can still veto.</summary>
    Passing = 5,
}

/// <summary>
/// Vote kick bookkeeping on the host (pure; the tests compile this file on its own). Players are their PlayerRef raw
/// codes and time is Time.unscaledTime, so nothing here touches the game.
///
/// <para>Rules:</para>
/// <list type="bullet">
/// <item>Voters ("eligible") are the players who run Server Rules (and the host, when the host votes), minus the
///   target. The starter must be one of them and counts as a yes.</item>
/// <item>A vote passes with a strict majority of the voters, and never with fewer than <see cref="MinYes"/> yes votes.
///   It fails as soon as it can no longer pass, or when <see cref="Duration"/> runs out.</item>
/// <item>The host can never be the target; nobody can target themselves; one vote at a time. In a public lobby
///   only the host starts votes (<see cref="GuestStartRefusal"/>; guests may still vote).</item>
/// <item>A vote that reaches its majority is <see cref="VoteOutcome.Passing"/> for <see cref="PassGrace"/> seconds
///   (ballots are closed, the host can still veto), then <see cref="VoteOutcome.Passed"/>.</item>
/// <item>Cooldowns: a starter waits <see cref="StarterCooldown"/> between votes they start; after a failed or vetoed
///   vote its target is safe for <see cref="TargetCooldown"/>; at most <see cref="MaxKicksPer10Min"/> votes may pass in
///   ten minutes. A target who leaves cancels the vote (no cooldown).</item>
/// </list>
/// </summary>
internal sealed class VoteBook
{
    public float Duration = 60f, StarterCooldown = 120f, TargetCooldown = 300f, PassGrace = 8f;
    public int MinYes = 2, MaxKicksPer10Min = 3;

    internal sealed class Vote
    {
        public int Id;
        public int Target, Starter;
        public float StartedAt, EndsAt;
        /// <summary>When a <see cref="VoteOutcome.Passing"/> vote becomes <see cref="VoteOutcome.Passed"/>.</summary>
        public float KickAt;
        public readonly HashSet<int> Eligible = new();
        public readonly Dictionary<int, bool> Ballots = new();
        public VoteOutcome Outcome = VoteOutcome.Open;
        internal VoteBook Book;

        public int Yes { get { int n = 0; foreach (var b in Ballots.Values) if (b) n++; return n; } }
        public int No => Ballots.Count - Yes;
        public int NeededNow => Book.Needed(Eligible.Count);
        public float SecondsLeft(float now) => Math.Max(0f, (Outcome == VoteOutcome.Passing ? KickAt : EndsAt) - now);
        /// <summary>Still running or in its grace: the host may veto, the target leaving cancels it.</summary>
        public bool Live => Outcome == VoteOutcome.Open || Outcome == VoteOutcome.Passing;
    }

    public Vote Current { get; private set; }

    private int _nextId = 1;
    private float _lastNow;
    private readonly Dictionary<int, float> _starterUntil = new();
    private readonly Dictionary<int, float> _targetUntil = new();
    private readonly Queue<float> _kicks = new();

    /// <summary>
    /// Host side: why a guest's request to start a vote is refused (P10: in a public lobby, actions that affect other
    /// players are not open to guests), or null when allowed. <paramref name="privateSession"/> = the session is private.
    /// </summary>
    public static string GuestStartRefusal(bool privateSession) =>
        privateSession ? null : "in public lobbies only the host can start a vote";

    /// <summary>Yes votes needed out of <paramref name="eligible"/> voters.</summary>
    public int Needed(int eligible) => Math.Max(MinYes, eligible / 2 + 1);

    /// <summary>Starts a vote; null when it started, otherwise why not.</summary>
    public string TryStart(int starter, int target, int host, IEnumerable<int> voters, float now)
    {
        _lastNow = now;
        if (Current != null && Current.Live) return "a vote is already running";
        if (target == host) return "the host can't be voted out";
        if (target == starter) return "you can't vote yourself out";
        var eligible = new HashSet<int>();
        foreach (int v in voters) if (v != target) eligible.Add(v);
        if (!eligible.Contains(starter)) return "only players with Server Rules can vote";
        if (eligible.Count < MinYes) return $"at least {MinYes} players with Server Rules are needed to vote";
        if (_starterUntil.TryGetValue(starter, out float s) && now < s) return $"you can start another vote in {Math.Ceiling(s - now)} s";
        if (_targetUntil.TryGetValue(target, out float t) && now < t) return $"that player can't be voted on again for {Math.Ceiling(t - now)} s";
        while (_kicks.Count > 0 && now - _kicks.Peek() > 600f) _kicks.Dequeue();
        if (_kicks.Count >= MaxKicksPer10Min) return "too many players were voted out in the last 10 minutes";

        Current = new Vote { Id = _nextId++, Target = target, Starter = starter, StartedAt = now, EndsAt = now + Duration, Book = this };
        foreach (int v in eligible) Current.Eligible.Add(v);
        Current.Ballots[starter] = true;
        _starterUntil[starter] = now + StarterCooldown;
        return null;
    }

    /// <summary>A ballot (a voter may change it while the vote runs); null when counted, otherwise why not.</summary>
    public string Cast(int voter, bool yes, float now)
    {
        _lastNow = now;
        var v = Current;
        if (v == null || !v.Live) return "no vote is running";
        if (v.Outcome == VoteOutcome.Passing) return "the vote is already decided";
        if (voter == v.Target) return "you can't vote on yourself";
        if (!v.Eligible.Contains(voter)) return "only players with Server Rules can vote";
        v.Ballots[voter] = yes;
        return null;
    }

    /// <summary>Settles the vote when its result is known (or time ran out); returns its outcome.</summary>
    public VoteOutcome Evaluate(float now)
    {
        _lastNow = now;
        var v = Current;
        if (v == null) return VoteOutcome.Cancelled;
        if (v.Outcome == VoteOutcome.Passing)
        {
            if (now >= v.KickAt) v.Outcome = VoteOutcome.Passed;
            return v.Outcome;
        }
        if (v.Outcome != VoteOutcome.Open) return v.Outcome;
        int needed = v.NeededNow, yes = v.Yes;
        int undecided = 0;
        foreach (int e in v.Eligible) if (!v.Ballots.ContainsKey(e)) undecided++;
        if (yes >= needed)
        {
            v.Outcome = PassGrace > 0f ? VoteOutcome.Passing : VoteOutcome.Passed;
            v.KickAt = now + PassGrace;
        }
        else if (yes + undecided < needed || now >= v.EndsAt) Finish(v, VoteOutcome.Failed, now);
        return v.Outcome;
    }

    /// <summary>A voter left the session: they no longer count, and neither does their ballot.</summary>
    public void VoterLeft(int player, float now)
    {
        _lastNow = now;
        var v = Current;
        if (v == null || v.Outcome != VoteOutcome.Open) return;
        v.Eligible.Remove(player);
        v.Ballots.Remove(player);
    }

    /// <summary>The target left on their own: the vote is called off.</summary>
    public void TargetLeft()
    {
        if (Current != null && Current.Live) Current.Outcome = VoteOutcome.Cancelled;
    }

    /// <summary>The host stopped the vote (also during its grace); the target is safe for <see cref="TargetCooldown"/>.</summary>
    public void Veto()
    {
        if (Current != null && Current.Live) Finish(Current, VoteOutcome.Vetoed, _lastNow);
    }

    /// <summary>A passed vote's kick went through (counts toward <see cref="MaxKicksPer10Min"/>).</summary>
    public void NoteKick(float now) => _kicks.Enqueue(now);

    public void Clear() => Current = null;

    /// <summary>New session: no vote, no cooldowns.</summary>
    public void Reset()
    {
        Current = null;
        _starterUntil.Clear();
        _targetUntil.Clear();
        _kicks.Clear();
    }

    private void Finish(Vote v, VoteOutcome outcome, float now)
    {
        v.Outcome = outcome;
        _targetUntil[v.Target] = now + TargetCooldown;
    }
}

/// <summary>
/// Rules-as-notices for players without the mod reach everyone in the session, so they go out at most once per
/// <see cref="Window"/>; joins in between are merged into one broadcast at the end of the window (pure).
/// </summary>
internal sealed class NoticeWindow
{
    public float Window = 180f;
    private float _lastSent = float.NegativeInfinity;
    private bool _pending;

    /// <summary>A player without the mod joined: true = broadcast now; false = merged into a later one.</summary>
    public bool Request(float now)
    {
        if (now - _lastSent >= Window)
        {
            _lastSent = now;
            _pending = false;
            return true;
        }
        _pending = true;
        return false;
    }

    /// <summary>Polled: true once when a merged broadcast is due.</summary>
    public bool Due(float now)
    {
        if (!_pending || now - _lastSent < Window) return false;
        _pending = false;
        _lastSent = now;
        return true;
    }

    public bool Pending => _pending;

    public void Reset()
    {
        _lastSent = float.NegativeInfinity;
        _pending = false;
    }
}
