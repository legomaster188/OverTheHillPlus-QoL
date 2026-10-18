using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.HudPlus;

/// <summary>Draws HUD+'s speedometer (analog, digital or minimal skin) and the car status card, on Repaint only.</summary>
internal static class HudPlusDraw
{
    private const float DialSize = 190f, DialStart = -135f, DialSweep = 270f;
    private const float DigitalW = 250f, DigitalH = 104f, MinimalW = 150f, MinimalH = 56f;
    private const float StatusW = 236f, Gap = 10f;
    private const int RpmSegments = 14;

    private static readonly Color Shadow = new(0f, 0f, 0f, 0.75f);
    private static Matrix4x4 _base;

    // Texts rebuilt only when the shown number changes (OnGUI runs several times per frame).
    private static int _speedShown = int.MinValue, _gearShown = int.MinValue, _rpmShown = int.MinValue;
    private static string _speedText = "0", _gearText = "N", _rpmText = "", _minimalText = "0";
    private static bool _mphShown;
    private static float _nextStatusText;
    private static string _healthText, _winchText, _odoText, _wetText;
    private static int _dialMaxShown = -1;
    private static readonly string[] DialLabels = new string[16];
    private static float _topKmh;

    internal static void Draw()
    {
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Repaint) return;
        if (!CarReadout.HasCar || CameraView.HudHidden) return;
        Theme.Ensure();
        float scale = Mathf.Clamp(HudPlusConfig.SpeedoScale.Value, 0.6f, 2f);
        var screen = HudLayout.Begin(scale);
        _base = GUI.matrix;
        float bgA = HudLayout.Opacity(HudPlusConfig.Opacity.Value, 0.2f);
        var bg = new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, bgA);

        float speedoH = 0f;
        if (HudPlusConfig.ShowSpeedo.Value)
        {
            UpdateTexts();
            switch (HudPlusConfig.Skin.Value)
            {
                case SpeedoSkin.Analog:
                    Analog(HudLayout.Place(HudPlusConfig.SpeedoCorner.Value, screen, DialSize, DialSize), bg);
                    speedoH = DialSize;
                    break;
                case SpeedoSkin.Digital:
                    Digital(HudLayout.Place(HudPlusConfig.SpeedoCorner.Value, screen, DigitalW, DigitalH), bg);
                    speedoH = DigitalH;
                    break;
                default:
                    Minimal(HudLayout.Place(HudPlusConfig.SpeedoCorner.Value, screen, MinimalW, MinimalH));
                    speedoH = MinimalH;
                    break;
            }
        }
        if (HudPlusConfig.ShowStatus.Value)
        {
            float offset = HudPlusConfig.ShowSpeedo.Value && HudPlusConfig.StatusCorner.Value == HudPlusConfig.SpeedoCorner.Value ? speedoH + Gap : 0f;
            Status(screen, offset, bg);
        }
    }

    private static void UpdateTexts()
    {
        bool mph = HudPlusConfig.Mph.Value;
        int s = HudMath.ShownSpeed(CarReadout.Kmh, mph);
        if (s != _speedShown || mph != _mphShown)
        {
            _speedShown = s;
            _mphShown = mph;
            _speedText = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _minimalText = _speedText + (mph ? " <size=16>mph</size>" : " <size=16>km/h</size>");
        }
        int g = CarReadout.Gear;
        if (g != _gearShown) { _gearShown = g; _gearText = HudMath.GearText(g); }
        int rpm = Mathf.RoundToInt(CarReadout.Rpm / 50f) * 50;
        if (rpm != _rpmShown) { _rpmShown = rpm; _rpmText = rpm + " rpm"; }
        if (CarReadout.Kmh > _topKmh && CarReadout.Kmh < 600f) _topKmh = CarReadout.Kmh;
    }

    private static string Unit => HudPlusConfig.Mph.Value ? "MPH" : "KM/H";

    // ---------------------------------------------------------------- skins

    private static void Analog(Rect r, Color bg)
    {
        float radius = r.width * 0.5f;
        var c = r.center;
        Theme.Fill(r, bg, Mathf.RoundToInt(radius));

        bool mph = HudPlusConfig.Mph.Value;
        int max = HudPlusConfig.DialMax.Value > 0 ? HudPlusConfig.DialMax.Value
            : HudMath.DialMaxFor(Mathf.Max(mph ? 100f : 160f, (mph ? _topKmh / HudMath.KmhPerMph : _topKmh) * 1.1f));
        int step = max <= 120 ? 20 : max <= 240 ? 40 : 50;
        if (max != _dialMaxShown)
        {
            _dialMaxShown = max;
            for (int i = 0; i < DialLabels.Length; i++) DialLabels[i] = (i * step).ToString();
        }

        // Ticks: a long one with a number every step, a short one between.
        int ticks = Mathf.Min(max / (step / 2), 30);
        for (int i = 0; i <= ticks; i++)
        {
            float v = i * step / 2f;
            bool major = i % 2 == 0;
            float a = HudMath.DialAngle(v, max, DialStart, DialSweep);
            Rotate(a, c);
            Theme.Fill(new Rect(c.x - (major ? 1.5f : 1f), r.y + 8f, major ? 3f : 2f, major ? 13f : 7f), major ? Theme.Text : Theme.Muted, 1);
            GUI.matrix = _base;
            int label = i / 2;
            if (major && label < DialLabels.Length)
            {
                float rad = a * Mathf.Deg2Rad;
                var p = c + new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad)) * (radius - 34f);
                Centered(new Rect(p.x - 22f, p.y - 9f, 44f, 18f), DialLabels[label], Theme.Small);
            }
        }

        // Past the top of the dial the needle stays at the end.
        float needle = HudMath.DialAngle(mph ? CarReadout.Kmh / HudMath.KmhPerMph : CarReadout.Kmh, max, DialStart, DialSweep);
        Rotate(needle, c);
        Theme.Fill(new Rect(c.x - 2f, r.y + 18f, 4f, radius - 10f), Theme.Accent, 2);
        GUI.matrix = _base;
        Theme.Fill(new Rect(c.x - 8f, c.y - 8f, 16f, 16f), Theme.Accent, 8);

        Big(new Rect(c.x - 60f, c.y + 18f, 120f, 40f), _speedText, 32, Theme.Title);
        Centered(new Rect(c.x - 40f, c.y + 54f, 80f, 16f), Unit, Theme.Small);
        if (HudPlusConfig.ShowRpm.Value)
        {
            Big(new Rect(c.x - 30f, c.y - 56f, 60f, 30f), _gearText, 22, Theme.Value);
            Centered(new Rect(c.x - 50f, c.y + 70f, 100f, 16f), _rpmText, Theme.Small);
        }
    }

    private static void Digital(Rect r, Color bg)
    {
        Theme.Fill(r, bg, 10);
        Big(new Rect(r.x + 14f, r.y + 6f, 150f, 52f), _speedText, 46, Theme.Title, TextAnchor.MiddleLeft);
        GUI.Label(new Rect(r.x + 16f, r.y + 56f, 80f, 16f), Unit, Theme.Small);
        if (!HudPlusConfig.ShowRpm.Value) return;
        Big(new Rect(r.xMax - 66f, r.y + 8f, 52f, 46f), _gearText, 34, Theme.Value, TextAnchor.MiddleCenter);
        GUI.Label(new Rect(r.xMax - 66f, r.y + 54f, 52f, 16f), "<color=#8C96A6>GEAR</color>", Theme.Small);

        int lit = HudMath.LitSegments(CarReadout.Rpm, CarReadout.PeakRpm, RpmSegments);
        float x = r.x + 14f, w = (r.width - 28f - (RpmSegments - 1) * 3f) / RpmSegments;
        for (int i = 0; i < RpmSegments; i++)
        {
            float t = (i + 1f) / RpmSegments;
            var col = i >= lit ? Theme.Off : t > 0.9f ? Theme.Bad : t > 0.72f ? Theme.Warn : Theme.Good;
            Theme.Fill(new Rect(x + i * (w + 3f), r.y + 80f, w, 10f), col, 2);
        }
    }

    private static void Minimal(Rect r)
    {
        var style = Theme.Title;
        int prev = style.fontSize;
        var align = style.alignment;
        style.fontSize = 40;
        style.alignment = TextAnchor.MiddleRight;
        var text = _minimalText;
        var prevColor = GUI.color;
        GUI.color = Shadow;
        GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, style);
        GUI.color = prevColor;
        GUI.Label(r, text, style);
        style.fontSize = prev;
        style.alignment = align;
    }

    // ---------------------------------------------------------------- status card

    private static void Status(Vector2 screen, float offset, Color bg)
    {
        if (Time.unscaledTime >= _nextStatusText || _healthText == null)
        {
            _nextStatusText = Time.unscaledTime + 0.2f;
            _healthText = CarReadout.Health < 0 ? "—" : CarReadout.Health + "%";
            _winchText = CarReadout.WinchState < 0 ? null
                : HudMath.WinchText(CarReadout.WinchState, CarReadout.WinchTaut, CarReadout.WinchTension, CarReadout.RopeLength);
            _odoText = CarReadout.OdometerM < 0f ? null : HudMath.Odometer(CarReadout.OdometerM, HudPlusConfig.Mph.Value);
            _wetText = CarReadout.Water < 0f ? null : HudMath.Wetness(CarReadout.Water);
        }

        bool health = HudPlusConfig.ShowDamage.Value && CarReadout.Health >= 0;
        bool tyres = HudPlusConfig.ShowTyres.Value && CarReadout.TyreCount > 0;
        bool winch = HudPlusConfig.ShowWinch.Value && _winchText != null;
        bool odo = HudPlusConfig.ShowOdometer.Value && _odoText != null;
        if (!health && !tyres && !winch && !odo) return;

        float h = 12f + 18f + (health ? 30f : 0f) + (tyres ? 62f : 0f) + (winch ? 20f : 0f) + (odo ? 20f : 0f) + 8f;
        var r = HudLayout.Place(HudPlusConfig.StatusCorner.Value, screen, StatusW, h, offset);
        Theme.Fill(r, bg, 10);
        float x = r.x + 12f, w = r.width - 24f, y = r.y + 10f;
        GUI.Label(new Rect(x, y, w, 18f), "<b>CAR</b>", Theme.Small);
        y += 18f;

        if (health)
        {
            GUI.Label(new Rect(x, y, 70f, 18f), "Health", Theme.Small);
            GUI.Label(new Rect(x + w - 50f, y, 50f, 18f), _healthText, Theme.Label);
            float t = CarReadout.Health / 100f;
            var bar = new Rect(x, y + 20f, w, 6f);
            Theme.Fill(bar, Theme.Off, 3);
            Theme.Fill(new Rect(bar.x, bar.y, bar.width * t, bar.height), t > 0.6f ? Theme.Good : t > 0.3f ? Theme.Warn : Theme.Bad, 3);
            y += 30f;
        }
        if (tyres)
        {
            GUI.Label(new Rect(x, y, 70f, 18f), "Tyres", Theme.Small);
            if (_wetText != null) GUI.Label(new Rect(x, y + 18f, 80f, 18f), _wetText, Theme.Label);
            TyreDiagram(new Rect(x + w - 64f, y, 56f, 56f));
            y += 62f;
        }
        if (winch)
        {
            GUI.Label(new Rect(x, y, 60f, 18f), "Winch", Theme.Small);
            GUI.Label(new Rect(x + 56f, y, w - 56f, 18f), _winchText, Theme.Label);
            y += 20f;
        }
        if (odo)
        {
            GUI.Label(new Rect(x, y, 70f, 18f), "Odometer", Theme.Small);
            GUI.Label(new Rect(x + 76f, y, w - 76f, 18f), _odoText, Theme.Label);
        }
    }

    /// <summary>The tyres seen from above (front at the top), coloured by grip.</summary>
    private static void TyreDiagram(Rect r)
    {
        int n = CarReadout.TyreCount;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var t = CarReadout.Tyres[i];
            minX = Mathf.Min(minX, t.X); maxX = Mathf.Max(maxX, t.X);
            minZ = Mathf.Min(minZ, t.Z); maxZ = Mathf.Max(maxZ, t.Z);
        }
        const float tw = 11f, th = 17f;
        Theme.Fill(new Rect(r.center.x - 7f, r.y + 6f, 14f, r.height - 12f), Theme.Line, 4);
        for (int i = 0; i < n; i++)
        {
            var t = CarReadout.Tyres[i];
            float u = maxX - minX < 0.05f ? 0.5f : (t.X - minX) / (maxX - minX);
            float v = maxZ - minZ < 0.05f ? 0.5f : (t.Z - minZ) / (maxZ - minZ);
            float px = r.x + u * (r.width - tw);
            float py = r.y + (1f - v) * (r.height - th);
            var col = t.Grip switch
            {
                TyreGrip.Grip => Theme.Good,
                TyreGrip.Slipping => Theme.Warn,
                TyreGrip.Sliding => Theme.Bad,
                _ => Theme.Off,
            };
            Theme.Fill(new Rect(px, py, tw, th), col, 3);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Turns GUI.matrix round <paramref name="pivot"/>; set it back to <see cref="_base"/> after drawing.</summary>
    private static void Rotate(float degrees, Vector2 pivot) => GUI.matrix = HudLayout.Rotated(_base, degrees, pivot);

    private static void Centered(Rect r, string text, GUIStyle style)
    {
        var align = style.alignment;
        style.alignment = TextAnchor.MiddleCenter;
        GUI.Label(r, text, style);
        style.alignment = align;
    }

    private static void Big(Rect r, string text, int size, GUIStyle style, TextAnchor anchor = TextAnchor.MiddleCenter)
    {
        int prev = style.fontSize;
        var align = style.alignment;
        style.fontSize = size;
        style.alignment = anchor;
        GUI.Label(r, text, style);
        style.fontSize = prev;
        style.alignment = align;
    }
}
