using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.Filter;
using OverTheHillPlus.Multiplayer;
using UnityEngine;
using UnityEngine.InputSystem;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Rules;

/// <summary>
/// What this player sees and does: the rules window the host sent, the running vote (for the host too), and the
/// guest's requests to the host (start a vote, vote, show the rules again). Guests act only on the host's own
/// messages (the channel is host-only), and every received line passes this game's word filter again.
/// </summary>
internal static class RulesClient
{
    public static RulesPopup Rules { get; private set; }
    public static float RulesShownAt { get; private set; } = -1000f;

    public static VoteView Vote { get; private set; }
    private static float _voteAt;
    /// <summary>This player may vote in <see cref="Vote"/>, and how they voted (null = not yet).</summary>
    public static bool CanVote { get; private set; }
    public static bool? MyBallot { get; private set; }

    public static string Toast { get; private set; }
    public static float ToastAt { get; private set; } = -1000f;

    public static void Reset()
    {
        Rules = null;
        Vote = null;
        _hiddenVote = 0;
        Toast = null;
        MyBallot = null;
        CanVote = false;
    }

    // ---------------------------------------------------------------- from the host

    internal static void OnHostMessage(Net.Message msg)
    {
        if (Net.IsHost || !msg.FromHost) return;
        switch (msg.Type)
        {
            case RulesWire.MsgRules:
                var popup = RulesWire.DecodeRules(msg.Payload);
                if (popup != null) ShowRules(popup);
                break;
            case RulesWire.MsgVoteState:
                var v = RulesWire.DecodeVoteState(msg.Payload);
                if (v == null) return;
                v.TargetName = Safe(v.TargetName, "a player");
                v.StarterName = Safe(v.StarterName, "a player");
                int me = LocalRaw;
                bool canVote = v.Target != me;
                bool? ballot = Vote != null && Vote.Id == v.Id ? MyBallot : null;
                OnVoteState(v, canVote, ballot);
                break;
            case RulesWire.MsgNotice:
                string text = RulesWire.DecodeNotice(msg.Payload);
                if (!string.IsNullOrEmpty(text)) ShowToast(Safe(text, null));
                break;
        }
    }

    private static string Safe(string s, string fallback)
    {
        s = HostNotice.Escape(s ?? "").Trim();
        if (s.Length == 0 || NameFilter.IsBlocked(s, out _)) return fallback;
        return s;
    }

    internal static void ShowRules(RulesPopup popup)
    {
        var clean = new RulesPopup
        {
            Title = Safe(popup.Title, "Server rules"),
            Welcome = Safe(popup.Welcome, ""),
        };
        foreach (string r in popup.Rules)
        {
            string s = Safe(r, null);
            if (s != null) clean.Rules.Add(s);
        }
        if (clean.Rules.Count == 0 && clean.Welcome.Length == 0) return;
        Rules = clean;
        RulesShownAt = Time.unscaledTime;
    }

    internal static void HideRules() => RulesShownAt = -1000f;

    internal static void OnVoteState(VoteView v, bool canVote, bool? ballot)
    {
        if (Vote == null || Vote.Id != v.Id) MyBallot = null;
        Vote = v;
        _voteAt = Time.unscaledTime;
        CanVote = canVote;
        if (ballot != null) MyBallot = ballot;
    }

    internal static void ShowToast(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Toast = text;
        ToastAt = Time.unscaledTime;
    }

    /// <summary>Seconds left in the running vote, counted down here between the host's updates.</summary>
    public static float VoteSecondsLeft => Vote == null ? 0f : Mathf.Max(0f, Vote.SecondsLeft - (Time.unscaledTime - _voteAt));

    /// <summary>A vote still running or in its grace before the kick.</summary>
    public static bool VoteLive => Vote != null && (Vote.Outcome == VoteOutcome.Open || Vote.Outcome == VoteOutcome.Passing) &&
                                   Time.unscaledTime - _voteAt < Vote.SecondsLeft + 5f;

    /// <summary>The vote to show on screen: a live one, or a finished one for a few seconds; not one the player hid.</summary>
    public static bool VoteVisible =>
        Vote != null && Vote.Id != _hiddenVote && (VoteLive || (!IsLive(Vote.Outcome) && Time.unscaledTime - _voteAt < 6f));

    private static int _hiddenVote;

    private static bool IsLive(VoteOutcome o) => o == VoteOutcome.Open || o == VoteOutcome.Passing;

    /// <summary>Hides the vote card for this vote (the MODS page still shows it and still lets you vote).</summary>
    public static void HideVoteCard()
    {
        if (Vote != null) _hiddenVote = Vote.Id;
    }

    // ---------------------------------------------------------------- actions

    private static int LocalRaw
    {
        get
        {
            try { return GameState.Runner?.LocalPlayer.RawEncoded ?? 0; } catch (System.Exception ex) { DebugLog.Error("rules", ex, "RulesClient.LocalRaw"); return 0; }
        }
    }

    /// <summary>Starts a vote to remove <paramref name="target"/>. Returns the menu status.</summary>
    public static string StartVote(PlayerRef target)
    {
        if (RulesHost.Hosting)
        {
            string why = RulesHost.StartVote(RulesHost.LocalRaw, target.RawEncoded, fromGuest: false);
            return why == null ? "Vote started." : "Blocked: " + why + ".";
        }
        if (!Net.IsGuest) return "Blocked: join or host a multiplayer session first.";
        // Starting a vote affects another player: not from a public lobby (the host can still start one there).
        if (GameState.IsPublicGuest) return "Blocked: in public lobbies only the host can start a vote.";
        if (!Net.HostHas(RulesWire.Channel)) return "Blocked: the host doesn't run Server Rules.";
        var r = Net.SendToHost(RulesWire.Channel, RulesWire.MsgStartVote, RulesWire.EncodeStartVote(target.RawEncoded));
        return r == Net.SendResult.Sent ? "Asked the host to start the vote." : "Failed: " + r + ".";
    }

    public static string Cast(bool yes)
    {
        var v = Vote;
        if (v == null || v.Outcome != VoteOutcome.Open || !CanVote) return "Blocked: no vote to vote in.";
        if (RulesHost.Hosting)
        {
            string why = RulesHost.CastAsHost(yes);
            if (why != null) return "Blocked: " + why + ".";
            MyBallot = yes;
            return yes ? "You voted yes." : "You voted no.";
        }
        var r = Net.SendToHost(RulesWire.Channel, RulesWire.MsgCast, RulesWire.EncodeCast(v.Id, yes));
        if (r != Net.SendResult.Sent) return "Failed: " + r + ".";
        MyBallot = yes;
        return yes ? "You voted yes." : "You voted no.";
    }

    public static string AskForRules()
    {
        if (RulesHost.Hosting)
        {
            ShowRules(RulesHost.Popup("you", true));
            return "Showing your rules (as a guest with the mod sees them).";
        }
        if (!Net.IsGuest || !Net.HostHas(RulesWire.Channel)) return "Blocked: the host doesn't run Server Rules.";
        var r = Net.SendToHost(RulesWire.Channel, RulesWire.MsgAskRules, System.Array.Empty<byte>());
        return r == Net.SendResult.Sent ? "Asked the host for the rules." : "Failed: " + r + ".";
    }

    internal static void Hotkeys(Keyboard kb)
    {
        if (!VoteVisible || !VoteLive) return;
        bool yesKey = CoreBehaviour.Pressed(kb, RulesConfig.VoteYesKey.Value);
        bool noKey = !yesKey && CoreBehaviour.Pressed(kb, RulesConfig.VoteNoKey.Value);
        if (!yesKey && !noKey) return;
        // Pressing the key of the vote you already gave (or any vote key once you can't vote) hides the card.
        if (Vote.Outcome != VoteOutcome.Open || !CanVote || MyBallot == yesKey) { HideVoteCard(); return; }
        ShowToast(Cast(yesKey));
    }

    /// <summary>Players this player can start a vote against: everyone in the session but you and the host.</summary>
    public static List<(PlayerRef player, string name)> Targets()
    {
        var list = new List<(PlayerRef, string)>();
        var sp = OverlandSessionPlayers.Instance;
        int me = LocalRaw, host = 0;
        try { host = sp?.HostPlayerRef.RawEncoded ?? 0; } catch (System.Exception ex) { DebugLog.Error("rules", ex, "RulesClient"); }
        foreach (var p in Features.Public.PlayerList.PlayerListRoster.AllPlayers())
        {
            if (p.RawEncoded == me || p.RawEncoded == host) continue;
            list.Add((p, HostNotice.SafeName(RulesHost.NameOf(p))));
        }
        return list;
    }
}
