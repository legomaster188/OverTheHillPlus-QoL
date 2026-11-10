using System.Text;
using OverTheHillPlus.Shared;
using OverTheHillPlus.UI;
using OverTheHillPlus.UI.HudKit;
using UnityEngine;

namespace OverTheHillPlus.Rules;

/// <summary>
/// On-screen parts: the rules window, the vote card (with its keys), and short toasts. On the screen layer of the game-style
/// HUD: a player joining sees the rules even if the game's HUD isn't up yet.
/// </summary>
internal static class RulesOverlay
{
    private static GUIStyle _body, _title, _small;
    private static float _w, _nextVote;
    private static object _rulesFor;
    private static string _rulesText, _head, _line2, _line3;
    private static Color _edge;

    private static void EnsureStyles()
    {
        if (_body != null) return;
        _body = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, richText = false };
        _body.normal.textColor = HudLayout.Cream;
        _title = new GUIStyle(_body) { fontSize = 20, fontStyle = FontStyle.Bold };
        _title.normal.textColor = Theme.Accent;
        _small = new GUIStyle(_body) { fontSize = 13 };
        _small.normal.textColor = HudLayout.Grey;
    }

    private static bool _logged;

    internal static void Draw()
    {
        // Caught here: an overlay that throws is switched off by core for the rest of the session.
        try { DrawInner(); }
        catch (System.Exception e)
        {
            DebugLog.Error("rules", e, "RulesOverlay.Draw");
            if (_logged) return;
            _logged = true;
            RulesPlugin.Logger.LogWarning("[Rules] Overlay: " + e);
        }
    }

    private static void DrawInner()
    {
        if (!HudDraw.Repaint || CoreBehaviour.CleanView) return;
        bool rules = !CoreBehaviour.MenuOpen && RulesClient.Rules != null &&
            Time.unscaledTime - RulesClient.RulesShownAt < Mathf.Clamp(RulesConfig.PopupSeconds.Value, 5f, 120f);
        bool toast = RulesClient.Toast != null && Time.unscaledTime - RulesClient.ToastAt < 5f;
        if (!RulesClient.VoteVisible && !rules && !toast) return;
        if (!HudDraw.Begin("rules", HudLayer.Screen)) return;
        try
        {
            EnsureStyles();
            _w = HudLayout.Begin().x;
            float y = 70f;
            if (RulesClient.VoteVisible) y = DrawVote(y) + 12f;
            if (rules) y = DrawRules(y) + 12f;
            if (toast) DrawToast(y);
        }
        finally
        {
            HudLayout.End();
            HudDraw.End();
        }
    }

    private static float DrawRules(float y)
    {
        var p = RulesClient.Rules;
        float w = Mathf.Min(560f, _w - 40f), x = (_w - w) * 0.5f, inner = w - 36f;
        if (!ReferenceEquals(_rulesFor, p))
        {
            _rulesFor = p;
            var sb = new StringBuilder();
            for (int i = 0; i < p.Rules.Count; i++) sb.Append(i + 1).Append(". ").Append(p.Rules[i]).Append('\n');
            _rulesText = sb.ToString().TrimEnd();
        }
        string rules = _rulesText;
        float hTitle = HudDraw.Height(p.Title, _title, inner);
        float hWelcome = p.Welcome.Length > 0 ? HudDraw.Height(p.Welcome, _body, inner) + 8f : 0f;
        float hRules = rules.Length > 0 ? HudDraw.Height(rules, _body, inner) : 0f;
        const string foot = "Read them again any time on the Rules+ page (MODS).";
        float hFoot = HudDraw.Height(foot, _small, inner);
        float h = 18f + hTitle + 8f + hWelcome + hRules + 10f + hFoot + 16f;
        var box = new Rect(x, y, w, h);
        WorldOverlay.PanelBox(box, Theme.Accent, HudLook.PanelStrong);
        float cy = y + 18f;
        HudDraw.Label(new Rect(x + 18f, cy, inner, hTitle), p.Title, _title);
        cy += hTitle + 8f;
        if (hWelcome > 0f) { HudDraw.Label(new Rect(x + 18f, cy, inner, hWelcome), p.Welcome, _body); cy += hWelcome; }
        if (hRules > 0f) { HudDraw.Label(new Rect(x + 18f, cy, inner, hRules), rules, _body); cy += hRules; }
        cy += 10f;
        HudDraw.Label(new Rect(x + 18f, cy, inner, hFoot), foot, _small);
        return box.yMax;
    }

    private static float DrawVote(float y)
    {
        float w = Mathf.Min(460f, _w - 40f), x = (_w - w) * 0.5f, inner = w - 32f;
        // The words a few times a second: the vote's counts and seconds don't move faster.
        if (_head == null || Time.unscaledTime >= _nextVote)
        {
            _nextVote = Time.unscaledTime + 0.2f;
            VoteWords();
        }
        string head = _head, line2 = _line2, line3 = _line3;
        float h1 = HudDraw.Height(head, _title, inner);
        float h2 = HudDraw.Height(line2, _small, inner);
        float h3 = line3 != null ? HudDraw.Height(line3, _body, inner) + 4f : 0f;
        var box = new Rect(x, y, w, 14f + h1 + 4f + h2 + h3 + 14f);
        WorldOverlay.PanelBox(box, _edge, HudLook.PanelStrong);
        float cy = y + 14f;
        HudDraw.Label(new Rect(x + 16f, cy, inner, h1), head, _title);
        cy += h1 + 4f;
        HudDraw.Label(new Rect(x + 16f, cy, inner, h2), line2, _small);
        cy += h2;
        if (line3 != null) HudDraw.Label(new Rect(x + 16f, cy + 4f, inner, h3), line3, _body);
        return box.yMax;
    }

    private static void VoteWords()
    {
        var v = RulesClient.Vote;
        string head, line2, line3 = null;
        Color edge = Theme.Warn;
        switch (v.Outcome)
        {
            case VoteOutcome.Open:
                head = $"Vote: remove {v.TargetName}?";
                line2 = $"Started by {v.StarterName} · {v.Yes} yes, {v.No} no · {v.Needed} of {v.Eligible} needed · {Mathf.CeilToInt(RulesClient.VoteSecondsLeft)} s";
                if (RulesClient.CanVote)
                    line3 = RulesClient.MyBallot == null
                        ? $"Press {RulesConfig.VoteYesKey.Value} for yes, {RulesConfig.VoteNoKey.Value} for no"
                        : $"You voted {(RulesClient.MyBallot.Value ? "yes" : "no")}: the other key changes it, the same key hides this";
                break;
            case VoteOutcome.Passing:
                head = $"Vote passed: removing {v.TargetName}";
                line2 = $"{v.Yes} of {v.Eligible} voted yes · in {Mathf.CeilToInt(RulesClient.VoteSecondsLeft)} s unless the host vetoes";
                line3 = RulesHost.Hosting ? "Veto on the Rules+ page (MODS) to keep them" : null;
                edge = Theme.Bad;
                break;
            case VoteOutcome.Passed:
                head = $"{v.TargetName} was voted out";
                line2 = $"{v.Yes} of {v.Eligible} voted yes";
                edge = Theme.Bad;
                break;
            case VoteOutcome.Vetoed:
                head = $"The host stopped the vote on {v.TargetName}";
                line2 = "Nobody is removed";
                edge = Theme.Info;
                break;
            case VoteOutcome.Cancelled:
                head = $"Vote on {v.TargetName} called off";
                line2 = "The player left";
                edge = Theme.Muted;
                break;
            default:
                head = $"Vote on {v.TargetName} failed";
                line2 = $"{v.Yes} yes, {v.No} no ({v.Needed} needed)";
                edge = Theme.Good;
                break;
        }
        _head = head;
        _line2 = line2;
        _line3 = line3;
        _edge = edge;
    }

    private static void DrawToast(float y)
    {
        string t = RulesClient.Toast;
        float w = Mathf.Min(460f, _w - 40f), x = (_w - w) * 0.5f;
        float h = HudDraw.Height(t, _body, w - 28f) + 16f;
        var box = new Rect(x, y, w, h);
        Theme.Fill(box, HudLook.PanelStrong, HudLayout.PanelRadius);
        HudDraw.Label(new Rect(x + 14f, y + 8f, w - 28f, h - 16f), t, _body);
    }
}
