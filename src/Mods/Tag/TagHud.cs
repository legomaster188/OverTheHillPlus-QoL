using System;
using System.Linq;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Tag;

/// <summary>
/// The round on screen: a banner with the mode, your role and the clock, the scoreboard, short notices, the IT marker
/// above the car that is it (tag only: in hide and seek nothing points at anyone), and the seekers' blackout during
/// the head start.
/// </summary>
internal static class TagHud
{
    private const float BannerW = 340f;
    private static GUIStyle _big, _mid, _small, _center, _right;
    private static Camera _cam;
    private static float _camAt = -100f;

    internal static void Draw()
    {
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Repaint) return;
        var snap = TagGame.View;
        if (snap == null) { Notices(); return; }
        bool running = TagGame.Running;
        bool results = !running && snap.Phase == TagPhase.Over && Time.unscaledTime - TagGame.ViewAt < 20f;
        if (!running && !results) { Notices(); return; }
        Styles();
        int me = TagCars.Me;

        if (running && snap.Mode == TagMode.HideAndSeek && snap.Phase == TagPhase.HeadStart && snap.Find(me)?.Seeker == true)
        {
            Blackout(TagGame.HeadStartLeft);
            Notices();
            return;
        }
        if (running && snap.Mode == TagMode.Tag && snap.It != me && TagConfig.ShowItMarker.Value) ItMarker(snap.It);
        Banner(snap, me, running);
        if (TagConfig.ShowScoreboard.Value) Scoreboard(snap, me);
        Notices();
    }

    private static void Styles()
    {
        Theme.Ensure();
        if (_big != null) return;
        _big = new GUIStyle(Theme.Label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = false };
        _big.normal.textColor = Theme.Text;
        _mid = new GUIStyle(Theme.Label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, richText = false };
        _mid.normal.textColor = Theme.Text;
        _small = new GUIStyle(Theme.Label) { fontSize = 12, alignment = TextAnchor.MiddleLeft, richText = false };
        _small.normal.textColor = Theme.Text;
        _center = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter };
        _right = new GUIStyle(_small) { alignment = TextAnchor.MiddleRight };
    }

    private static void Banner(TagSnapshot snap, int me, bool running)
    {
        var r = new Rect((Screen.width - BannerW) / 2f, 76f, BannerW, 58f);
        Color accent = snap.Mode == TagMode.Tag ? Theme.Bad : Theme.Info;
        string title, line;
        if (!running)
        {
            accent = Theme.Warn;
            var w = snap.Players.Where(p => p.Winner).ToList();
            title = w.Any(p => p.Id == me) ? "You win!" : "Round over";
            line = w.Count == 0 ? "No winner this time" : string.Join(", ", w.Take(3).Select(p => TagCars.ShortName(p.Id))) + (w.Count == 1 ? " wins" : " win");
        }
        else if (snap.Mode == TagMode.Tag)
        {
            title = snap.It == me ? "YOU ARE IT!" : TagCars.ShortName(snap.It) + " is it";
            line = "Tag · " + TagGame.Clock(TagGame.TimeLeft) + " left";
            if (snap.It == me && snap.TagBackFrom >= 0 && snap.TagBackLeft - (Time.unscaledTime - TagGame.ViewAt) > 0f)
                line += " · no tag-back on " + TagCars.ShortName(snap.TagBackFrom);
        }
        else
        {
            bool seeking = snap.Find(me)?.Seeker == true;
            int hiders = snap.Players.Count(p => !p.Seeker);
            title = snap.Find(me) == null ? "Hide and seek" : seeking ? "You seek" : "You hide";
            line = snap.Phase == TagPhase.HeadStart
                ? "Head start · seekers out in " + TagGame.Clock(TagGame.HeadStartLeft)
                : $"{hiders} {(hiders == 1 ? "hider" : "hiders")} left · {TagGame.Clock(TagGame.TimeLeft)}";
        }
        Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.88f), 8);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), accent, 2);
        var prev = _big.normal.textColor;
        _big.normal.textColor = accent;
        GUI.Label(new Rect(r.x, r.y + 4f, r.width, 28f), title, _big);
        _big.normal.textColor = prev;
        GUI.Label(new Rect(r.x, r.y + 32f, r.width, 20f), line, _mid);
    }

    private static void Scoreboard(TagSnapshot snap, int me)
    {
        var rows = snap.Mode == TagMode.Tag
            ? snap.Players.OrderBy(p => p.ItSeconds).ThenByDescending(p => p.Tags).ToList()
            : snap.Players.OrderByDescending(p => p.Winner).ThenBy(p => p.Seeker).ThenByDescending(p => p.Finds).ToList();
        int n = Math.Min(rows.Count, 10);
        float w = 250f, h = 26f + n * 20f;
        var r = new Rect(Screen.width - w - 16f, 150f, w, h);
        Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.80f), 8);
        GUI.Label(new Rect(r.x + 10f, r.y + 4f, w - 20f, 18f), snap.Mode == TagMode.Tag ? "PLAYER · TIME AS IT · TAGS" : "PLAYER · ROLE · FINDS", _small);
        for (int i = 0; i < n; i++)
        {
            var p = rows[i];
            string name = TagCars.ShortName(p.Id);
            string value = snap.Mode == TagMode.Tag
                ? $"{TagGame.Clock(p.ItSeconds)} · {p.Tags}"
                : $"{(p.Winner ? "winner" : p.Found ? "found" : p.Seeker ? "seeker" : "hiding")} · {p.Finds}";
            var row = new Rect(r.x + 10f, r.y + 24f + i * 20f, w - 20f, 20f);
            var prev = _small.normal.textColor;
            if (p.Id == me) _small.normal.textColor = Theme.Warn;
            else if (snap.Mode == TagMode.Tag && p.Id == snap.It) _small.normal.textColor = Theme.Bad;
            GUI.Label(new Rect(row.x, row.y, row.width - 110f, row.height), name, _small);
            _right.normal.textColor = _small.normal.textColor;
            GUI.Label(new Rect(row.xMax - 110f, row.y, 110f, row.height), value, _right);
            _small.normal.textColor = prev;
        }
    }

    private static void Blackout(float left)
    {
        var all = new Rect(0f, 0f, Screen.width, Screen.height);
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.97f);
        GUI.DrawTexture(all, Texture2D.whiteTexture);
        GUI.color = prev;
        GUI.Label(new Rect(0f, Screen.height / 2f - 40f, Screen.width, 32f), "You seek. No peeking!", _big);
        GUI.Label(new Rect(0f, Screen.height / 2f, Screen.width, 24f), "The hiders are hiding. You can drive in " + TagGame.Clock(left), _mid);
    }

    private static void ItMarker(int it)
    {
        if (!TagCars.TryPos(it, out var pos)) return;
        var cam = Cam();
        if (cam == null) return;
        var sp = cam.WorldToScreenPoint(pos + Vector3.up * 2.6f);
        if (sp.z < 0.5f) return;
        var p = new Vector2(sp.x, Screen.height - sp.y);
        if (p.x < -40f || p.x > Screen.width + 40f || p.y < -40f || p.y > Screen.height + 40f) return;
        float dist = sp.z;
        var r = new Rect(p.x - 34f, p.y - 30f, 68f, 26f);
        Theme.Fill(r, new Color(0.85f, 0.12f, 0.10f, 0.92f), 6);
        var prev = _mid.normal.textColor;
        _mid.normal.textColor = Color.white;
        GUI.Label(r, "IT", _mid);
        _mid.normal.textColor = prev;
        GUI.Label(new Rect(p.x - 50f, p.y - 4f, 100f, 18f), $"{dist:0} m", _center);
    }

    private static void Notices()
    {
        var list = TagGame.Notices;
        if (list.Count == 0) return;
        Styles();
        float y = 142f;
        float now = Time.unscaledTime;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var (text, at, color) = list[i];
            float age = now - at;
            if (age > 5f || age < 0f) continue;
            var size = _mid.CalcSize(new GUIContent(text));
            var r = new Rect((Screen.width - size.x) / 2f - 14f, y, size.x + 28f, 30f);
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(5f - age));
            Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.85f), 8);
            Theme.Fill(new Rect(r.x, r.y, 4f, r.height), color, 2);
            GUI.Label(r, text, _mid);
            GUI.color = prev;
            y += 34f;
        }
    }

    private static Camera Cam()
    {
        if (_cam == null || Time.unscaledTime - _camAt > 2f)
        {
            _camAt = Time.unscaledTime;
            _cam = Camera.main;
        }
        return _cam;
    }
}
