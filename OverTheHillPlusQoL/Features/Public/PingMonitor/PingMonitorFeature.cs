using System;
using BepInEx.Configuration;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.Features.Public.Photo;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Features.Public.PingMonitor;

/// <summary>
/// Ping and session monitor (PLUS > Network):
/// - Host: a live table of every guest's ping (now, 10 s average, jitter, 1 min peak), direct/relayed link and
///   whether they run this mod, colour coded; plus the optional high-ping rule (<see cref="PingRule"/>).
/// - Everyone: a compact session HUD (your ping to the host, players, host name, session time), toggled with a
///   hotkey, and the host's ping notices.
/// Reading pings is local and harmless; only the rule ever affects other players, and it is off by default.
/// </summary>
internal sealed class PingMonitorFeature : IFeature
{
    internal const string Section = "Ping Monitor";
    private const string Tab = "Network";

    internal static ConfigEntry<bool> ShowHud, HudWhenHosting, RuleEnabled, RuleKick, RuleAllowRejoin, ShowNotices;
    internal static ConfigEntry<Key> HudKey;
    internal static ConfigEntry<int> HudCorner, RuleMs, RuleSeconds, RuleGrace;

    private static readonly string[] CornerNames = { "Top left", "Top right", "Bottom left", "Bottom right" };

    public void Register()
    {
        var f = ModConfig.File;
        ShowHud = f.Bind(Section, "ShowSessionHud", false,
            "Show a small session panel in multiplayer: your ping to the host, players, host name and session time.");
        HudKey = f.Bind(Section, "SessionHudKey", Key.None, "Hotkey that shows / hides the session panel. None = no hotkey.");
        HudCorner = f.Bind(Section, "SessionHudCorner", 1,
            new ConfigDescription("Screen corner of the session panel: 0 top left, 1 top right, 2 bottom left, 3 bottom right.",
                new AcceptableValueRange<int>(0, 3)));
        HudWhenHosting = f.Bind(Section, "SessionHudWhenHosting", true,
            "Also show the session panel while you host (with the highest guest ping instead of your own).");
        ShowNotices = f.Bind(Section, "ShowPingNotices", true,
            "Show the host's high-ping notices (guest) and the rule's actions (host) on screen.");

        RuleEnabled = f.Bind(Section, "HighPingRule", false,
            "Host only: act on guests whose ping stays above the limit (after a grace period). Off by default.");
        RuleMs = f.Bind(Section, "HighPingLimitMs", 250,
            new ConfigDescription("High-ping rule: the limit in milliseconds.", new AcceptableValueRange<int>(100, 1000)));
        RuleSeconds = f.Bind(Section, "HighPingSeconds", 30,
            new ConfigDescription("High-ping rule: how long a guest's ping has to stay above the limit.", new AcceptableValueRange<int>(10, 300)));
        RuleGrace = f.Bind(Section, "HighPingGraceSeconds", 60,
            new ConfigDescription("High-ping rule: ignore a player for this long after they join (loading in spikes the ping).",
                new AcceptableValueRange<int>(0, 600)));
        RuleKick = f.Bind(Section, "HighPingRemove", false,
            "High-ping rule: remove the player from the session instead of only warning them. At most 3 removals a minute.");
        RuleAllowRejoin = f.Bind(Section, "HighPingAllowRejoin", true,
            "High-ping rule: a guest removed for high ping may join again (the game's kick would otherwise keep them out for the session).");

        PingRule.RegisterChannel();
        Registry.OnTick(Tick);
        Registry.OnGui(DrawOverlay);
        Registry.Section(Registry.PlusMode, Tab, DrawPlayersCard, 6);
        Registry.Section(Registry.PlusMode, Tab, DrawRuleCard, 7);
        Registry.Section(Registry.PlusMode, Tab, DrawHudCard, 8);
        PatchJoinLeave();
    }

    // ---------------------------------------------------------------- per frame

    private static int _lastEvaluated = -1;

    private static void Tick()
    {
        PingTracker.Tick();
        // The rule runs right after each new sample.
        if (PingTracker.InSession && PingTracker.Hosting && PingTracker.SampleCount != _lastEvaluated)
        {
            _lastEvaluated = PingTracker.SampleCount;
            try { PingRule.Evaluate(); }
            catch (Exception e) { PingTracker.LogOnce("rule", "[Ping] High-ping rule: " + e); }
        }
        PingRule.Tick();
        HotkeyTick();
    }

    private static void HotkeyTick()
    {
        var key = HudKey.Value;
        if (key == Key.None) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        // Not while a key is being rebound or text is being typed in the menu.
        if (MenuUx.HotkeysCaptured || KeybindEditor.Capturing || MenuUx.Typing) return;
        if (GUIUtility.keyboardControl != 0) return;
        bool down;
        try { down = kb[key].wasPressedThisFrame; } catch { return; }
        if (!down) return;
        ShowHud.Value = !ShowHud.Value;
        try { Menu.SetStatus(ShowHud.Value ? "Session panel shown" : "Session panel hidden"); } catch { }
    }

    /// <summary>Joins and leaves from the game's runner callbacks (each patch on its own: the periodic scan covers a miss).</summary>
    private static void PatchJoinLeave()
    {
        var h = new Harmony(Plugin.Guid + ".pingmonitor");
        void Try(string method, string patch)
        {
            try
            {
                var target = AccessTools.Method(typeof(OverlandNetworkRunner), method);
                if (target == null) { Plugin.Logger.LogWarning($"[Ping] {method} not found; relying on the periodic scan"); return; }
                h.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(PingMonitorFeature), patch)));
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Ping] Could not patch {method}: {e.Message}"); }
        }
        Try("Fusion_INetworkRunnerCallbacks_OnPlayerJoined", nameof(JoinedPrefix));
        Try("Fusion_INetworkRunnerCallbacks_OnPlayerLeft", nameof(LeftPrefix));
    }

    private static void JoinedPrefix(PlayerRef player)
    {
        try { PingTracker.PlayerJoined(player); } catch (Exception e) { PingTracker.LogOnce("joined", "[Ping] join hook: " + e.Message); }
    }

    private static void LeftPrefix(PlayerRef player)
    {
        try { PingTracker.PlayerLeft(player); } catch (Exception e) { PingTracker.LogOnce("left", "[Ping] leave hook: " + e.Message); }
    }

    // ---------------------------------------------------------------- menu

    private static void IntSlider(ConfigEntry<int> e, string label, int min, int max, Func<int, string> shown, bool enabled)
    {
        int v = Mathf.RoundToInt(Menu.RawSlider(label, e.Value, min, max, shown(e.Value), enabled));
        if (v != e.Value) e.Value = Mathf.Clamp(v, min, max);
    }

    private static void DrawPlayersCard()
    {
        Menu.Card("Player ping", "Live ping of everyone in your session, sampled every second. Green under 100 ms, yellow under 200, red above.", () =>
        {
            if (!PingTracker.InSession)
            {
                Menu.Small("Not in a multiplayer session.");
                return;
            }
            if (!PingTracker.Hosting)
            {
                var s = PingTracker.Self;
                float now = s.Median3();
                Menu.Bar("Your ping", now < 0f ? 0f : Mathf.Clamp01(now / 300f), PingTracker.ColorFor(now), PingTracker.Ms(now));
                Menu.KeyValue("Average / jitter", $"{PingTracker.Ms(s.Average())} · ±{PingTracker.Ms(s.Jitter())}");
                Menu.KeyValue("Peak (1 min)", PingTracker.Ms(s.Peak()) + (PingTracker.SelfSource == "mod echo" ? " (measured by echo through the mod network)" : ""));
                Menu.Small("Only the host can see everyone's ping: in Host mode every car is simulated on the host's PC.");
                return;
            }
            DrawTable();
        });
    }

    private static void DrawTable()
    {
        var rows = PingTracker.Players;
        int guests = 0;
        foreach (var e in rows) if (!e.IsYou) guests++;
        if (guests == 0) { Menu.Small("No guests yet. Their ping shows here as soon as someone joins."); return; }

        float x = Menu.Left + 4f, w = Menu.Width - 8f;
        // columns: player, now, avg, jitter, peak, link, mod, rule
        float[] col = { 0f, 0.30f, 0.41f, 0.52f, 0.62f, 0.72f, 0.82f, 0.88f, 1f };
        Rect Cell(Rect row, int i) => new(x + w * col[i], row.y, w * (col[i + 1] - col[i]), row.height);

        var head = Menu.NextRow(18f);
        string[] titles = { "PLAYER", "PING", "AVG", "JITTER", "PEAK", "LINK", "MOD", "RULE" };
        for (int i = 0; i < titles.Length; i++) Menu.Label(Cell(head, i), titles[i], Theme.Small);

        int shown = 0, red = 0, relayed = 0;
        foreach (var e in rows)
        {
            if (e.IsYou) continue;
            if (shown++ >= 24) { Menu.Small($"+{guests - 24} more"); break; }
            var row = Menu.NextRow(20f);
            var s = e.Stats;
            float now = e.Misses > 2 ? -1f : s.Median3();
            float avg = s.Average(), jit = s.Jitter(), peak = s.Peak();
            if (now >= 200f) red++;
            if (e.Link == "relayed") relayed++;
            Menu.Label(Cell(row, 0), Clip(e.Name, 20), Theme.Label);
            Menu.Label(Cell(row, 1), now < 0f ? (s.Count == 0 ? "..." : "lost") : $"{now:0}", Theme.Label, PingTracker.ColorFor(now));
            Menu.Label(Cell(row, 2), avg < 0f ? "-" : $"{avg:0}", Theme.Label, PingTracker.ColorFor(avg));
            Menu.Label(Cell(row, 3), jit < 0f ? "-" : $"±{jit:0}", Theme.Label, jit < 0f ? Theme.Muted : jit < 20f ? Theme.Text : jit < 50f ? Theme.Warn : Theme.Bad);
            Menu.Label(Cell(row, 4), peak < 0f ? "-" : $"{peak:0}", Theme.Label, PingTracker.ColorFor(peak));
            Menu.Label(Cell(row, 5), e.Link == "" ? "-" : e.Link, Theme.Label, e.Link == "relayed" ? Theme.Warn : Theme.Muted);
            Menu.Label(Cell(row, 6), e.Modded ? "yes" : "-", Theme.Label, e.Modded ? Theme.Info : Theme.Muted);
            string rule = e.RuleState ?? "";
            Menu.Label(Cell(row, 7), rule, Theme.Label,
                rule.StartsWith("remov", StringComparison.Ordinal) || rule == "warned" ? Theme.Bad : rule.Contains('/') ? Theme.Warn : Theme.Muted);
        }

        var worst = PingTracker.Worst();
        if (worst != null) Menu.KeyValue("Highest now", $"{PingTracker.Ms(worst.Stats.Median3())} ({Clip(worst.Name, 24)})");
        Menu.Small("Values in ms. Avg = last 10 s, jitter = average change between seconds (felt as rubber-banding), peak = last minute. " +
                   "Relayed = traffic goes through Photon's relay (no direct route), which adds latency. MOD = runs this mod with the mod network on.");
        if (red > 0 && red * 2 >= guests && guests >= 2)
            Menu.Small("Most guests are red at once: that usually points at your own upload or a stall on your PC, not at them.");
    }

    private static void DrawRuleCard()
    {
        if (!PingTracker.Hosting && !RuleEnabled.Value) return; // a host setting: only clutter for guests
        Menu.Card("High-ping rule (host)", "Warn or remove guests whose ping stays too high. Off by default.", () =>
        {
            bool on = RuleEnabled.Value;
            Menu.Switch(RuleEnabled, "High-ping rule", on ? "On while you host" : "Off: nobody is warned or removed", true);
            IntSlider(RuleMs, "Limit", 100, 1000, v => v + " ms", on);
            IntSlider(RuleSeconds, "For", 10, 300, v => v + " s", on);
            IntSlider(RuleGrace, "Grace after joining", 0, 600, v => v <= 0 ? "none" : v + " s", on);
            Menu.Switch(RuleKick, "Remove from the session", RuleKick.Value ? "Removes the player (at most 3 a minute, 6 with the AFK watch)" : "Only warns (you and, if modded, the player)", on);
            Menu.Switch(RuleAllowRejoin, "Let them rejoin", "A guest removed for high ping may join again", on && RuleKick.Value);
            if (PingTracker.Hosting && on) Menu.KeyValue("Status", PingRule.HostState);
            if (PingRule.Warned + PingRule.Removed > 0) Menu.KeyValue("This session", $"{PingRule.Warned} warned · {PingRule.Removed} removed");
            Menu.Small("Guests running this mod with the mod network on are told at half the time and when the limit is hit; " +
                       "others can't be told. Nothing counts while most guests lag at once, within 10 s of someone joining, or when your game stalls.");
            if (on && !Net.Enabled.Value) Menu.Small("Turn on the mod network (below) to warn modded guests before acting.");
        });
    }

    private static void DrawHudCard()
    {
        Menu.Card("Session panel", "A small on-screen panel: your ping to the host, players, host name and session time.", () =>
        {
            Menu.Switch(ShowHud, "Show session panel", "Only in multiplayer sessions; hidden in clean view and screenshots", true);
            string key = HudKey.Value == Key.None ? "none" : KeybindEditor.KeyName(HudKey.Value);
            Menu.Chips(2,
                (KeybindEditor.Capturing ? "Press a key..." : $"Hotkey: {key}", () => KeybindEditor.StartCapture(HudKey), !KeybindEditor.Capturing),
                ("Clear hotkey", () => { HudKey.Value = Key.None; Menu.SetStatus("Session panel hotkey cleared"); }, HudKey.Value != Key.None));
            int c = Mathf.Clamp(HudCorner.Value, 0, 3);
            Menu.Chips(4,
                ((c == 0 ? "> " : "") + CornerNames[0], () => HudCorner.Value = 0, true),
                ((c == 1 ? "> " : "") + CornerNames[1], () => HudCorner.Value = 1, true),
                ((c == 2 ? "> " : "") + CornerNames[2], () => HudCorner.Value = 2, true),
                ((c == 3 ? "> " : "") + CornerNames[3], () => HudCorner.Value = 3, true));
            Menu.Switch(HudWhenHosting, "Also while hosting", "Shows the highest guest ping instead of your own", true);
            Menu.Switch(ShowNotices, "Ping notices", "The host's high-ping notices (guest) and the rule's actions (host)", true);
            if (PingTracker.InSession)
            {
                Menu.KeyValue("Host", PingTracker.Hosting ? "you" : string.IsNullOrEmpty(PingTracker.HostName) ? "?" : PingTracker.HostName);
                Menu.KeyValue("Session", SessionLine());
                if (!PingTracker.Hosting && PingRule.HostRuleKnown)
                    Menu.KeyValue("Host's ping rule", HostRuleText());
            }
        });
    }

    // ---------------------------------------------------------------- overlay

    private static GUIStyle _hudStyle, _toastStyle;

    private static void DrawOverlay()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (PhotoCapture.CleanViewActive || PhotoCapture.Capturing) return;
        Theme.Ensure();
        if (ShowNotices.Value) DrawNotice();
        if (ShowHud.Value && PingTracker.InSession && (!PingTracker.Hosting || HudWhenHosting.Value)) DrawHud();
    }

    private static readonly System.Collections.Generic.List<(string text, Color color)> Lines = new();

    private static void DrawHud()
    {
        if (_hudStyle == null)
        {
            _hudStyle = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleLeft, richText = false, wordWrap = false };
            _hudStyle.normal.textColor = Theme.Text;
        }
        Lines.Clear();
        if (PingTracker.Hosting)
        {
            var worst = PingTracker.Worst();
            float ms = worst?.Stats.Median3() ?? -1f;
            Lines.Add((worst == null ? "Hosting · no guests" : $"Highest ping {PingTracker.Ms(ms)} · {Clip(worst.Name, 16)}", PingTracker.ColorFor(ms)));
        }
        else
        {
            var s = PingTracker.Self;
            float ms = s.Median3(), jit = s.Jitter();
            Lines.Add(($"Ping {PingTracker.Ms(ms)}" + (jit >= 0f ? $" ±{jit:0}" : ""), PingTracker.ColorFor(ms)));
        }
        string players = PingTracker.MaxPlayers > 0 ? $"{PingTracker.PlayerCount}/{PingTracker.MaxPlayers}" : PingTracker.PlayerCount.ToString();
        string host = PingTracker.Hosting ? "you" : string.IsNullOrEmpty(PingTracker.HostName) ? "?" : Clip(PingTracker.HostName, 18);
        Lines.Add(($"Players {players} · Host {host}", Theme.Text));
        Lines.Add((SessionLine(), Theme.Muted));
        if (!PingTracker.Hosting && PingRule.HostRuleKnown && PingRule.HostRuleOn)
            Lines.Add(("Host limit: " + HostRuleText(), Theme.Muted));

        const float lineH = 18f, padX = 10f, padY = 6f;
        float wMax = 0f;
        foreach (var (text, _) in Lines) wMax = Math.Max(wMax, _hudStyle.CalcSize(new GUIContent(text)).x);
        float w = wMax + padX * 2f + 4f, h = Lines.Count * lineH + padY * 2f;
        int corner = Mathf.Clamp(HudCorner.Value, 0, 3);
        const float margin = 12f;
        float px = corner % 2 == 0 ? margin : Screen.width - w - margin;
        float py = corner < 2 ? margin : Screen.height - h - margin;
        var r = new Rect(px, py, w, h);
        Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.78f), 6);
        Theme.Fill(new Rect(r.x, r.y, 3f, r.height), Lines[0].color, 2);
        var prev = _hudStyle.normal.textColor;
        for (int i = 0; i < Lines.Count; i++)
        {
            _hudStyle.normal.textColor = Lines[i].color;
            GUI.Label(new Rect(r.x + padX + 2f, r.y + padY + i * lineH, w - padX * 2f, lineH), Lines[i].text, _hudStyle);
        }
        _hudStyle.normal.textColor = prev;
    }

    private static void DrawNotice()
    {
        string msg = PingRule.LastEvent;
        float age = Time.unscaledTime - PingRule.LastEventAt;
        const float show = 8f;
        if (string.IsNullOrEmpty(msg) || age > show || age < 0f) return;
        if (_toastStyle == null)
        {
            _toastStyle = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleCenter, richText = false, wordWrap = true };
            _toastStyle.normal.textColor = Theme.Text;
        }
        float alpha = Mathf.Clamp01(show - age);
        var prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        float w = Mathf.Min(560f, Screen.width - 40f);
        float th = _toastStyle.CalcHeight(new GUIContent(msg), w - 28f);
        var r = new Rect((Screen.width - w) / 2f, 84f, w, th + 16f);
        Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.9f), 8);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), PingRule.LastEventBad ? Theme.Bad : Theme.Warn, 2);
        GUI.Label(new Rect(r.x + 14f, r.y + 8f, w - 28f, th), msg, _toastStyle);
        GUI.color = prev;
    }

    // ---------------------------------------------------------------- text

    private static string SessionLine()
    {
        string you = PingTracker.JoinedAt >= 0f ? PingTracker.Duration(Time.unscaledTime - PingTracker.JoinedAt) : "-";
        return PingTracker.ServerUptime > 0f
            ? $"Session {PingTracker.Duration(PingTracker.ServerUptime)} · you {you}"
            : $"In session {you}";
    }

    private static string HostRuleText() =>
        !PingRule.HostRuleOn ? "off"
            : $"{PingRule.HostRuleMs} ms for {PingRule.HostRuleSeconds} s, then " + (PingRule.HostRuleKicks ? "removed" : "warned");

    private static string Clip(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
