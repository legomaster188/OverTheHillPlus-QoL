using System.Text;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Rules;

/// <summary>On-screen parts: the rules window, the vote card (with its keys), and short toasts.</summary>
internal static class RulesOverlay
{
    private static GUIStyle _body, _title, _small;

    private static void EnsureStyles()
    {
        if (_body != null) return;
        _body = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, richText = false };
        _body.normal.textColor = Theme.Text;
        _title = new GUIStyle(_body) { fontSize = 20, fontStyle = FontStyle.Bold };
        _title.normal.textColor = Theme.Accent;
        _small = new GUIStyle(_body) { fontSize = 13 };
        _small.normal.textColor = Theme.Muted;
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
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (CoreBehaviour.CleanView) return;
        EnsureStyles();
        float y = 70f;
        if (RulesClient.VoteVisible) y = DrawVote(y) + 12f;
        if (!CoreBehaviour.MenuOpen && RulesClient.Rules != null &&
            Time.unscaledTime - RulesClient.RulesShownAt < Mathf.Clamp(RulesConfig.PopupSeconds.Value, 5f, 120f))
            y = DrawRules(y) + 12f;
        if (RulesClient.Toast != null && Time.unscaledTime - RulesClient.ToastAt < 5f) DrawToast(y);
    }

    private static float DrawRules(float y)
    {
        var p = RulesClient.Rules;
        float w = Mathf.Min(560f, Screen.width - 40f), x = (Screen.width - w) * 0.5f, inner = w - 36f;
        var sb = new StringBuilder();
        for (int i = 0; i < p.Rules.Count; i++) sb.Append(i + 1).Append(". ").Append(p.Rules[i]).Append('\n');
        string rules = sb.ToString().TrimEnd();
        float hTitle = _title.CalcHeight(new GUIContent(p.Title), inner);
        float hWelcome = p.Welcome.Length > 0 ? _body.CalcHeight(new GUIContent(p.Welcome), inner) + 8f : 0f;
        float hRules = rules.Length > 0 ? _body.CalcHeight(new GUIContent(rules), inner) : 0f;
        string foot = "Read them again any time on the Server Rules page (MODS).";
        float hFoot = _small.CalcHeight(new GUIContent(foot), inner);
        float h = 18f + hTitle + 8f + hWelcome + hRules + 10f + hFoot + 16f;
        var box = new Rect(x, y, w, h);
        Theme.Fill(box, Theme.Bg, 8);
        Theme.Fill(new Rect(box.x, box.y, 4f, box.height), Theme.Accent, 2);
        float cy = y + 18f;
        GUI.Label(new Rect(x + 18f, cy, inner, hTitle), p.Title, _title);
        cy += hTitle + 8f;
        if (hWelcome > 0f) { GUI.Label(new Rect(x + 18f, cy, inner, hWelcome), p.Welcome, _body); cy += hWelcome; }
        if (hRules > 0f) { GUI.Label(new Rect(x + 18f, cy, inner, hRules), rules, _body); cy += hRules; }
        cy += 10f;
        GUI.Label(new Rect(x + 18f, cy, inner, hFoot), foot, _small);
        return box.yMax;
    }

    private static float DrawVote(float y)
    {
        var v = RulesClient.Vote;
        float w = Mathf.Min(460f, Screen.width - 40f), x = (Screen.width - w) * 0.5f, inner = w - 32f;
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
                line3 = RulesHost.Hosting ? "Veto on the Server Rules page (MODS) to keep them" : null;
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
        float h1 = _title.CalcHeight(new GUIContent(head), inner);
        float h2 = _small.CalcHeight(new GUIContent(line2), inner);
        float h3 = line3 != null ? _body.CalcHeight(new GUIContent(line3), inner) + 4f : 0f;
        var box = new Rect(x, y, w, 14f + h1 + 4f + h2 + h3 + 14f);
        Theme.Fill(box, Theme.Bg, 8);
        Theme.Fill(new Rect(box.x, box.y, 4f, box.height), edge, 2);
        float cy = y + 14f;
        GUI.Label(new Rect(x + 16f, cy, inner, h1), head, _title);
        cy += h1 + 4f;
        GUI.Label(new Rect(x + 16f, cy, inner, h2), line2, _small);
        cy += h2;
        if (line3 != null) GUI.Label(new Rect(x + 16f, cy + 4f, inner, h3), line3, _body);
        return box.yMax;
    }

    private static void DrawToast(float y)
    {
        string t = RulesClient.Toast;
        float w = Mathf.Min(460f, Screen.width - 40f), x = (Screen.width - w) * 0.5f;
        float h = _body.CalcHeight(new GUIContent(t), w - 28f) + 16f;
        var box = new Rect(x, y, w, h);
        Theme.Fill(box, Theme.Bg, 8);
        GUI.Label(new Rect(x + 14f, y + 8f, w - 28f, h - 16f), t, _body);
    }
}
