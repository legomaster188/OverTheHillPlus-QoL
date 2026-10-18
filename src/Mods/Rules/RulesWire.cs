using System.Collections.Generic;
using OverTheHillPlus.Features.Public.ModNet;

namespace OverTheHillPlus.Rules;

/// <summary>The rules pop-up a host sends a player with Server Rules who just joined.</summary>
internal sealed class RulesPopup
{
    public string Title = "";
    public string Welcome = "";
    public List<string> Rules = new();
}

/// <summary>A vote as the host shows it to the voters.</summary>
internal sealed class VoteView
{
    public int Id;
    public int Target;
    public string TargetName = "", StarterName = "";
    public int Yes, No, Needed, Eligible;
    public float SecondsLeft;
    public VoteOutcome Outcome;
}

/// <summary>
/// Server Rules' messages on the mod network (pure; compiled by the tests with core's NetWriter / NetReader).
/// Channel <see cref="Channel"/>, version 1; new fields are only ever appended.
/// <list type="bullet">
/// <item><see cref="MsgRules"/> host -> guest: [string title][string welcome][byte n][string rule]*n</item>
/// <item><see cref="MsgStartVote"/> guest -> host: [int target PlayerRef raw]</item>
/// <item><see cref="MsgCast"/> guest -> host: [int vote id][bool yes]</item>
/// <item><see cref="MsgVoteState"/> host -> guests: [int id][int target][string target name][string starter name]
///   [u16 yes][u16 no][u16 needed][u16 eligible][float seconds left][byte outcome]</item>
/// <item><see cref="MsgAskRules"/> guest -> host: (empty) "show me the rules again"</item>
/// <item><see cref="MsgNotice"/> host -> guest: [string text] (e.g. why a vote could not start)</item>
/// </list>
/// Guests only act on the host's own messages (never relayed), and every text the host sends passed its word filter.
/// </summary>
internal static class RulesWire
{
    /// <summary>The mod-network channel (core's table: ModNet.Channels.Rules; this file is pure, so the number is
    /// repeated here and RulesFeature checks the two agree).</summary>
    public const ushort Channel = 11;
    public const byte Version = 1;
    public const byte MsgRules = 1, MsgStartVote = 2, MsgCast = 3, MsgVoteState = 4, MsgAskRules = 5, MsgNotice = 6;
    public const int MaxRules = 8, MaxRuleChars = 120, MaxTitleChars = 60, MaxWelcomeChars = 160, MaxNameChars = 40, MaxNoticeChars = 160;
    /// <summary>Largest payload: 8 rules of 120 characters at up to 3 UTF-8 bytes, plus the title and welcome.</summary>
    public const int MaxPayload = 4096;

    public static byte[] EncodeRules(RulesPopup p)
    {
        var w = new NetWriter(256).String(p?.Title ?? "", MaxTitleChars).String(p?.Welcome ?? "", MaxWelcomeChars);
        var rules = p?.Rules ?? new List<string>();
        int n = System.Math.Min(rules.Count, MaxRules);
        w.Byte((byte)n);
        for (int i = 0; i < n; i++) w.String(rules[i] ?? "", MaxRuleChars);
        return w.ToArray();
    }

    public static RulesPopup DecodeRules(byte[] bytes)
    {
        var r = new NetReader(bytes);
        var p = new RulesPopup { Title = r.String(MaxTitleChars), Welcome = r.String(MaxWelcomeChars) };
        int n = r.Byte();
        if (!r.Ok || n > MaxRules) return null;
        for (int i = 0; i < n; i++) p.Rules.Add(r.String(MaxRuleChars));
        return r.Ok ? p : null;
    }

    public static byte[] EncodeStartVote(int target) => new NetWriter(4).Int(target).ToArray();

    public static bool DecodeStartVote(byte[] bytes, out int target)
    {
        var r = new NetReader(bytes);
        target = r.Int();
        return r.Ok;
    }

    public static byte[] EncodeCast(int voteId, bool yes) => new NetWriter(5).Int(voteId).Bool(yes).ToArray();

    public static bool DecodeCast(byte[] bytes, out int voteId, out bool yes)
    {
        var r = new NetReader(bytes);
        voteId = r.Int();
        yes = r.Bool();
        return r.Ok;
    }

    public static byte[] EncodeVoteState(VoteView v) => new NetWriter(96)
        .Int(v.Id).Int(v.Target).String(v.TargetName ?? "", MaxNameChars).String(v.StarterName ?? "", MaxNameChars)
        .UShort(Clamp(v.Yes)).UShort(Clamp(v.No)).UShort(Clamp(v.Needed)).UShort(Clamp(v.Eligible))
        .Float(v.SecondsLeft).Byte((byte)v.Outcome).ToArray();

    public static VoteView DecodeVoteState(byte[] bytes)
    {
        var r = new NetReader(bytes);
        var v = new VoteView
        {
            Id = r.Int(), Target = r.Int(), TargetName = r.String(MaxNameChars), StarterName = r.String(MaxNameChars),
            Yes = r.UShort(), No = r.UShort(), Needed = r.UShort(), Eligible = r.UShort(), SecondsLeft = r.Float(),
        };
        byte outcome = r.Byte();
        if (!r.Ok || outcome > (byte)VoteOutcome.Passing) return null;
        v.Outcome = (VoteOutcome)outcome;
        return v;
    }

    public static byte[] EncodeNotice(string text) => new NetWriter(64).String(text ?? "", MaxNoticeChars).ToArray();

    public static string DecodeNotice(byte[] bytes)
    {
        var r = new NetReader(bytes);
        string s = r.String(MaxNoticeChars);
        return r.Ok ? s : null;
    }

    private static ushort Clamp(int v) => (ushort)System.Math.Clamp(v, 0, ushort.MaxValue);
}
