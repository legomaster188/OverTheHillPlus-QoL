using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Cargo;

/// <summary>Your cargo job on screen: a card with where to go, how far and the time left for the bonus, a marker over
/// the place, and short notices (taken, loaded, paid).</summary>
internal static class CargoHud
{
    private static GUIStyle _title, _line, _mid;
    private static Camera _cam;
    private static float _camAt = -100f;

    internal static void Draw()
    {
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Repaint) return;
        Styles();
        var job = CargoGame.MyJob;
        if (job != null)
        {
            bool loaded = job.State == JobState.Loaded;
            var target = loaded ? job.To : job.From;
            var targetPos = new Vector3(target.X, target.Y, target.Z);
            float dist = -1f;
            var car = GameState.LocalCar;
            try { if (car != null) dist = Vector3.Distance(car.transform.position, targetPos); } catch (System.Exception ex) { DebugLog.Error("cargo", ex, "CargoHud.Draw"); }
            if (CargoConfig.ShowHud.Value) Card(job, loaded, dist);
            if (CargoConfig.ShowMarker.Value) Marker(targetPos, loaded ? "DELIVER" : "PICK UP", dist);
        }
        Notices();
    }

    private static void Styles()
    {
        Theme.Ensure();
        if (_title != null) return;
        _title = new GUIStyle(Theme.Label) { fontSize = 11, fontStyle = FontStyle.Bold, richText = false };
        _title.normal.textColor = Theme.Muted;
        _line = new GUIStyle(Theme.Label) { fontSize = 14, richText = false };
        _line.normal.textColor = Theme.Text;
        _mid = new GUIStyle(_line) { alignment = TextAnchor.MiddleCenter };
    }

    private static void Card(CargoJob job, bool loaded, float dist)
    {
        var r = new Rect(Screen.width - 296f, 16f, 280f, 64f);
        Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.86f), 8);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), loaded ? Theme.Good : Theme.Info, 2);
        GUI.Label(new Rect(r.x + 12f, r.y + 4f, r.width - 20f, 16f), loaded ? "CARGO · DELIVER TO" : "CARGO · PICK UP AT", _title);
        GUI.Label(new Rect(r.x + 12f, r.y + 20f, r.width - 20f, 20f), loaded ? job.ToName : job.FromName, _line);
        string d = dist < 0f ? "" : dist >= 1000f ? $"{dist / 1000f:0.0} km" : $"{dist:0} m";
        string extra;
        if (loaded)
        {
            float left = job.TimeLeft - (Time.unscaledTime - CargoGame.ViewAt);
            extra = left > 0f ? $"{Clock(left)} for the bonus" : "late: reduced pay";
        }
        else extra = $"pays {job.Pay:N0}–{job.MaxPay:N0}";
        GUI.Label(new Rect(r.x + 12f, r.y + 40f, r.width - 20f, 20f), d.Length > 0 ? d + " · " + extra : extra, _line);
    }

    private static void Marker(Vector3 world, string label, float dist)
    {
        var cam = Cam();
        if (cam == null) return;
        var sp = cam.WorldToScreenPoint(world + Vector3.up * 3f);
        if (sp.z < 0.5f) return;
        var p = new Vector2(sp.x, Screen.height - sp.y);
        if (p.x < -60f || p.x > Screen.width + 60f || p.y < -60f || p.y > Screen.height + 60f) return;
        var r = new Rect(p.x - 46f, p.y - 30f, 92f, 24f);
        Theme.Fill(r, new Color(0.10f, 0.45f, 0.30f, 0.90f), 6);
        GUI.Label(r, label, _mid);
        if (dist >= 0f) GUI.Label(new Rect(p.x - 60f, p.y - 4f, 120f, 20f), dist >= 1000f ? $"{dist / 1000f:0.0} km" : $"{dist:0} m", _mid);
    }

    private static void Notices()
    {
        var list = CargoGame.Notices;
        if (list.Count == 0) return;
        float y = Screen.height - 150f, now = Time.unscaledTime;
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
            y -= 34f;
        }
    }

    internal static string Clock(float seconds)
    {
        int s = Mathf.Max(0, Mathf.CeilToInt(seconds));
        return $"{s / 60}:{s % 60:00}";
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
