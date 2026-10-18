using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using Fusion;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;
using SteamFriendsList = OverTheHillPlus.Features.Public.ServerBrowserPlus.SteamFriendsList;

namespace OverTheHillPlus.Features.Public.Moderation;

/// <summary>
/// Host join rules: a block list (names or Steam ids) and an optional "friends only" mode (the host's Steam friends,
/// plus a manual allow list). Checked on the host for every player who joins, once their name is known (at most
/// 15 s after joining), and a player who fails is removed with the game's own kick, which also keeps their
/// connection out for the rest of the session. Players already in the session are never checked again.
///
/// List entries (stored in the config, separated by ';'): a name (compared without case, spaces trimmed), or
/// "steam:&lt;id&gt;=&lt;label&gt;" for a Steam id (the label is only a reminder of who it is). The Steam id comes from the
/// connection token the player's game sends; the game uses it the same way for its own developer badge.
/// </summary>
internal static class JoinRules
{
    public enum FriendsMode { Off = 0, SteamFriendsAndAllowList = 1, AllowListOnly = 2 }

    internal sealed class Entry
    {
        public string Raw;      // as stored
        public ulong SteamId;   // 0 = a name entry
        public string Name;     // the name, or the label of a Steam id entry
        public string Key;      // normalised name for matching ("" = none)
        public string Shown => SteamId != 0 ? (string.IsNullOrEmpty(Name) ? "Steam " + SteamId : $"{Name} (Steam id)") : Name;
    }

    private sealed class Pending
    {
        public PlayerRef Ref;
        public float Since;
        public ulong SteamId;
        public bool TokenRead;
        public float RetryAt;   // a removal that didn't happen yet (cap reached, or an error) is tried again then
        public int Failures;
        public bool CapNoted;
    }

    private const float NameWait = 15f;
    private const int MaxEntries = 200;

    public static ConfigEntry<string> BlockedText, AllowedText;
    public static ConfigEntry<bool> BlockEnabled;
    public static ConfigEntry<int> Mode;

    private static readonly List<Entry> Blocked = new(), Allowed = new();
    private static string _blockedParsed, _allowedParsed;
    private static readonly Dictionary<int, Pending> Waiting = new();
    private static readonly HashSet<int> Checked = new();
    private static float _nextCheck, _nextScan;

    public static FriendsMode CurrentMode => (FriendsMode)Mathf.Clamp(Mode?.Value ?? 0, 0, 2);

    public static IReadOnlyList<Entry> BlockList { get { Sync(); return Blocked; } }
    public static IReadOnlyList<Entry> AllowList { get { Sync(); return Allowed; } }

    /// <summary>The host is screening joins right now (rules on and hosting).</summary>
    public static bool Active => ModerationCore.Hosting && AnyRule;

    private static bool AnyRule => (BlockEnabled.Value && BlockList.Count > 0) || CurrentMode != FriendsMode.Off;

    public static void Bind(ConfigFile f, string section)
    {
        BlockEnabled = f.Bind(section, "BlockListEnabled", true,
            "Host: remove players on the block list as they join.");
        // The key names contain "SteamId" so these lists never travel with shared settings.
        BlockedText = f.Bind(section, "BlockedNamesOrSteamIds", "",
            "Host: players removed as they join, separated by ';'. A name (case doesn't matter) or steam:<id>=<label>.");
        AllowedText = f.Bind(section, "AllowedNamesOrSteamIds", "",
            "Host: players always let in by the friends-only mode, separated by ';'. A name or steam:<id>=<label>.");
        Mode = f.Bind(section, "FriendsOnlyMode", 0,
            new ConfigDescription("Host: who may join. 0 everyone (block list still applies), 1 your Steam friends and the allow list, 2 the allow list only.",
                new AcceptableValueRange<int>(0, 2)));
        ModerationCore.SessionChanged += () => { Waiting.Clear(); Checked.Clear(); _armedSession = false; };
    }

    // ---------------------------------------------------------------- lists

    private static void Sync()
    {
        if (BlockedText == null) return;
        if (!ReferenceEquals(_blockedParsed, BlockedText.Value)) { _blockedParsed = BlockedText.Value; Parse(_blockedParsed, Blocked); }
        if (!ReferenceEquals(_allowedParsed, AllowedText.Value)) { _allowedParsed = AllowedText.Value; Parse(_allowedParsed, Allowed); }
    }

    private static void Parse(string text, List<Entry> into)
    {
        into.Clear();
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var part in text.Split(';'))
        {
            if (into.Count >= MaxEntries) break;
            var e = ParseEntry(part);
            if (e != null) into.Add(e);
        }
    }

    private static Entry ParseEntry(string raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = s.Substring(6);
            int eq = rest.IndexOf('=');
            string idText = (eq >= 0 ? rest.Substring(0, eq) : rest).Trim();
            string label = eq >= 0 ? CleanName(rest.Substring(eq + 1)) : "";
            if (!ulong.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || !ModerationCore.LooksLikeSteamId(id)) return null;
            return new Entry { Raw = s, SteamId = id, Name = label, Key = Normalize(label) };
        }
        // A bare 17-digit Steam id typed in is a Steam id too.
        if (s.Length == 17 && ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong bare) && ModerationCore.LooksLikeSteamId(bare))
            return new Entry { Raw = "steam:" + bare, SteamId = bare, Name = "", Key = "" };
        string name = CleanName(s);
        return name.Length == 0 ? null : new Entry { Raw = name, Name = name, Key = Normalize(name) };
    }

    /// <summary>Plain text for storage: no separators, control characters or markup, at most 32 characters.</summary>
    public static string CleanName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, 32));
        foreach (char c in s)
        {
            if (sb.Length >= 32) break;
            if (char.IsControl(c) || c == ';' || c == '=' || c == '<' || c == '>') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Matching form of a name: cleaned, lower case, inner whitespace collapsed.</summary>
    public static string Normalize(string s)
    {
        s = CleanName(s);
        if (s.Length == 0) return "";
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Adds an entry (a typed name or Steam id, or a player's id with their name as label). Returns a status line.</summary>
    public static string Add(bool blockList, string nameOrId, ulong steamId = 0)
    {
        var cfg = blockList ? BlockedText : AllowedText;
        string raw = steamId != 0 ? $"steam:{steamId}" + (string.IsNullOrEmpty(CleanName(nameOrId)) ? "" : "=" + CleanName(nameOrId)) : (nameOrId ?? "").Trim();
        var e = ParseEntry(raw);
        if (e == null) return "Type a player name or a Steam id first.";
        Sync();
        var list = blockList ? Blocked : Allowed;
        if (list.Count >= MaxEntries) return $"The list is full ({MaxEntries}).";
        foreach (var x in list)
            if ((e.SteamId != 0 && x.SteamId == e.SteamId) || (e.SteamId == 0 && x.SteamId == 0 && x.Key == e.Key))
                return $"{e.Shown} is already on the list.";
        cfg.Value = string.IsNullOrWhiteSpace(cfg.Value) ? e.Raw : cfg.Value.TrimEnd().TrimEnd(';') + ";" + e.Raw;
        ModerationCore.Add($"{(blockList ? "Blocked" : "Allowed")} {e.Shown}");
        return $"{(blockList ? "Block" : "Allow")} list: added {e.Shown}.";
    }

    public static string Remove(bool blockList, Entry entry)
    {
        if (entry == null) return "";
        var cfg = blockList ? BlockedText : AllowedText;
        Sync();
        var list = blockList ? Blocked : Allowed;
        var keep = new List<string>();
        foreach (var x in list) if (!ReferenceEquals(x, entry) && x.Raw != entry.Raw) keep.Add(x.Raw);
        cfg.Value = string.Join(";", keep);
        ModerationCore.Add($"{(blockList ? "Unblocked" : "Removed from the allow list:")} {entry.Shown}");
        return $"{(blockList ? "Block" : "Allow")} list: removed {entry.Shown}.";
    }

    /// <param name="labelsMatch">
    /// Block list: a Steam id entry's label also matches a player whose game sent no Steam id (catches someone dodging
    /// a block). Allow list: false, a Steam id entry matches only by id, or anyone could leave the id out and use a
    /// friend's label as their name to get in.
    /// </param>
    private static Entry Match(List<Entry> list, ulong steamId, string key, bool labelsMatch)
    {
        foreach (var e in list)
        {
            if (e.SteamId != 0)
            {
                if (steamId != 0 && e.SteamId == steamId) return e;
                if (labelsMatch && steamId == 0 && key.Length > 0 && e.Key == key) return e;
                continue;
            }
            if (key.Length > 0 && e.Key == key) return e;
        }
        return null;
    }

    // ---------------------------------------------------------------- joins

    /// <summary>Host: a player joined (runner callback). They are checked once their name is known.</summary>
    public static void PlayerJoined(PlayerRef p)
    {
        if (!p.IsRealPlayer || !ModerationCore.Hosting || ModerationCore.IsLocal(p)) return;
        Checked.Remove(p.RawEncoded); // a reused player slot is a new player
        if (!AnyRule) { Checked.Add(p.RawEncoded); return; }
        Waiting[p.RawEncoded] = new Pending { Ref = p, Since = Time.unscaledTime };
        if (CurrentMode == FriendsMode.SteamFriendsAndAllowList) SteamFriendsList.RefreshSoon();
        _nextCheck = 0f;
    }

    public static void PlayerLeft(PlayerRef p)
    {
        Waiting.Remove(p.RawEncoded);
        Checked.Remove(p.RawEncoded);
    }

    public static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < _nextCheck) return;
        _nextCheck = now + 0.5f;
        if (!ModerationCore.Hosting) return; // a new session clears everything (ModerationCore.SessionChanged)
        if (!AnyRule)
        {
            // Rules off: when they come back on, the players already here are not checked (only new joins are).
            Waiting.Clear();
            _armedSession = false;
            return;
        }

        // Cover a missed join callback: anyone in the session not seen yet is checked too. Players who were already
        // here when the rules were switched on are marked as seen without a check.
        if (now >= _nextScan)
        {
            _nextScan = now + 2f;
            try
            {
                foreach (var p in PlayerListRoster.AllPlayers())
                {
                    int raw = p.RawEncoded;
                    if (Checked.Contains(raw) || Waiting.ContainsKey(raw) || ModerationCore.IsLocal(p)) continue;
                    if (_armedSession) Waiting[raw] = new Pending { Ref = p, Since = now };
                    else Checked.Add(raw);
                }
            }
            catch (Exception e) { DebugLog.Error("serverutils", e, "JoinRules.Tick"); ModerationCore.LogOnce("scan", "[Moderation] Player scan: " + e.Message); }
            _armedSession = true;
        }

        if (Waiting.Count == 0) return;
        List<int> done = null;
        foreach (var kv in Waiting)
        {
            var w = kv.Value;
            bool finished;
            try { finished = Evaluate(w, now); }
            catch (Exception e)
            {
                DebugLog.Error("serverutils", e, "JoinRules.Tick");
                ModerationCore.LogOnce("eval", "[Moderation] Join check failed: " + e);
                finished = true;
            }
            if (finished) (done ??= new List<int>()).Add(kv.Key);
        }
        if (done != null)
            foreach (int raw in done) { Waiting.Remove(raw); Checked.Add(raw); }
    }

    // The first scan after the rules come on (or a new session starts) only records who is already here.
    private static bool _armedSession;

    /// <summary>True when the player is dealt with (let in, removed or gone).</summary>
    private static bool Evaluate(Pending w, float now)
    {
        var r = GameState.Runner;
        bool valid;
        try { valid = r != null && r.IsPlayerValid(w.Ref); } catch (Exception ex) { DebugLog.Error("serverutils", ex, "JoinRules.Evaluate"); valid = true; }
        if (!valid) return true;
        if (now < w.RetryAt) return false; // a removal that has to wait (removal cap) or failed once

        if (!w.TokenRead)
        {
            string token = ModerationCore.TokenOf(w.Ref);
            if (token != null || now - w.Since > 3f)
            {
                w.TokenRead = true;
                w.SteamId = ModerationCore.SteamIdFromToken(token);
            }
        }

        string name = ModerationCore.RealNameOf(w.Ref);
        string key = Normalize(name);
        bool timedOut = now - w.Since > NameWait;
        Sync();

        // Block list: a Steam id match needs no name.
        if (BlockEnabled.Value && Blocked.Count > 0)
        {
            var hit = Match(Blocked, w.SteamId, key, labelsMatch: true);
            if (hit != null) return RemovePlayer(w, name, $"on the block list ({hit.Shown})", now);
        }
        if (name == null && !timedOut && w.TokenRead && BlockEnabled.Value && HasNameEntries(Blocked)) return false; // wait for the name
        if (!w.TokenRead) return false;

        var mode = CurrentMode;
        if (mode == FriendsMode.Off) return true;
        if (Match(Allowed, w.SteamId, key, labelsMatch: false) != null) return true;
        if (mode == FriendsMode.SteamFriendsAndAllowList && w.SteamId != 0)
        {
            bool? friend = SteamFriendsList.IsFriend(w.SteamId);
            if (friend == true) return true;
            if (friend == null)
            {
                ModerationCore.Add($"Let {Shown(w.Ref, name)} in: your Steam friends list could not be read", true);
                return true;
            }
        }
        // Not a friend (or no Steam id): the allow list may still name them, so wait for the name first.
        if (name == null && !timedOut && HasNameEntries(Allowed, namesOnly: true)) return false;
        string why = mode == FriendsMode.AllowListOnly ? "not on the allow list"
            : w.SteamId == 0 ? "friends only, and their game sent no Steam id"
            : "friends only, not your Steam friend";
        return RemovePlayer(w, name, why, now);
    }

    /// <param name="namesOnly">Only plain name entries count (the allow list ignores Steam id labels).</param>
    private static bool HasNameEntries(List<Entry> list, bool namesOnly = false)
    {
        foreach (var e in list) if (e.Key.Length > 0 && (!namesOnly || e.SteamId == 0)) return true;
        return false;
    }

    private static string Shown(PlayerRef p, string name) => string.IsNullOrEmpty(name) ? "Player " + p.PlayerId : name;

    /// <summary>
    /// Removes a refused player. True when that is done (removed, or already gone); false keeps them waiting so the
    /// removal is tried again: when the removal cap stopped it (until it goes through), or after an error (5 tries).
    /// </summary>
    private static bool RemovePlayer(Pending w, string name, string why, float now)
    {
        // Keep the game's session ban: a refused player who retries is turned away before loading in.
        string result = ModerationCore.Kick(w.Ref, "join rule: " + why, false, joinRule: true);
        if (ModerationCore.WasRemoved(result) || result == ModerationCore.AlreadyLeft) return true;
        if (result == ModerationCore.RateLimited)
        {
            w.RetryAt = now + 3f;
            if (!w.CapNoted)
            {
                w.CapNoted = true;
                ModerationCore.Add($"Join rule: {Shown(w.Ref, name)} will be removed once the removal limit allows it", true);
            }
            return false;
        }
        if (!result.StartsWith("Failed", StringComparison.Ordinal)) return true; // not hosting any more, or it is us
        w.Failures++;
        if (w.Failures < 5)
        {
            w.RetryAt = now + 5f;
            return false;
        }
        ModerationCore.Add($"Join rule could not remove {Shown(w.Ref, name)}: {result}", true);
        return true;
    }

    /// <summary>For the menu: how many joins are waiting for a decision.</summary>
    public static int WaitingCount => Waiting.Count;
}
