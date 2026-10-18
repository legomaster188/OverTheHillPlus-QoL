using System;
using System.Collections.Generic;
using HarmonyLib;
using OverTheHillPlus.ServerBrowser;
using OverTheHillPlus.ServerCreator;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Public.ServerPlayers;

/// <summary>
/// Browser side: a small "who's in" panel for the selected server under the game's area polaroid, and a tooltip
/// with the names when the pointer rests on a server row. Display only (no buttons), so it never takes input from
/// the game's browser.
/// </summary>
internal static class BrowserPlayersPanel
{
    private const float PanelW = 260f;
    private const int PanelLines = 10, TooltipLines = 8;

    private sealed class Row
    {
        public RectTransform Root;
        public OverlandSessionInfo Info;
    }

    // Row views the list adapter has bound, by native pointer (the adapter recycles ~15 views while scrolling).
    private static readonly Dictionary<IntPtr, Row> Rows = new();
    private static Camera _rowCamera;
    private static bool _rowCameraKnown;

    private static OverlandSessionInfo _hovered;
    private static float _nextHoverCheck;
    private static Rect _polaroid; // screen pixels, top-left origin; zero = not found
    private static float _nextPolaroid;
    private static GUIStyle _line, _header, _tip;
    private static bool _loggedError, _loggedTrackError;

    private static bool MenuOpen => Time.frameCount - Menu.DrawnFrame <= 2;

    // ---------------------------------------------------------------- row tracking

    internal static void Track(SessionInfoListAdapter adapter, SessionInfoItemViewsHolder vh)
    {
        try
        {
            var data = adapter?.Data;
            if (data == null || vh == null) return;
            int idx = vh.ItemIndex;
            if (idx < 0 || idx >= data.Count) return;
            var root = vh.root;
            if (root == null) return;
            if (Rows.Count > 64) Rows.Clear(); // only the live views matter; they re-register on their next bind
            var key = root.Pointer;
            if (!Rows.TryGetValue(key, out var row)) Rows[key] = row = new Row { Root = root };
            row.Info = data[idx];
            if (!_rowCameraKnown)
            {
                _rowCameraKnown = true;
                _rowCamera = CameraFor(root);
            }
        }
        catch (Exception e)
        {
            DebugLog.Error("servercreator", e, "BrowserPlayersPanel.Track");
            if (_loggedTrackError) return;
            _loggedTrackError = true;
            ServerCreatorPlugin.Logger.LogWarning("[ServerPlayers] row tracking failed (further errors suppressed): " + e.Message);
        }
    }

    internal static void OnBrowserClosed()
    {
        Rows.Clear();
        _hovered = null;
        _rowCameraKnown = false;
        _rowCamera = null;
        _polaroid = default;
    }

    /// <summary>The camera that renders a UI element's canvas (null for a screen-space overlay canvas).</summary>
    private static Camera CameraFor(Component c)
    {
        try
        {
            var canvas = c.GetComponentInParent<Canvas>();
            if (canvas == null) return null;
            var rootCanvas = canvas.rootCanvas ?? canvas;
            return rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : rootCanvas.worldCamera;
        }
        catch (Exception ex) { DebugLog.Error("servercreator", ex, "BrowserPlayersPanel.CameraFor"); return null; }
    }

    // ---------------------------------------------------------------- per frame

    private static bool Visible => BrowserScreen.Open && !MenuOpen;

    private static void UpdateHover(float now)
    {
        if (now < _nextHoverCheck) return;
        _nextHoverCheck = now + 0.1f;
        _hovered = null;
        if (!ServerPlayersFeature.ShowTooltip.Value || Rows.Count == 0) return;
        var mouse = Mouse.current;
        if (mouse == null) return;
        Vector2 pos = mouse.position.ReadValue();
        List<IntPtr> dead = null;
        foreach (var kv in Rows)
        {
            var row = kv.Value;
            try
            {
                if (row.Root == null) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                if (!row.Root.gameObject.activeInHierarchy) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(row.Root, pos, _rowCamera))
                {
                    _hovered = row.Info;
                    break;
                }
            }
            catch (Exception ex) { DebugLog.Error("servercreator", ex, "BrowserPlayersPanel.UpdateHover"); (dead ??= new List<IntPtr>()).Add(kv.Key); }
        }
        if (dead != null) foreach (var k in dead) Rows.Remove(k);
    }

    /// <summary>Where the game's area polaroid is on screen (refreshed four times a second).</summary>
    private static void UpdatePolaroid(float now)
    {
        if (now < _nextPolaroid) return;
        _nextPolaroid = now + 0.25f;
        _polaroid = default;
        try
        {
            var list = BrowserScreen.Current?.ui?.ContextUIServerList;
            if (list == null || !list.gameObject.activeInHierarchy) { _polaroid = new Rect(-1f, -1f, 0f, 0f); return; } // other tab
            var pol = list.AreaPolaroid;
            if (pol == null || !pol.gameObject.activeInHierarchy) return;
            var rt = pol.transform.TryCast<RectTransform>();
            if (rt == null) return;
            var cam = CameraFor(pol);
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            rt.GetWorldCorners(corners);
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                var p = RectTransformUtility.WorldToScreenPoint(cam, corners[i]);
                xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x);
                yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y);
            }
            if (xMax - xMin < 4f || yMax - yMin < 4f) return;
            _polaroid = new Rect(xMin, Screen.height - yMax, xMax - xMin, yMax - yMin);
        }
        catch (Exception ex) { DebugLog.Error("servercreator", ex, "BrowserPlayersPanel.UpdatePolaroid"); _polaroid = default; }
    }

    // ---------------------------------------------------------------- drawing

    internal static void Draw()
    {
        if (!Visible) { _hovered = null; return; }
        bool panel = ServerPlayersFeature.ShowPanel.Value, tip = ServerPlayersFeature.ShowTooltip.Value;
        if (!panel && !tip) return;
        var prevMatrix = GUI.matrix;
        try
        {
            float now = Time.unscaledTime;
            if (Event.current.type == EventType.Repaint)
            {
                UpdateHover(now);
                if (panel) UpdatePolaroid(now);
            }
            Theme.Ensure();
            EnsureStyles();
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            float sw = Screen.width / scale, sh = Screen.height / scale;

            if (panel && _polaroid.x >= 0f) DrawPanel(now, scale, sw, sh);
            if (tip && _hovered != null) DrawTooltip(now, scale, sw, sh);
        }
        catch (Exception e)
        {
            if (!_loggedError)
            {
                _loggedError = true;
                ServerCreatorPlugin.Logger.LogError("[ServerPlayers] browser panel failed (further errors suppressed): " + e);
            }
        }
        finally { GUI.matrix = prevMatrix; }
    }

    private static void DrawPanel(float now, float scale, float sw, float sh)
    {
        var sel = BrowserScreen.SelectedSession();
        var s = sel?.FusionSessionInfo;
        if (s == null) return;
        var lines = Lines(s, now, PanelLines, out string header);

        float lineH = 18f;
        float h = 30f + lines.Count * lineH + 8f;
        float x, y, w = PanelW;
        if (_polaroid.width > 0f)
        {
            var p = new Rect(_polaroid.x / scale, _polaroid.y / scale, _polaroid.width / scale, _polaroid.height / scale);
            w = Mathf.Clamp(p.width, 200f, 340f);
            x = p.x + (p.width - w) / 2f;
            y = p.yMax + 8f;
            if (y + h > sh - 8f) y = Mathf.Max(8f, p.y - h - 8f); // no room below: above it
        }
        else
        {
            // Polaroid not found (layout changed or hidden): right edge, middle of the screen.
            x = sw - w - 24f;
            y = sh * 0.55f;
        }
        x = Mathf.Clamp(x, 0f, Mathf.Max(0f, sw - w));
        y = Mathf.Clamp(y, 0f, Mathf.Max(0f, sh - h));
        var r = new Rect(x, y, w, h);
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.9f), 8);
        Theme.Fill(new Rect(r.x, r.y, 3f, r.height), Theme.Accent, 2);
        GUI.Label(new Rect(r.x + 12f, r.y + 6f, w - 20f, 20f), header, _header);
        float ly = r.y + 28f;
        foreach (var (text, muted) in lines)
        {
            _line.normal.textColor = muted ? Theme.Muted : Theme.Text;
            GUI.Label(new Rect(r.x + 12f, ly, w - 20f, lineH), text, _line);
            ly += lineH;
        }
    }

    private static void DrawTooltip(float now, float scale, float sw, float sh)
    {
        var s = _hovered.FusionSessionInfo;
        if (s == null) return;
        var lines = Lines(s, now, TooltipLines, out string header);
        var mouse = Mouse.current;
        if (mouse == null) return;
        Vector2 m = mouse.position.ReadValue();
        float mx = m.x / scale, my = (Screen.height - m.y) / scale;

        const float w = 240f, lineH = 17f;
        float h = 26f + lines.Count * lineH + 6f;
        float x = mx + 18f, y = my + 14f;
        if (x + w > sw - 4f) x = mx - w - 12f;
        if (y + h > sh - 4f) y = my - h - 8f;
        x = Mathf.Clamp(x, 0f, Mathf.Max(0f, sw - w));
        y = Mathf.Clamp(y, 0f, Mathf.Max(0f, sh - h));
        var r = new Rect(x, y, w, h);
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.95f), 6);
        GUI.Label(new Rect(r.x + 10f, r.y + 4f, w - 16f, 18f), header, _header);
        float ly = r.y + 24f;
        foreach (var (text, muted) in lines)
        {
            _tip.normal.textColor = muted ? Theme.Muted : Theme.Text;
            GUI.Label(new Rect(r.x + 10f, ly, w - 16f, lineH), text, _tip);
            ly += lineH;
        }
    }

    private static readonly List<(string text, bool muted)> Scratch = new();

    /// <summary>Header and text lines describing who is in a session (at most <paramref name="max"/> lines).</summary>
    private static List<(string text, bool muted)> Lines(Fusion.SessionInfo s, float now, int max, out string header)
    {
        Scratch.Clear();
        int count = 0, slots = 0;
        try { count = s.PlayerCount; slots = s.MaxPlayers; } catch (Exception ex) { DebugLog.Error("servercreator", ex, "BrowserPlayersPanel"); }
        header = $"IN THIS SERVER  {count}/{slots}";
        var names = PlayerNamesProperty.For(s, now, out bool present);
        if (!present) { Scratch.Add(("names unavailable (host not modded)", true)); return Scratch; }
        if (names == null) { Scratch.Add(("names unavailable (unknown format)", true)); return Scratch; }
        if (names.Hidden) { Scratch.Add(("the host keeps player names private", true)); return Scratch; }
        if (names.List.Count == 0)
        {
            Scratch.Add((count > 0 ? "names coming (host updates every few s)" : "nobody yet", true));
            return Scratch;
        }
        int shown = Math.Min(names.List.Count, names.Total > max ? max - 1 : max);
        for (int i = 0; i < shown; i++)
            Scratch.Add((i == names.HostIndex ? names.List[i] + "  (host)" : names.List[i], false));
        int rest = names.Total - shown;
        if (rest > 0) Scratch.Add(($"+{rest} more", true));
        return Scratch;
    }

    private static void EnsureStyles()
    {
        if (_line != null && _header != null && _tip != null) return;
        // Names are cleaned (no tags), but rich text stays off for them anyway.
        _line = new GUIStyle(Theme.Label) { richText = false, fontSize = 13, clipping = TextClipping.Clip };
        _tip = new GUIStyle(Theme.Label) { richText = false, fontSize = 12, clipping = TextClipping.Clip };
        _header = new GUIStyle(Theme.Section) { richText = false };
        _header.normal.textColor = Theme.Accent;
    }
}

[HarmonyPatch]
internal static class BrowserPlayersPanelPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.UpdateViewsHolder))]
    private static void RowBound(SessionInfoListAdapter __instance, SessionInfoItemViewsHolder newOrRecycled) =>
        BrowserPlayersPanel.Track(__instance, newOrRecycled);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.Close))]
    private static void Closed() => BrowserPlayersPanel.OnBrowserClosed();
}
