using UnityEngine;
using EspDraw = OverTheHillPlus.UI.OverlayDraw;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/// <summary>
/// The convoy's on-screen output: a status line (the convoy is waiting / what just happened) and, when switched on,
/// a debug overlay drawn like the ESP: each AI car's mode, speed, stuck timer and activity, its look-ahead point and
/// target, the trail, incidents and the meteorite team's slots.
/// </summary>
internal static class ConvoyOverlay
{
    private static Camera _cam;
    private static float _camAt = -100f;

    private static readonly Color Follow = new(0.30f, 0.76f, 0.54f);
    private static readonly Color WaitC = new(0.95f, 0.70f, 0.24f);
    private static readonly Color HelpC = new(0.31f, 0.66f, 0.97f);
    private static readonly Color BadC = new(0.90f, 0.33f, 0.29f);
    private static readonly Color TrailC = new(1f, 1f, 1f, 0.45f);

    public static void Draw()
    {
        if (!ConvoyAi.Active || Event.current == null || Event.current.type != EventType.Repaint) return;
        if (ConvoyFeature.ShowStatusLine.Value) StatusLine();
        if (!ConvoyFeature.ShowOverlay.Value) return;
        var cam = Cam();
        if (cam == null) return;
        if (ConvoyFeature.ShowTrail.Value) DrawTrail(cam);
        foreach (var a in ConvoyAi.Agents) DrawAgent(cam, a);
        foreach (var inc in ConvoyAi.Incidents)
        {
            if (inc.Closed || !inc.TargetAlive) continue;
            if (!Screen(cam, inc.Pos + Vector3.up * 3.2f, out var p)) continue;
            EspDraw.Diamond(p, 9f, BadC, 2f);
            EspDraw.Label(p.x, p.y + 10f, inc.Describe(), 12, BadC);
            if (!string.IsNullOrEmpty(inc.Note)) EspDraw.Label(p.x, p.y + 25f, inc.Note, 11, Color.white);
        }
        var f = ConvoyAi.Fetch;
        if (f != null && !f.Done)
        {
            if (Screen(cam, f.AttachWorld, out var mp))
            {
                EspDraw.Diamond(mp, 10f, HelpC, 2f);
                EspDraw.Label(mp.x, mp.y + 12f, "meteorite: " + f.Phase, 12, HelpC);
            }
            foreach (var s in f.Slots)
                if (Screen(cam, s + Vector3.up * 0.5f, out var sp)) EspDraw.Diamond(sp, 5f, HelpC, 1.5f);
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

    private static bool Screen(Camera cam, Vector3 world, out Vector2 p)
    {
        var sp = cam.WorldToScreenPoint(world);
        p = new Vector2(sp.x, UnityEngine.Screen.height - sp.y);
        return sp.z > 0.5f && p.x > -50f && p.x < UnityEngine.Screen.width + 50f && p.y > -50f && p.y < UnityEngine.Screen.height + 50f;
    }

    private static Color ModeColor(ConvoyAgent a) => a.Mode switch
    {
        AgentMode.Follow or AgentMode.Regroup => Follow,
        AgentMode.Wait or AgentMode.Park or AgentMode.Starting => WaitC,
        AgentMode.Help => HelpC,
        _ => BadC,
    };

    private static void DrawAgent(Camera cam, ConvoyAgent a)
    {
        if (!a.Alive) return;
        var c = ModeColor(a);
        if (Screen(cam, a.Pos + Vector3.up * 2.4f, out var head))
        {
            float y = head.y - 46f;
            y += EspDraw.Label(head.x, y, $"{a.Name} · {a.Mode} · {Mathf.Abs(a.Speed) * 3.6f:0} km/h", 13, c);
            string line2 = a.Activity ?? "";
            if (a.Stuck.IsStuck) line2 = $"{StuckMonitor.Describe(a.Stuck.Kind)} {a.Stuck.StuckFor:0.0}s · " + line2;
            else if (a.Stuck.NoMoveTime > 0.3f) line2 = $"no progress {a.Stuck.NoMoveTime:0.0}s · " + line2;
            y += EspDraw.Label(head.x, y, line2, 11, Color.white);
            string tele = $"thr {a.Cmd.Throttle:0.00} brk {a.Cmd.Brake:0.00} str {a.Cmd.Steer:0.00}" +
                          (a.Cmd.Dir < 0 ? " R" : "") + (a.Cmd.LowRange ? " LO" : "") + (a.Cmd.DiffLock ? " LOCK" : "") +
                          (a.MaxSlip > 1f ? $" slip {a.MaxSlip:0.0}" : "") + $" dmg {a.Damage01 * 100f:0}%";
            EspDraw.Label(head.x, y, tele, 10, new Color(1f, 1f, 1f, 0.75f));
        }
        if (Screen(cam, a.Pos + Vector3.up * 0.8f, out var body))
        {
            if (a.DebugLook != Vector3.zero && Screen(cam, a.DebugLook + Vector3.up * 0.5f, out var look))
            {
                EspDraw.LineShadowed(body, look, new Color(c.r, c.g, c.b, 0.85f), 1.5f);
                EspDraw.Diamond(look, 5f, c, 1.5f);
            }
            if (a.HasDebugTarget && Screen(cam, a.DebugTarget + Vector3.up * 0.5f, out var tgt))
            {
                EspDraw.Line(body, tgt, new Color(1f, 1f, 1f, 0.35f), 1f);
                EspDraw.Diamond(tgt, 7f, Color.white, 1.5f);
            }
        }
    }

    private static void DrawTrail(Camera cam)
    {
        var trail = ConvoyAi.Trail;
        if (!trail.Usable) return;
        var camPos = cam.transform.position;
        Vector2 prev = default;
        bool havePrev = false;
        int drawn = 0;
        float step = 3f;
        for (float s = trail.StartS; s <= trail.EndS && drawn < 220; s += step)
        {
            var p = trail.PointAt(s);
            if ((p - camPos).sqrMagnitude > 250f * 250f) { havePrev = false; continue; }
            if (!Screen(cam, p + Vector3.up * 0.3f, out var sp)) { havePrev = false; continue; }
            if (havePrev) { EspDraw.Line(prev, sp, TrailC, 2f); drawn++; }
            prev = sp;
            havePrev = true;
        }
    }

    private static void StatusLine()
    {
        string text = null;
        Color color = WaitC;
        if (ConvoyAi.HoldReason != null) text = "Convoy " + ConvoyAi.HoldReason;
        else if (Time.unscaledTime - ConvoyAi.LastEventAt < 6f) { text = "Convoy: " + ConvoyAi.LastEvent; color = Color.white; }
        if (text == null) return;
        EspDraw.Label(UnityEngine.Screen.width * 0.5f, 34f, text, 15, color);
    }
}
