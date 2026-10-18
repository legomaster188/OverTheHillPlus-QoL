using System.Collections.Generic;
using OverTheHillPlus.Shared;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Race;

/// <summary>
/// Race Mode on screen: checkpoints in the world (rings and markers, the next one bright, with a dotted way to it),
/// on the map screen (core's <see cref="MapMarkers"/>), the countdown, your timer / lap / checkpoint / split, the
/// racers' order, and the results.
/// </summary>
internal static class RaceHud
{
    private const string Owner = "race";
    private static readonly Color Next = Theme.Hex(0xF2B33D), Later = new(1f, 1f, 1f, 0.55f), StartColor = Theme.Hex(0x4CC38A);
    private static GUIStyle _line, _lineRight, _timer;
    private static float _nextMap;
    private static string _mapTrackId;
    private static int _mapNextSeq = -2;
    private static readonly List<MapMarkers.Spot> Spots = new();

    // ---------------------------------------------------------------- map screen (per tick)

    public static void Tick()
    {
        var t = RaceView.Track;
        var me = RaceView.Me;
        bool racing = me != null && RaceView.Live && me.State == RacerState.Racing;
        bool show = t != null && RaceConfig.ShowOnMap.Value && (racing || RaceView.Live || RaceConfig.ShowTrackIdle.Value);
        int nextSeq = racing ? me.NextSeq : -1;

        if (!show)
        {
            if (_mapTrackId != null) { MapMarkers.Set(Owner, null); MapMarkers.SetRoute(Owner, false, default, default, default); _mapTrackId = null; }
            return;
        }

        bool countdown = racing && Time.unscaledTime < RaceView.GoAt;
        if (racing && GameMap.TryCarPosition(out var car) && nextSeq < t.SequenceLength)
            MapMarkers.SetRoute(Owner, true, car, Pos(t.Gates[countdown ? 0 : t.GateAt(nextSeq)]), countdown ? StartColor : Next);
        else MapMarkers.SetRoute(Owner, false, default, default, default);

        if (Time.unscaledTime < _nextMap && _mapTrackId == RaceView.TrackId && _mapNextSeq == nextSeq) return;
        _nextMap = Time.unscaledTime + 1f;
        _mapTrackId = RaceView.TrackId;
        _mapNextSeq = nextSeq;
        Spots.Clear();
        int nextGate = racing && nextSeq < t.SequenceLength ? t.GateAt(nextSeq) : -1;
        for (int i = 0; i < t.Gates.Count; i++)
            Spots.Add(new MapMarkers.Spot(Pos(t.Gates[i]), GateLabel(t, i), i == nextGate ? Next : i == 0 ? StartColor : Later));
        MapMarkers.Set(Owner, Spots);
    }

    private static Vector3 Pos(RaceGate g) => new(g.X, g.Y, g.Z);

    private static string GateLabel(RaceTrack t, int i)
    {
        if (i == 0) return t.Kind == RaceKind.Circuit ? "Start / finish" : "Start";
        if (t.Kind == RaceKind.Sprint && i == t.Gates.Count - 1) return "Finish";
        return "Checkpoint " + i;
    }

    // ---------------------------------------------------------------- OnGUI

    public static void Draw()
    {
        if (!RaceConfig.ShowHud.Value) return;
        if (!WorldOverlay.Begin()) return;
        EnsureStyles();
        var t = RaceView.Track;
        var me = RaceView.Me;
        bool racing = me != null && RaceView.Live && me.State == RacerState.Racing;
        float now = Time.unscaledTime;

        if (t != null && t.Gates.Count >= 1) DrawGates(t, me, racing);

        // Countdown, then GO.
        if (RaceView.Live)
        {
            float left = RaceView.GoAt - now;
            if (left > 0f) WorldOverlay.BigText(Mathf.CeilToInt(left).ToString(), Screen.height * 0.28f, left <= 3f ? Next : Theme.Text);
            else if (left > -1.5f && me != null) WorldOverlay.BigText("GO!", Screen.height * 0.28f, StartColor);
        }

        if (me != null && RaceView.Live && t != null) DrawTimer(t, me, now);
        if (RaceView.Live && RaceView.Racers.Count > 1) DrawOrder(t);
        if (RaceView.Results != null && now - RaceView.ResultsAt < 15f && now >= RaceView.ResultsAt) DrawResults();

        // The latest happening, under the timer.
        if (RaceView.Ticker.Length > 0 && now - RaceView.TickerAt < 5f && now >= RaceView.TickerAt)
        {
            var c = new GUIContent(RaceView.Ticker);
            var size = _line.CalcSize(c);
            var r = new Rect((Screen.width - size.x) / 2f - 12f, 128f, size.x + 24f, 28f);
            WorldOverlay.Panel(r, Next, inner => GUI.Label(new Rect(r.x, r.y, r.width, r.height), c, CenterLine));
        }
    }

    private static GUIStyle _center;
    private static GUIStyle CenterLine => _center ??= new GUIStyle(_line) { alignment = TextAnchor.MiddleCenter };

    private static void DrawGates(RaceTrack t, RaceView.Entry me, bool racing)
    {
        if (!racing && !RaceView.Live && !RaceConfig.ShowTrackIdle.Value) return;
        bool hasCar = GameMap.TryCarPosition(out var car);
        if (racing && Time.unscaledTime < RaceView.GoAt)
        {
            // Countdown: get to the start (you must be there at GO).
            var s = t.Gates[0];
            var sp = Pos(s);
            WorldOverlay.GroundRing(sp, s.Radius, StartColor, 32);
            WorldOverlay.Marker(sp + Vector3.up * 4f, "Start" + (hasCar ? "  " + GameMap.DistanceText(GameMap.FlatDistance(car, sp)) : ""),
                StartColor, edgeWhenOffscreen: true, size: 14f);
            if (RaceConfig.ShowRoute.Value && hasCar) WorldOverlay.GroundDots(car, sp, StartColor, 200f);
            if (t.SequenceLength > 0)
            {
                var first = t.Gates[t.GateAt(0)];
                WorldOverlay.GroundRing(Pos(first), first.Radius, Later, 16);
                WorldOverlay.Marker(Pos(first) + Vector3.up * 4f, "first", Later, edgeWhenOffscreen: false, size: 9f);
            }
            return;
        }
        if (racing && me.NextSeq < t.SequenceLength)
        {
            int seq = me.NextSeq;
            var g = t.Gates[t.GateAt(seq)];
            var pos = Pos(g);
            WorldOverlay.GroundRing(pos, g.Radius, Next, 28);
            string label = (seq + 1 >= t.SequenceLength ? "Finish" : $"Checkpoint {seq + 1}/{t.SequenceLength}") +
                           (hasCar ? "  " + GameMap.DistanceText(GameMap.FlatDistance(car, pos)) : "");
            WorldOverlay.Marker(pos + Vector3.up * 4f, label, Next, edgeWhenOffscreen: true, size: 14f);
            if (RaceConfig.ShowRoute.Value && hasCar) WorldOverlay.GroundDots(car, pos, Next, 200f);
            if (seq + 1 < t.SequenceLength)
            {
                var after = t.Gates[t.GateAt(seq + 1)];
                WorldOverlay.GroundRing(Pos(after), after.Radius, Later, 16);
                WorldOverlay.Marker(Pos(after) + Vector3.up * 4f, "then", Later, edgeWhenOffscreen: false, size: 9f);
            }
            return;
        }
        // No race for us (or before / after it): the whole track, the start in green.
        for (int i = 0; i < t.Gates.Count; i++)
        {
            var g = t.Gates[i];
            var color = i == 0 ? StartColor : Later;
            WorldOverlay.GroundRing(Pos(g), g.Radius, color, 16);
            WorldOverlay.Marker(Pos(g) + Vector3.up * 4f, GateLabel(t, i), color, edgeWhenOffscreen: i == 0 && RaceView.Live && me != null, size: i == 0 ? 12f : 9f);
        }
    }

    private static void DrawTimer(RaceTrack t, RaceView.Entry me, float now)
    {
        long ms = me.State == RacerState.Finished ? me.LastMs : (long)Mathf.Max(0f, (now - RaceView.GoAt) * 1000f);
        var r = new Rect(Screen.width - 250f, 120f, 230f, 112f);
        WorldOverlay.Panel(r, me.State == RacerState.Dnf ? Theme.Bad : me.State == RacerState.Finished ? StartColor : Next, inner =>
        {
            GUI.Label(new Rect(inner.x, inner.y, inner.width, 34f), SplitCompare.FormatTime(ms), _timer);
            float y = inner.y + 36f;
            string where = me.State == RacerState.Finished ? "Finished"
                : me.State == RacerState.Dnf ? "Out: " + SplitCompare.DnfText(me.Reason)
                : $"Checkpoint {Mathf.Min(me.NextSeq + 1, t.SequenceLength)}/{t.SequenceLength}";
            GUI.Label(new Rect(inner.x, y, inner.width, 20f), where, _line);
            if (t.Kind == RaceKind.Circuit)
                GUI.Label(new Rect(inner.x, y, inner.width, 20f), $"Lap {Mathf.Min(t.LapOf(Mathf.Min(me.NextSeq, t.SequenceLength - 1)), t.Laps)}/{t.Laps}", _lineRight);
            y += 22f;
            if (me.LastDelta.HasValue && now - me.LastAt < 6f)
            {
                long d = me.LastDelta.Value;
                var prev = _line.normal.textColor;
                _line.normal.textColor = d <= 0 ? StartColor : Theme.Bad;
                GUI.Label(new Rect(inner.x, y, inner.width, 20f), SplitCompare.FormatDelta(d) + " vs best", _line);
                _line.normal.textColor = prev;
            }
            else if (RaceView.Board?.Best != null)
                GUI.Label(new Rect(inner.x, y, inner.width, 20f), "Best " + SplitCompare.FormatTime(RaceView.Board.Best.Ms) + "  " + RaceView.Board.Best.Name, _line);
        });
    }

    private static void DrawOrder(RaceTrack t)
    {
        var list = RaceView.Ordered();
        int n = Mathf.Min(list.Count, 10);
        var r = new Rect(20f, Screen.height * 0.3f, 230f, 14f + n * 20f);
        WorldOverlay.Panel(r, Theme.Info, inner =>
        {
            for (int i = 0; i < n; i++)
            {
                var e = list[i];
                string right = e.State == RacerState.Finished ? SplitCompare.FormatTime(e.LastMs)
                    : e.State == RacerState.Dnf ? "out" : t != null ? $"{e.NextSeq}/{t.SequenceLength}" : "";
                var row = new Rect(inner.x, inner.y + i * 20f, inner.width, 20f);
                bool mine = e.Key == RaceView.MyKey;
                var prev = _line.normal.textColor;
                if (mine) _line.normal.textColor = Next;
                GUI.Label(row, $"{i + 1}. {e.Name}", _line);
                _line.normal.textColor = prev;
                GUI.Label(row, right, _lineRight);
            }
        });
    }

    private static void DrawResults()
    {
        var list = RaceView.Results;
        int n = Mathf.Min(list.Count, 12);
        var r = new Rect((Screen.width - 340f) / 2f, Screen.height * 0.36f, 340f, 44f + n * 20f);
        WorldOverlay.Panel(r, StartColor, inner =>
        {
            GUI.Label(new Rect(inner.x, inner.y, inner.width, 24f), "Results  ·  " + (RaceView.ResultsTrack ?? ""), _line);
            for (int i = 0; i < n; i++)
            {
                var s = list[i];
                var row = new Rect(inner.x, inner.y + 28f + i * 20f, inner.width, 20f);
                GUI.Label(row, (s.Place > 0 ? s.Place + ". " : "–  ") + s.Name, _line);
                GUI.Label(row, s.State == RacerState.Finished ? SplitCompare.FormatTime(s.Ms) : SplitCompare.DnfText(s.Reason), _lineRight);
            }
        });
    }

    private static void EnsureStyles()
    {
        if (_line != null) return;
        _line = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleLeft, richText = false, fontSize = 13 };
        _line.normal.textColor = Theme.Text;
        _lineRight = new GUIStyle(_line) { alignment = TextAnchor.MiddleRight };
        _timer = new GUIStyle(Theme.Title) { alignment = TextAnchor.MiddleLeft, richText = false, fontSize = 28 };
        _timer.normal.textColor = Theme.Text;
    }
}
