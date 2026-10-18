using System;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Stats;

/// <summary>Trip Stats' per-frame work plugged into core: sampling the car, the card's hotkey, drawing the card, saving on quit.</summary>
internal static class StatsFrame
{
    private static bool _loggedDraw;
    private static float _drawRetryAt;

    internal static void Install()
    {
        CarAir.Use();
        CarAir.Landed += StatsTracker.OnLanded;
        CoreBehaviour.BeforeFeatureTicks += StatsTracker.Tick;
        CoreBehaviour.Hotkeys += Hotkeys;
        CoreBehaviour.AfterGuis += Draw;
        CoreBehaviour.OnQuit += StatsTracker.Save;
    }

    private static void Hotkeys(Keyboard kb)
    {
        if (CoreBehaviour.Pressed(kb, StatsConfig.CardKey.Value)) StatsConfig.ShowCard.Value = !StatsConfig.ShowCard.Value;
    }

    private static void Draw()
    {
        if (!StatsConfig.ShowCard.Value || Time.unscaledTime < _drawRetryAt) return;
        try { StatsCard.Draw(); }
        catch (Exception e)
        {
            _drawRetryAt = Time.unscaledTime + 5f;
            if (!_loggedDraw)
            {
                _loggedDraw = true;
                StatsPlugin.Logger.LogError("Trip Stats card (retrying every 5 s, further errors not logged): " + e);
            }
        }
        finally { HudLayout.End(); }
    }
}

/// <summary>The small trip card: a few lines of numbers in a screen corner, while you have a car.</summary>
internal static class StatsCard
{
    private const float LineH = 18f, Pad = 12f, LabelW = 96f, ColW = 104f;
    private static readonly string[] Labels = { "Distance", "Top speed", "Air time", "Longest jump", "Flips", "Driven" };
    private static readonly string[] SessionText = new string[Labels.Length], AllText = new string[Labels.Length];
    private static float _nextText;

    internal static void Draw()
    {
        // Labels only paint on Repaint; skipping the other events also skips the string work for them.
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Repaint) return;
        if (GameState.LocalCar == null || CameraView.HudHidden) return;

        if (Time.unscaledTime >= _nextText || SessionText[0] == null)
        {
            _nextText = Time.unscaledTime + 0.25f;
            Fill(SessionText, StatsTracker.Recorder.Session);
            Fill(AllText, StatsTracker.Recorder.Lifetime);
        }

        var shows = StatsConfig.Shows.Value;
        bool session = shows != StatsConfig.CardShows.AllTime, all = shows != StatsConfig.CardShows.Session;
        Theme.Ensure();
        var screen = HudLayout.Begin();
        float w = Pad * 2f + LabelW + ColW * ((session ? 1 : 0) + (all ? 1 : 0));
        float h = Pad * 2f + 20f + LineH * Labels.Length;
        var r = HudLayout.Place(StatsConfig.Corner.Value, screen, w, h);
        float a = HudLayout.Opacity(StatsConfig.CardOpacity.Value, 0.2f);
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, a), 10);

        float x = r.x + Pad, y = r.y + Pad - 2f;
        GUI.Label(new Rect(x, y, LabelW, 18f), "<b>TRIP</b>", Theme.Small);
        float cx = x + LabelW;
        if (session) { GUI.Label(new Rect(cx, y, ColW, 18f), "SESSION", Theme.Small); cx += ColW; }
        if (all) GUI.Label(new Rect(cx, y, ColW, 18f), "ALL TIME", Theme.Small);
        y += 20f;
        for (int i = 0; i < Labels.Length; i++)
        {
            GUI.Label(new Rect(x, y, LabelW, LineH), Labels[i], Theme.Small);
            cx = x + LabelW;
            if (session) { GUI.Label(new Rect(cx, y, ColW, LineH), SessionText[i], Theme.Label); cx += ColW; }
            if (all) GUI.Label(new Rect(cx, y, ColW, LineH), AllText[i], Theme.Label);
            y += LineH;
        }
    }

    private static void Fill(string[] into, TripTotals t)
    {
        bool imp = StatsConfig.Imperial.Value;
        into[0] = TripFormat.Distance(t.DistanceM, imp);
        into[1] = TripFormat.Speed(t.TopSpeedKmh, imp);
        into[2] = TripFormat.Duration(t.AirTimeS) + (t.Jumps > 0 ? $" · {t.Jumps}" : "");
        into[3] = TripFormat.Length(t.LongestJumpM, imp);
        into[4] = t.Flips.ToString();
        into[5] = TripFormat.Duration(t.DrivenS);
    }
}
