using System;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.UI;

/// <summary>Small driving HUD fed from the car's physics data bus (read-only, works in any session).</summary>
internal static class Hud
{
    private const float W = 250f, H = DrivingHud.Height;
    private static float _peakRpm = 6000f;

    // OnGUI runs for several events per frame; the texts are rebuilt only when the shown numbers change.
    private static readonly CachedText SpeedText = new(), GearText = new(), RpmText = new(), PowerText = new();

    private sealed class CachedText
    {
        private long _key = long.MinValue;
        public string Text = "";

        /// <summary>True when <paramref name="key"/> differs from last time (the caller then rebuilds <see cref="Text"/>).</summary>
        public bool Changed(long key)
        {
            if (key == _key) return false;
            _key = key;
            return true;
        }
    }

    public static void Draw()
    {
        // Labels and fills only paint on Repaint; skipping the other events (Layout, mouse, keys) also skips the
        // data-bus reads and string work for them.
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Repaint) return;

        var car = GameState.LocalCar;
        if (car == null) return;

        int speedRaw, rpmRaw, gear, powerRaw, torqueRaw;
        try
        {
            var d = car.data;
            speedRaw = d.Get(Channel.Vehicle, VehicleData.Speed);
            rpmRaw = d.Get(Channel.Vehicle, VehicleData.EngineRpm);
            gear = d.Get(Channel.Vehicle, VehicleData.GearboxGear);
            powerRaw = d.Get(Channel.Vehicle, VehicleData.EnginePower);
            torqueRaw = d.Get(Channel.Vehicle, VehicleData.EngineTorque);
        }
        catch (Exception ex) { DebugLog.Error("maphud", ex, "Hud.Draw"); return; }

        // Data-bus values are fixed point (x1000).
        float kmh = Mathf.Abs(speedRaw / 1000f) * 3.6f;
        float rpm = rpmRaw / 1000f;
        _peakRpm = Mathf.Max(_peakRpm * 0.9995f, rpm, 3000f);

        Theme.Ensure();
        float scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        var r = new Rect(Screen.width / scale - W - 24f, 24f, W, H);

        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.82f), 10);
        int kmhShown = Mathf.RoundToInt(kmh);
        if (SpeedText.Changed(kmhShown)) SpeedText.Text = kmhShown.ToString();
        var big = Theme.Title;
        int prevSize = big.fontSize;
        big.fontSize = 40;
        GUI.Label(new Rect(r.x + 14f, r.y + 8f, 140f, 46f), SpeedText.Text, big);
        big.fontSize = prevSize;
        GUI.Label(new Rect(r.x + 14f, r.y + 50f, 80f, 16f), "KM/H", Theme.Small);

        if (GearText.Changed(gear)) GearText.Text = gear < 0 ? "R" : gear == 0 ? "N" : gear.ToString();
        var gearStyle = Theme.Value;
        int prevGear = gearStyle.fontSize;
        gearStyle.fontSize = 34;
        GUI.Label(new Rect(r.xMax - 70f, r.y + 8f, 56f, 46f), GearText.Text, gearStyle);
        gearStyle.fontSize = prevGear;
        GUI.Label(new Rect(r.xMax - 70f, r.y + 50f, 56f, 16f), "<color=#8C96A6>GEAR</color>", Theme.Value);

        float t = Mathf.Clamp01(rpm / _peakRpm);
        var bar = new Rect(r.x + 14f, r.y + 74f, W - 28f, 10f);
        Theme.Fill(bar, Theme.Off, 5);
        Theme.Fill(new Rect(bar.x, bar.y, bar.width * t, bar.height), t > 0.9f ? Theme.Bad : t > 0.75f ? Theme.Warn : Theme.Good, 5);
        int rpmShown = Mathf.RoundToInt(rpm);
        if (RpmText.Changed(rpmShown)) RpmText.Text = rpmShown + " rpm";
        GUI.Label(new Rect(r.x + 14f, r.y + 86f, W - 28f, 18f), RpmText.Text, Theme.Small);

        int kw = Mathf.RoundToInt(powerRaw / 1000f), nm = Mathf.RoundToInt(torqueRaw / 1000f);
        if (PowerText.Changed(((long)kw << 32) | (uint)nm)) PowerText.Text = $"{kw} kW  ·  {nm} Nm";
        GUI.Label(new Rect(r.x + 14f, r.y + 106f, W - 28f, 18f), PowerText.Text, Theme.Label);
        // A line another mod may fill (core's DrivingHud.ExtraLine), e.g. a live tyre readout.
        var extra = DrivingHud.ExtraLine;
        string line = null;
        if (extra != null)
        {
            try { line = extra(); }
            catch (Exception ex) { DebugLog.Error("maphud", ex, "Hud.Draw"); line = null; }
        }
        if (!string.IsNullOrEmpty(line)) GUI.Label(new Rect(r.x + 14f, r.y + 126f, W - 28f, 18f), line, Theme.Small);
    }
}
