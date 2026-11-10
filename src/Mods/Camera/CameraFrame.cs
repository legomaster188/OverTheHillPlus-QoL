using System;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.CameraPlus;

/// <summary>Camera+'s work outside the camera hook: the cycle key, a hook check, and the camera's name on screen.</summary>
internal static class CameraFrame
{
    private const float NameFor = 1.6f;

    internal static void Install()
    {
        CoreBehaviour.Hotkeys += Hotkeys;
        CoreBehaviour.AfterFeatureTicks += CameraRig.CheckHook;
        CoreBehaviour.AddAfterGui(DrawName, () => CameraConfig.ShowName.Value, repaintOnly: true);
        ExtraCameras.TurningOn += CameraRig.ExtraTurningOn;
    }

    private static void Hotkeys(Keyboard kb)
    {
        if (!CoreBehaviour.Pressed(kb, CameraConfig.CycleKey.Value)) return;
        bool back = false;
        try { back = kb.shiftKey.isPressed; } catch (Exception ex) { DebugLog.Error("camera", ex, "CameraFrame.Hotkeys"); }
        CameraRig.Cycle(back ? -1 : 1);
    }

    private static void DrawName()
    {
        if (!CameraConfig.ShowName.Value) return;
        float age = Time.unscaledTime - CameraRig.ChangedAt;
        if (age < 0f || age > NameFor) return;
        if (!UI.HudKit.HudDraw.Repaint || CameraView.HudHidden) return;
        if (!UI.HudKit.HudDraw.Begin("camera")) return;
        try
        {
            Theme.Ensure();
            var screen = HudLayout.Begin();
            string name = ExtraCameras.Active?.Name ?? CameraMath.Name(CameraConfig.Mode.Value);
            if (!ReferenceEquals(name, _nameFor))
            {
                _nameFor = name;
                _nameText = "CAMERA  ·  " + name.ToUpperInvariant();
            }
            var r = new Rect(screen.x * 0.5f - 130f, screen.y * 0.22f, 260f, 34f);
            float a = Mathf.Clamp01((NameFor - age) / 0.3f);
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, a);
            HudLayout.Panel(r);
            UI.HudKit.HudDraw.Label(r, _nameText, Theme.Label, TextAnchor.MiddleCenter);
            GUI.color = prev;
        }
        catch (Exception e) { DebugLog.Error("camera", e, "CameraFrame.DrawName"); CameraPlugin.Logger.LogDebug("Camera+ name: " + e.Message); }
        finally
        {
            HudLayout.End();
            UI.HudKit.HudDraw.End();
        }
    }

    private static string _nameFor, _nameText;
}
