using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cockpit;

/// <summary>
/// The cockpit built at runtime from Unity's own primitives (cubes and cylinders, no assets): floor, doors, pillars and
/// roof lining sized to the car's cab, a dashboard with an instrument hood, a steering wheel that turns, a speedometer
/// and a rev counter with moving needles, seven-segment readouts for speed and gear, and a 4H / 4L lamp. Laid out in
/// metres relative to the driver's eyes (x right, y up, z forward) and parented to the car at the seat, so it moves with
/// the car whatever the car's size. Built once per car per visit (about 200 small parts) and destroyed on leaving.
/// </summary>
internal sealed class CockpitModel
{
    public GameObject Root { get; private set; }

    private Transform _speedNeedle, _rpmNeedle, _wheelSpin;
    private SegDigit[] _speedDigits;
    private SegDigit _gearDigit;
    private SegDigit[] _lowDigits;
    private Renderer _lamp;
    private int _shownSpeed = -1, _shownGear = int.MinValue;
    private int _shownLow = -1;

    private sealed class SegDigit
    {
        public readonly Renderer[] Segs = new Renderer[7];
        public int Shown = -1;
        public Material On, Off;
    }

    // ---- materials ----

    private static readonly Dictionary<string, Material> Mats = new();
    private static Shader _lit, _unlit;
    private static bool _shadersTried;
    private static float _matBrightness = -1f;

    private static void FindShaders()
    {
        if (_shadersTried) return;
        _shadersTried = true;
        _lit = First("Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Standard", "Sprites/Default");
        _unlit = First("Universal Render Pipeline/Unlit", "Sprites/Default", "UI/Default") ?? _lit;
        DebugLog.Info(CockpitPlugin.ModId, $"shaders: lit {(_lit == null ? "none" : _lit.name)}, unlit {(_unlit == null ? "none" : _unlit.name)}");
    }

    private static Shader First(params string[] names)
    {
        foreach (string n in names)
        {
            try
            {
                var s = Shader.Find(n);
                if (s != null && s.isSupported) return s;
            }
            catch (Exception e) { DebugLog.Error(CockpitPlugin.ModId, e, "CockpitModel.First"); }
        }
        return null;
    }

    /// <summary>True when a shader was found (without one nothing can be drawn).</summary>
    public static bool CanDraw
    {
        get { FindShaders(); return _lit != null; }
    }

    private static Material Mat(float r, float g, float b, float smooth = 0.25f, bool glow = false)
    {
        FindShaders();
        float k = glow ? 1f : CockpitConfig.Brightness.Value;
        var c = new Color(Mathf.Clamp01(r * k), Mathf.Clamp01(g * k), Mathf.Clamp01(b * k), 1f);
        string key = $"{c.r:0.000},{c.g:0.000},{c.b:0.000},{smooth:0.00},{glow}";
        if (Mats.TryGetValue(key, out var m) && m != null) return m;
        var shader = glow ? _unlit : _lit;
        if (shader == null) return null;
        m = new Material(shader) { name = "OTH Cockpit", hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        Mats[key] = m;
        return m;
    }

    /// <summary>Throws the cached materials away when the brightness changed (they carry it in their colours).</summary>
    private static void CheckBrightness()
    {
        float b = CockpitConfig.Brightness.Value;
        if (Mathf.Abs(b - _matBrightness) < 0.001f) return;
        _matBrightness = b;
        foreach (var m in Mats.Values)
            if (m != null) Object.Destroy(m);
        Mats.Clear();
    }

    // ---- building blocks ----

    private static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Quaternion rot, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        var col = go.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col); // a car part with a collider would push the car around
        var t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = pos;
        t.localRotation = rot;
        t.localScale = scale;
        var r = go.GetComponent<Renderer>();
        if (r != null)
        {
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = true;
        }
        return go;
    }

    private static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 size, Material mat) =>
        Prim(PrimitiveType.Cube, name, parent, pos, size, Quaternion.identity, mat);

    /// <summary>A bar from a to b: width across and depth up (when it runs along a floor-parallel line), length between.</summary>
    private static GameObject Beam(Transform parent, string name, Vector3 a, Vector3 b, float width, float depth, Material mat)
    {
        var d = b - a;
        float len = d.magnitude;
        if (len < 0.001f) return null;
        return Prim(PrimitiveType.Cube, name, parent, (a + b) * 0.5f, new Vector3(width, depth, len), Quaternion.LookRotation(d / len, Vector3.up), mat);
    }

    /// <summary>A flat disc facing the driver (a cylinder turned so its cap looks back along -z).</summary>
    private static GameObject Disc(Transform parent, string name, Vector3 pos, float diameter, float thickness, Material mat) =>
        Prim(PrimitiveType.Cylinder, name, parent, pos, new Vector3(diameter, thickness * 0.5f, diameter), Quaternion.Euler(-90f, 0f, 0f), mat);

    private static GameObject Pivot(Transform parent, string name, Vector3 pos, Quaternion rot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = rot;
        return go;
    }

    // ---- the cockpit ----

    public static CockpitModel Build(Transform car, SeatPlan p)
    {
        CheckBrightness();
        FindShaders();
        var m = new CockpitModel();
        var root = new GameObject("OTH_Cockpit");
        root.SetActive(false); // inactive while it is built, so nothing of it exists for a frame half-done
        root.transform.SetParent(car, false);
        root.transform.localPosition = new Vector3(p.EyeX, p.EyeY, p.EyeZ);
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        m.Root = root;
        m.BuildCab(root.transform, p);
        m.BuildDash(root.transform, p);
        m.BuildWheel(root.transform);
        root.SetActive(true);
        return m;
    }

    public void Destroy()
    {
        if (Root != null)
        {
            Root.SetActive(false); // Destroy happens at the end of the frame: until then it must not be seen as part of the car
            Object.Destroy(Root);
        }
        Root = null;
    }

    public void SetVisible(bool on)
    {
        if (Root != null && Root.activeSelf != on) Root.SetActive(on);
    }

    private void BuildCab(Transform t, SeatPlan p)
    {
        float cx = p.CentreX, hw = p.HalfWidth, roof = p.RoofY, floor = p.FloorY;
        const float belt = -0.20f;
        var carpet = Mat(0.07f, 0.065f, 0.06f, 0.05f);
        var liner = Mat(0.30f, 0.29f, 0.26f, 0.1f);
        var trim = Mat(0.085f, 0.085f, 0.09f, 0.3f);
        var door = Mat(0.14f, 0.13f, 0.12f, 0.2f);
        var sill = Mat(0.2f, 0.19f, 0.17f, 0.35f);
        var mirror = Mat(0.11f, 0.13f, 0.15f, 0.9f);

        Box(t, "Floor", new Vector3(cx, floor, 0f), new Vector3(2f * hw, 0.04f, 2.6f), carpet);
        Box(t, "Roof lining", new Vector3(cx, roof + 0.015f, -0.2f), new Vector3(2f * hw, 0.03f, 1.7f), liner);
        Box(t, "Rear wall", new Vector3(cx, (belt + floor) * 0.5f, -1.0f), new Vector3(2f * hw, belt - floor, 0.05f), door);

        foreach (float s in new[] { -1f, 1f })
        {
            float x = cx + s * (hw - 0.02f);
            string side = s < 0f ? "L" : "R";
            // A pillar: from the dashboard's far edge, leaning back to the roof.
            Beam(t, "A pillar " + side, new Vector3(cx + s * (hw - 0.04f), -SeatMath.DashDrop, SeatMath.DashDepth),
                new Vector3(cx + s * (hw - 0.07f), roof, 0.64f), 0.075f, 0.07f, trim);
            // Roof rail, B and C pillars, door and sill.
            Beam(t, "Roof rail " + side, new Vector3(cx + s * (hw - 0.04f), roof - 0.025f, 0.64f), new Vector3(cx + s * (hw - 0.04f), roof - 0.025f, -1f), 0.07f, 0.05f, trim);
            Box(t, "B pillar " + side, new Vector3(x, (belt + roof) * 0.5f, -0.45f), new Vector3(0.05f, roof - belt, 0.1f), trim);
            Box(t, "C pillar " + side, new Vector3(x, (belt + roof) * 0.5f, -0.97f), new Vector3(0.05f, roof - belt, 0.14f), trim);
            Box(t, "Door " + side, new Vector3(x, (belt + floor) * 0.5f, 0.05f), new Vector3(0.05f, belt - floor, 2f), door);
            Box(t, "Door sill " + side, new Vector3(x - s * 0.02f, belt + 0.005f, 0f), new Vector3(0.09f, 0.03f, 1.9f), sill);
        }

        // Windscreen header and the rear-view mirror hanging from it (on the car's centre line, not the driver's).
        Beam(t, "Header", new Vector3(cx - hw + 0.05f, roof - 0.035f, 0.64f), new Vector3(cx + hw - 0.05f, roof - 0.035f, 0.64f), 0.1f, 0.07f, trim);
        Box(t, "Mirror stem", new Vector3(cx, roof - 0.085f, 0.62f), new Vector3(0.02f, 0.07f, 0.02f), trim);
        Box(t, "Mirror", new Vector3(cx, roof - 0.14f, 0.6f), new Vector3(0.2f, 0.06f, 0.025f), trim);
        Box(t, "Mirror glass", new Vector3(cx, roof - 0.14f, 0.585f), new Vector3(0.185f, 0.047f, 0.005f), mirror);
    }

    private void BuildDash(Transform t, SeatPlan p)
    {
        float cx = p.CentreX, hw = p.HalfWidth;
        var pad = Mat(0.10f, 0.10f, 0.11f, 0.35f);
        var fascia = Mat(0.075f, 0.075f, 0.085f, 0.25f);
        var dark = Mat(0.035f, 0.035f, 0.04f, 0.3f);
        var accent = Mat(0.9f, 0.38f, 0.08f, 0.4f);
        var hood = Mat(0.05f, 0.05f, 0.055f, 0.3f);

        Box(t, "Dash top", new Vector3(cx, -0.31f, 0.78f), new Vector3(2f * hw, 0.06f, 0.5f), pad);
        Box(t, "Fascia", new Vector3(cx, -0.6f, 0.515f), new Vector3(2f * hw, 0.56f, 0.04f), fascia);
        Box(t, "Dash stripe", new Vector3(cx, -0.325f, 0.497f), new Vector3(2f * hw * 0.98f, 0.012f, 0.012f), accent);
        Box(t, "Glovebox", new Vector3(cx + hw * 0.5f, -0.56f, 0.493f), new Vector3(Mathf.Min(0.42f, hw * 0.7f), 0.2f, 0.012f), dark);
        for (int i = -1; i <= 1; i++)
            Box(t, "Vent", new Vector3(cx + i * 0.11f, -0.45f, 0.493f), new Vector3(0.075f, 0.035f, 0.012f), dark);

        // The instrument hood: the gauges sit in its front face, which looks back at the driver.
        Box(t, "Instrument hood", new Vector3(0f, -0.22f, 0.68f), new Vector3(0.62f, 0.17f, 0.26f), hood);
        Box(t, "Hood lip", new Vector3(0f, -0.128f, 0.58f), new Vector3(0.64f, 0.012f, 0.07f), hood);
        const float face = 0.55f;
        const float r = 0.08f;
        _speedNeedle = BuildGauge(t, "Speedometer", new Vector3(-0.13f, -0.22f, face), r, 12, 2, 0.0f).transform;
        _rpmNeedle = BuildGauge(t, "Tachometer", new Vector3(0.13f, -0.22f, face), r, 8, 1, 0.8f).transform;

        // Speed (3 digits) and gear (1) in the dials' lower halves.
        float dh = r * 0.36f, dw = dh * 0.55f * 1.45f;
        _speedDigits = new SegDigit[3];
        for (int i = 0; i < 3; i++)
            _speedDigits[i] = MakeDigit(t, new Vector3(-0.13f + (i - 1) * dw, -0.22f - r * 0.42f, face - 0.011f), dh, 0.9f, 0.82f, 0.62f);
        _gearDigit = MakeDigit(t, new Vector3(0.13f, -0.22f - r * 0.40f, face - 0.011f), dh * 1.2f, 0.9f, 0.82f, 0.62f);

        // 4H / 4L: two characters and a lamp beside the dials.
        _lowDigits = new SegDigit[2];
        for (int i = 0; i < 2; i++)
            _lowDigits[i] = MakeDigit(t, new Vector3(0.262f + (i - 0.5f) * 0.026f, -0.235f, face - 0.004f), 0.03f, 0.95f, 0.95f, 0.9f);
        var lamp = Box(t, "4WD lamp", new Vector3(0.262f, -0.19f, face - 0.004f), new Vector3(0.026f, 0.012f, 0.004f), Mat(0.1f, 0.5f, 0.15f, 0.2f, true));
        _lamp = lamp.GetComponent<Renderer>();
        _shownLow = -1;
    }

    /// <summary>
    /// A round gauge facing the driver at <paramref name="at"/>, ticks round its scale (every <paramref name="major"/>th
    /// longer, those from <paramref name="redFrom"/> of the scale red when above 0), and a needle; returns the needle's
    /// pivot, which is turned by setting its z rotation.
    /// </summary>
    private static GameObject BuildGauge(Transform t, string name, Vector3 at, float radius, int ticks, int major, float redFrom)
    {
        var g = Pivot(t, name, at, Quaternion.identity).transform;
        Disc(g, "Bezel", Vector3.zero, radius * 2f + 0.014f, 0.012f, Mat(0.17f, 0.17f, 0.19f, 0.6f));
        Disc(g, "Face", new Vector3(0f, 0f, -0.006f), radius * 2f, 0.006f, Mat(0.018f, 0.018f, 0.022f, 0.1f));
        var white = Mat(0.92f, 0.92f, 0.9f, 0.1f, true);
        var red = Mat(0.95f, 0.15f, 0.1f, 0.1f, true);
        for (int i = 0; i <= ticks; i++)
        {
            float a = DialMath.TickAngle(i, ticks);
            bool big = major <= 1 || i % major == 0;
            bool isRed = redFrom > 0f && i >= Mathf.Ceil(ticks * redFrom);
            float len = big ? radius * 0.16f : radius * 0.09f, wid = big ? 0.0045f : 0.0028f;
            float rad = radius * 0.86f - len * 0.5f;
            float rx = Mathf.Sin(a * Mathf.Deg2Rad), ry = Mathf.Cos(a * Mathf.Deg2Rad);
            Prim(PrimitiveType.Cube, "Tick", g, new Vector3(rx * rad, ry * rad, -0.0095f), new Vector3(wid, len, 0.002f), Quaternion.Euler(0f, 0f, -a), isRed ? red : white);
        }
        if (redFrom > 0f)
        {
            // The rev counter's redline arc: a row of red dashes just inside the ticks.
            int steps = 10;
            float start = DialMath.TickAngle((int)Mathf.Ceil(ticks * redFrom), ticks);
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(start, DialMath.StartDeg + DialMath.SweepDeg, i / (float)steps);
                float rad = radius * 0.64f;
                Prim(PrimitiveType.Cube, "Redline", g, new Vector3(Mathf.Sin(a * Mathf.Deg2Rad) * rad, Mathf.Cos(a * Mathf.Deg2Rad) * rad, -0.0095f),
                    new Vector3(0.0035f, radius * 0.07f, 0.002f), Quaternion.Euler(0f, 0f, -a), red);
            }
        }
        var needle = Pivot(g, "Needle", new Vector3(0f, 0f, -0.0125f), Quaternion.Euler(0f, 0f, -DialMath.StartDeg));
        Prim(PrimitiveType.Cube, "Needle blade", needle.transform, new Vector3(0f, radius * 0.33f, 0f), new Vector3(0.0042f, radius * 0.9f, 0.002f), Quaternion.identity,
            Mat(1f, 0.42f, 0.08f, 0.2f, true));
        Disc(needle.transform, "Needle hub", new Vector3(0f, 0f, -0.002f), radius * 0.2f, 0.004f, Mat(0.06f, 0.06f, 0.065f, 0.7f));
        return needle;
    }

    private void BuildWheel(Transform t)
    {
        var tilt = Pivot(t, "Wheel", new Vector3(0f, -0.47f, 0.36f), Quaternion.Euler(28f, 0f, 0f));
        var spin = Pivot(tilt.transform, "Wheel spin", Vector3.zero, Quaternion.identity);
        _wheelSpin = spin.transform;
        var leather = Mat(0.04f, 0.04f, 0.045f, 0.45f);
        var stripe = Mat(0.95f, 0.8f, 0.2f, 0.3f);
        var spoke = Mat(0.06f, 0.06f, 0.065f, 0.4f);
        const float R = 0.19f;
        const int N = 32;
        float chord = 2f * Mathf.PI * R / N * 1.12f;
        for (int i = 0; i < N; i++)
        {
            float a = i * 360f / N;
            float rx = Mathf.Cos(a * Mathf.Deg2Rad) * R, ry = Mathf.Sin(a * Mathf.Deg2Rad) * R;
            // i = N/4 is straight up: a yellow centre mark so the turn can be seen.
            Prim(PrimitiveType.Cube, "Rim", _wheelSpin, new Vector3(rx, ry, 0f), new Vector3(chord, 0.03f, 0.03f), Quaternion.Euler(0f, 0f, a + 90f), i == N / 4 ? stripe : leather);
        }
        Box(_wheelSpin, "Spoke L", new Vector3(-R * 0.5f, -0.012f, 0f), new Vector3(R, 0.026f, 0.02f), spoke);
        Box(_wheelSpin, "Spoke R", new Vector3(R * 0.5f, -0.012f, 0f), new Vector3(R, 0.026f, 0.02f), spoke);
        Box(_wheelSpin, "Spoke B", new Vector3(0f, -R * 0.5f, 0f), new Vector3(0.026f, R, 0.02f), spoke);
        Prim(PrimitiveType.Cylinder, "Hub", _wheelSpin, new Vector3(0f, 0f, -0.012f), new Vector3(0.1f, 0.018f, 0.1f), Quaternion.Euler(-90f, 0f, 0f), Mat(0.07f, 0.07f, 0.075f, 0.6f));
        Prim(PrimitiveType.Cylinder, "Hub cap", _wheelSpin, new Vector3(0f, 0f, -0.03f), new Vector3(0.05f, 0.006f, 0.05f), Quaternion.Euler(-90f, 0f, 0f), Mat(0.5f, 0.5f, 0.52f, 0.8f));
        // The column, which does not turn.
        Prim(PrimitiveType.Cube, "Column", t, new Vector3(0f, -0.545f, 0.44f), new Vector3(0.07f, 0.07f, 0.22f), Quaternion.Euler(43f, 0f, 0f), Mat(0.04f, 0.04f, 0.045f, 0.3f));
    }

    // ---- seven-segment displays ----

    private static SegDigit MakeDigit(Transform parent, Vector3 centre, float h, float onR, float onG, float onB)
    {
        var d = new SegDigit
        {
            On = Mat(onR, onG, onB, 0.1f, true),
            Off = Mat(0.05f * onR, 0.05f * onG, 0.05f * onB, 0.1f, true),
        };
        float w = h * 0.55f, th = h * 0.13f;
        float hl = w - th, vl = h * 0.5f - th * 1.1f;
        // a, b, c, d, e, f, g
        Vector3[] pos =
        {
            new(0f, h * 0.5f - th * 0.5f, 0f), new(w * 0.5f - th * 0.5f, h * 0.25f, 0f), new(w * 0.5f - th * 0.5f, -h * 0.25f, 0f),
            new(0f, -h * 0.5f + th * 0.5f, 0f), new(-w * 0.5f + th * 0.5f, -h * 0.25f, 0f), new(-w * 0.5f + th * 0.5f, h * 0.25f, 0f), Vector3.zero,
        };
        bool[] horizontal = { true, false, false, true, false, false, true };
        for (int i = 0; i < 7; i++)
        {
            var seg = Box(parent, "Seg", centre + pos[i], horizontal[i] ? new Vector3(hl, th, 0.002f) : new Vector3(th, vl, 0.002f), d.Off);
            d.Segs[i] = seg.GetComponent<Renderer>();
        }
        return d;
    }

    private static void Show(SegDigit d, char ch)
    {
        int mask = DialMath.Segments(ch);
        if (d.Shown == mask) return;
        d.Shown = mask;
        for (int i = 0; i < 7; i++)
            if (d.Segs[i] != null) d.Segs[i].sharedMaterial = (mask & (1 << i)) != 0 ? d.On : d.Off;
    }

    // ---- per frame ----

    /// <summary>Moves the needles and wheel and updates the displays. Angles in degrees clockwise from 12 o'clock.</summary>
    public void Update(float speedAngle, float rpmAngle, float wheelDeg, int shownSpeed, int gear, bool lowRange)
    {
        if (Root == null) return;
        if (_speedNeedle != null) _speedNeedle.localRotation = Quaternion.Euler(0f, 0f, -speedAngle);
        if (_rpmNeedle != null) _rpmNeedle.localRotation = Quaternion.Euler(0f, 0f, -rpmAngle);
        if (_wheelSpin != null) _wheelSpin.localRotation = Quaternion.Euler(0f, 0f, -wheelDeg);
        if (shownSpeed != _shownSpeed)
        {
            _shownSpeed = shownSpeed;
            string s = DialMath.Digits(shownSpeed, _speedDigits.Length);
            for (int i = 0; i < _speedDigits.Length; i++) Show(_speedDigits[i], s[i]);
        }
        if (gear != _shownGear)
        {
            _shownGear = gear;
            Show(_gearDigit, DialMath.GearChar(gear));
        }
        int low = lowRange ? 1 : 0;
        if (low != _shownLow)
        {
            _shownLow = low;
            Show(_lowDigits[0], '4');
            Show(_lowDigits[1], lowRange ? 'L' : 'H');
            // Green for high range, amber for low.
            if (_lamp != null) _lamp.sharedMaterial = lowRange ? Mat(1f, 0.6f, 0.05f, 0.2f, true) : Mat(0.1f, 0.5f, 0.15f, 0.2f, true);
        }
    }
}
