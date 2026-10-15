using System;
using System.Collections.Generic;
using System.Text;
using Fusion;
using OverTheHillPlus.Features.Public.Filter;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Features.Public.Moderation;
using OverTheHillPlus.Features.Public.PlayerList;
using OverTheHillPlus.UI;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Features.Public.Chat;

/// <summary>
/// Text chat between players who run this mod, over the mod network's "chat" channel.
///
/// Wire: channel <see cref="Net.Channels.Chat"/>, type <see cref="MsgSay"/>, version 1, payload
/// [byte kind (0 = say)] [string text]. New fields are only ever appended. The sender's name is never sent: every
/// receiver looks it up from the player who sent the message (for a relayed message the host fills that in), so a
/// message can't claim to be from someone else.
///
/// Routing: a host broadcasts to every modded guest that has chat on; a guest asks the host to relay to everyone
/// (the host shows it too). Vanilla players are never sent anything (the mod network only talks to players that
/// announced it). Every message, outgoing and incoming, must pass the strict name filter: a message that fails is not
/// sent (outgoing) or not shown (incoming), never censored. Rate limits: locally (burst 4, then one every 2 s) and by
/// the mod network on every receiver (per sender).
/// </summary>
internal static class ChatNet
{
    public const int MaxChars = 120;
    public const int MaxHistory = 60;
    private const byte MsgSay = 1;
    private const byte KindSay = 0;

    internal enum LineKind { Player, Self, System }

    internal sealed class Line
    {
        public LineKind Kind;
        public string Name;
        public Color NameColor;
        public string Text;
        public float At;
        // layout cache (overlay)
        public float CacheWidth = -1f, CacheScale = -1f, NameWidth, Height;
    }

    internal static readonly List<Line> History = new();

    public static int SentCount { get; private set; }
    public static int ReceivedCount { get; private set; }
    public static int BlockedCount { get; private set; }
    public static int MutedCount { get; private set; }

    // Local send limit: burst 4, then one every 2 s; at least 0.6 s between messages; no repeats within 5 s.
    private const float RefillPerSecond = 0.5f, BurstTokens = 4f, MinGap = 0.6f, RepeatWindow = 5f;
    private static float _tokens = BurstTokens, _tokensAt, _lastSendAt = -100f;
    private static string _lastSent;

    private static readonly HashSet<string> LoggedOnce = new();

    internal static void RegisterChannel()
    {
        Net.Register(Net.Channels.Chat, new Net.ChannelOptions
        {
            Name = "chat",
            Version = 1,
            MaxPayload = 512,          // 120 characters of UTF-8 is at most 360 bytes + header
            RatePerSecond = 1f,
            Burst = 5,
            AllowRelay = true,         // guests talk to each other through the host
            HostOnlySender = false,
            // A host with its own chat off can still carry its guests' messages (they need the host to relay).
            Active = () => ChatFeature.Enabled.Value || (Net.IsHost && ChatFeature.RelayForGuests.Value),
        }, OnMessage);
    }

    // ---------------------------------------------------------------- status

    /// <summary>Can this game send a chat message right now? <paramref name="why"/> says why not (for the UI).</summary>
    public static bool CanSend(out string why)
    {
        why = null;
        if (!ChatFeature.Enabled.Value) { why = "chat is off (PLUS > Chat)"; return false; }
        if (Net.Enabled == null || !Net.Enabled.Value) { why = "turn on the mod network (PLUS > Network) to chat"; return false; }
        if (Net.IsHost)
        {
            if (!Net.HostAdvertised) { why = "this server started with the mod network off: host a new one to chat"; return false; }
            return true;
        }
        if (!Net.IsGuest) { why = "join or host a multiplayer session to chat"; return false; }
        switch (Net.HostStatus)
        {
            case Net.HostLink.Ready: break;
            case Net.HostLink.Asking: why = "still connecting to the host's mod network"; return false;
            case Net.HostLink.Paused: why = "the host switched its mod network off"; return false;
            default: why = "the host doesn't run this mod, so there is nobody to chat with"; return false;
        }
        if (!Net.HostHas(Net.Channels.Chat)) { why = "the host has chat off"; return false; }
        return true;
    }

    /// <summary>Host: modded guests that currently have chat on.</summary>
    public static int ReadersAsHost()
    {
        int n = 0;
        try
        {
            foreach (var p in Net.Peers)
                if (p.Channels.ContainsKey(Net.Channels.Chat)) n++;
        }
        catch { }
        return n;
    }

    /// <summary>One line for the menu.</summary>
    public static string Describe()
    {
        if (!ChatFeature.Enabled.Value)
            return Net.IsHost && ChatFeature.RelayForGuests.Value && Net.Enabled != null && Net.Enabled.Value
                ? "off (still carrying your guests' messages)" : "off";
        if (!CanSend(out string why)) return why;
        if (Net.IsHost)
        {
            int n = ReadersAsHost();
            return n == 0 ? "hosting: no guest with this mod has chat on yet" : $"hosting: {n} guest(s) can read chat";
        }
        return "guest: the host carries chat (everyone with this mod and chat on can read it)";
    }

    // ---------------------------------------------------------------- sending

    /// <summary>Sends a message. Returns null when sent, else a reason starting with "Blocked:" or "Failed:".</summary>
    public static string Send(string raw)
    {
        string text = Clean(raw, MaxChars);
        if (text.Length == 0) return "Blocked: nothing to send";
        if (!CanSend(out string why)) return "Blocked: " + why;
        if (NameFilter.IsBlockedStrict(text, out string reason))
        {
            BlockedCount++;
            return "Blocked: not sent, " + (reason ?? "it did not pass the word filter");
        }

        float now = Time.unscaledTime;
        if (_lastSent != null && now - _lastSendAt < RepeatWindow && string.Equals(_lastSent, text, StringComparison.OrdinalIgnoreCase))
            return "Blocked: you just said that";
        if (now - _lastSendAt < MinGap) return "Blocked: slow down a little";
        _tokens = Mathf.Min(BurstTokens, _tokens + (now - _tokensAt) * RefillPerSecond);
        _tokensAt = now;
        if (_tokens < 1f) return $"Blocked: too many messages, wait {Mathf.CeilToInt((1f - _tokens) / RefillPerSecond)} s";

        byte[] payload = new NetWriter(16 + text.Length * 3).Byte(KindSay).String(text, MaxChars).ToArray();
        bool host = Net.IsHost;
        int n;
        try { n = Net.Broadcast(Net.Channels.Chat, MsgSay, payload); }
        catch (Exception e)
        {
            LogOnce("send", "[Chat] send failed: " + e.Message);
            return "Failed: could not send (see the log)";
        }
        // Host: n = guests reached (0 is fine, nobody else is listening). Guest: 1 = handed to the host.
        if (!host && n == 0) return "Failed: could not reach the host (too fast, or the connection dropped)";

        _tokens -= 1f;
        _lastSendAt = now;
        _lastSent = text;
        SentCount++;
        AddLine(LineKind.Self, LocalName(), LocalColor(), text);
        if (host && n == 0 && ReadersAsHost() == 0) AddSystem("Nobody else here runs this mod with chat on, so nobody can read that yet.");
        return null;
    }

    // ---------------------------------------------------------------- receiving

    private static void OnMessage(Net.Message m)
    {
        if (m.Type != MsgSay) return;
        // Host: a guest who writes is not away (the AFK watch can't see typing). m.From is the guest who sent it.
        if (Net.IsHost && m.From.IsRealPlayer) AfkWatch.MarkActive(m.From);
        if (!ChatFeature.Enabled.Value) return; // a host relaying with chat off: ModNet already forwarded it
        var rd = new NetReader(m.Payload);
        byte kind = rd.Byte();
        string text = rd.String(MaxChars);
        if (!rd.Ok || kind != KindSay) return; // a newer kind this version doesn't know: skip quietly
        text = Clean(text, MaxChars);
        if (text.Length == 0) return;

        PlayerRef from = m.From;
        var sp = SafeSessionPlayers();
        if (m.FromHost && !m.Relayed)
        {
            // From the host itself: the transport's sender may not be the host's player ref on a guest.
            try { if (sp != null && sp.HostPlayerRef.IsRealPlayer) from = sp.HostPlayerRef; } catch { }
        }
        try
        {
            var r = GameState.Runner;
            if (r != null && from.IsRealPlayer && r.LocalPlayer.RawEncoded == from.RawEncoded) return; // our own echo
        }
        catch { }

        ReceivedCount++;
        if (NameFilter.IsBlockedStrict(text, out _)) { BlockedCount++; return; }
        string name = NameOf(sp, from);
        if (IsMuted(name)) { MutedCount++; return; }
        AddLine(LineKind.Player, name, ColorOf(from), text);
    }

    // ---------------------------------------------------------------- history

    internal static void AddSystem(string text) => AddLine(LineKind.System, null, Theme.Warn, Clean(text, 200));

    private static void AddLine(LineKind kind, string name, Color color, string text)
    {
        History.Add(new Line { Kind = kind, Name = name, NameColor = color, Text = text, At = Time.unscaledTime });
        if (History.Count > MaxHistory) History.RemoveRange(0, History.Count - MaxHistory);
    }

    internal static void ClearHistory() => History.Clear();

    // ---------------------------------------------------------------- mute list (by name, saved)

    private static HashSet<string> _muted;
    private static string _mutedFrom;

    private static HashSet<string> Muted
    {
        get
        {
            string v = ChatFeature.MutedNames.Value ?? "";
            if (_muted == null || !ReferenceEquals(v, _mutedFrom))
            {
                _mutedFrom = v;
                _muted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in v.Split('|'))
                {
                    string n = part.Trim();
                    if (n.Length > 0) _muted.Add(n);
                }
            }
            return _muted;
        }
    }

    private static string MuteKey(string name) => (name ?? "").Replace("|", "").Trim();

    public static bool IsMuted(string name)
    {
        string k = MuteKey(name);
        return k.Length > 0 && Muted.Contains(k);
    }

    public static IEnumerable<string> MutedNames => Muted;
    public static int MutedNamesCount => Muted.Count;

    public static void SetMuted(string name, bool mute)
    {
        string k = MuteKey(name);
        if (k.Length == 0) return;
        var set = new HashSet<string>(Muted, StringComparer.OrdinalIgnoreCase);
        if (mute) set.Add(k); else set.Remove(k);
        ChatFeature.MutedNames.Value = string.Join("|", set);
    }

    public static void UnmuteAll() => ChatFeature.MutedNames.Value = "";

    // ---------------------------------------------------------------- players

    internal static OverlandSessionPlayers SafeSessionPlayers()
    {
        try { return OverlandSessionPlayers.Instance; } catch { return null; }
    }

    /// <summary>The player's name as this game knows it; a name that fails the filter shows as "Player N".</summary>
    internal static string NameOf(OverlandSessionPlayers sp, PlayerRef p)
    {
        string n;
        try { n = PlayerListRoster.NameOf(sp, p); } catch { n = null; }
        n = Clean(n, 24);
        if (n.Length == 0 || NameFilter.IsBlockedStrict(n, out _))
        {
            int id;
            try { id = p.PlayerId; } catch { id = 0; }
            n = "Player " + id;
        }
        return n;
    }

    internal static string LocalName()
    {
        try
        {
            var r = GameState.Runner;
            if (r != null) return NameOf(SafeSessionPlayers(), r.LocalPlayer);
        }
        catch { }
        return "You";
    }

    private static Color LocalColor()
    {
        try
        {
            var r = GameState.Runner;
            if (r != null) return ColorOf(r.LocalPlayer);
        }
        catch { }
        return Theme.Accent;
    }

    /// <summary>The player's colour (the game's, which the player-colours feature extends), lifted to stay readable.</summary>
    internal static Color ColorOf(PlayerRef p)
    {
        Color c = Theme.Accent;
        try { if (p.IsRealPlayer) c = UIManager.GetPlayerColor(p); } catch { }
        Color.RGBToHSV(c, out float h, out float s, out float v);
        return Color.HSVToRGB(h, Mathf.Min(s, 0.85f), Mathf.Max(v, 0.8f));
    }

    // ---------------------------------------------------------------- text helpers

    /// <summary>Control characters dropped, runs of whitespace collapsed, trimmed and cut to <paramref name="max"/>.</summary>
    internal static string Clean(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue;
            if (space) { if (sb.Length >= max) break; sb.Append(' '); space = false; }
            if (sb.Length >= max) break;
            sb.Append(c);
        }
        // Never end on half a surrogate pair.
        if (sb.Length > 0 && char.IsHighSurrogate(sb[sb.Length - 1])) sb.Length--;
        return sb.ToString();
    }

    /// <summary>For places that render rich text (the menu): angle brackets shown as look-alikes, so no tag applies.</summary>
    internal static string NoTags(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace('<', '‹').Replace('>', '›');

    internal static void LogOnce(string id, string message)
    {
        if (LoggedOnce.Count < 50 && LoggedOnce.Add(id)) Plugin.Logger.LogWarning(message);
    }
}
