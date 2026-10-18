using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Shared;

/// <summary>
/// Extra pins and dotted route lines on the game's map screen, fed by mods (pins from Waypoints+, checkpoints from
/// Race Mode), so each mod only says what to show. Purely local and visual.
///
/// <para>How: the first <see cref="Set"/> or <see cref="SetRoute"/> installs postfixes on MapView.UpdatePlayerIndicators
/// (per frame while the map is open; no struct parameters), UpdatePlayersNameZoom (to keep dots the same size when
/// zooming) and OnDestroy. Pins are clones of the game's own map pin icon (label and colour set through
/// PinpointMapIcon), placed with MapView.WorldToUIPosition the way the game places its pins. Route dots are small UI
/// images under the same parent, along the straight line between the two spots.</para>
/// </summary>
internal static class MapMarkers
{
    public struct Spot
    {
        public Vector3 Pos;
        public string Label;
        public Color Color;

        public Spot(Vector3 pos, string label, Color color) { Pos = pos; Label = label; Color = color; }
    }

    private struct Route
    {
        public Vector3 From, To;
        public Color Color;
    }

    private sealed class PinClone
    {
        public GameObject Root;
        public RectTransform Rect;
        public PinpointMapIcon Icon;
        public string Label;
        public Color Color;
        public bool Shown;
    }

    private sealed class ViewState
    {
        public MapView View;
        public readonly List<PinClone> Pins = new();
        public readonly List<Image> Dots = new();
        public bool Broken;
    }

    private const int MaxSpots = 96, MaxDots = 240;
    private const float DotSpacing = 14f, DotSize = 5f;

    private static readonly Dictionary<string, List<Spot>> SpotsByOwner = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Route> Routes = new(StringComparer.Ordinal);
    private static readonly Dictionary<IntPtr, ViewState> Views = new();
    private static readonly List<Spot> Flat = new();
    private static readonly HashSet<string> Logged = new();
    private static bool _installed, _installFailed, _dirty = true;
    private static float _zoom = 1f;

    /// <summary>Replaces the pins <paramref name="owner"/> shows on the map (empty = none).</summary>
    public static void Set(string owner, IReadOnlyList<Spot> spots)
    {
        if (string.IsNullOrEmpty(owner)) return;
        EnsureInstalled();
        if (spots == null || spots.Count == 0)
        {
            if (SpotsByOwner.Remove(owner)) _dirty = true;
            return;
        }
        if (!SpotsByOwner.TryGetValue(owner, out var list)) SpotsByOwner[owner] = list = new List<Spot>();
        list.Clear();
        for (int i = 0; i < spots.Count; i++) list.Add(spots[i]);
        _dirty = true;
    }

    /// <summary>Shows (or with <paramref name="on"/> false, hides) <paramref name="owner"/>'s dotted route.</summary>
    public static void SetRoute(string owner, bool on, Vector3 from, Vector3 to, Color color)
    {
        if (string.IsNullOrEmpty(owner)) return;
        EnsureInstalled();
        if (!on) { Routes.Remove(owner); return; }
        Routes[owner] = new Route { From = from, To = to, Color = color };
    }

    // ---------------------------------------------------------------- hooks

    private static void EnsureInstalled()
    {
        if (_installed || _installFailed) return;
        _installed = true;
        try
        {
            var h = new Harmony("com.oth.core.mapmarkers");
            Patch(h, nameof(MapView.UpdatePlayerIndicators), nameof(AfterUpdatePlayerIndicators));
            Patch(h, nameof(MapView.UpdatePlayersNameZoom), nameof(AfterUpdatePlayersNameZoom));
            Patch(h, nameof(MapView.OnDestroy), nameof(AfterOnDestroy));
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "MapMarkers.EnsureInstalled");
            _installFailed = true;
            Core.CorePlugin.Logger?.LogWarning("[Map] map markers could not hook the map: " + e.Message);
        }
    }

    private static void Patch(Harmony h, string target, string postfix)
    {
        var original = AccessTools.Method(typeof(MapView), target);
        if (original == null) { Core.CorePlugin.Logger?.LogWarning($"[Map] MapView.{target} not found: mod pins can't be shown on the map"); return; }
        h.Patch(original, postfix: new HarmonyMethod(typeof(MapMarkers), postfix));
    }

    private static void AfterUpdatePlayerIndicators(MapView __instance)
    {
        try { Update(__instance); }
        catch (Exception e) { DebugLog.Error("core", e, "MapMarkers.AfterUpdatePlayerIndicators"); LogOnce("update", e); }
    }

    private static void AfterUpdatePlayersNameZoom(float currentZoom)
    {
        if (currentZoom > 0.0001f && float.IsFinite(currentZoom)) _zoom = currentZoom;
    }

    private static void AfterOnDestroy(MapView __instance)
    {
        try { Views.Remove(__instance.Pointer); } catch (Exception ex) { DebugLog.Error("core", ex, "MapMarkers.AfterOnDestroy"); }
    }

    // ---------------------------------------------------------------- per frame

    private static void Update(MapView view)
    {
        if (view == null) return;
        if (!Views.TryGetValue(view.Pointer, out var st))
        {
            PruneViews();
            st = new ViewState { View = view };
            Views[view.Pointer] = st;
        }
        if (st.Broken) return;

        if (_dirty)
        {
            _dirty = false;
            Flat.Clear();
            foreach (var list in SpotsByOwner.Values)
                foreach (var s in list)
                    if (Flat.Count < MaxSpots) Flat.Add(s);
        }

        // Pins
        int used = 0;
        for (int i = 0; i < Flat.Count; i++)
        {
            var s = Flat[i];
            var pin = PinAt(st, used);
            if (pin == null) break;
            used++;
            string label = s.Label ?? "";
            if (pin.Label != label) { pin.Label = label; try { pin.Icon?.SetLabelText(label); } catch (Exception ex) { DebugLog.Error("core", ex, "MapMarkers.Update"); } }
            if (pin.Color != s.Color) { pin.Color = s.Color; try { pin.Icon?.SetColor(s.Color); } catch (Exception ex) { DebugLog.Error("core", ex, "MapMarkers.Update"); } }
            pin.Rect.anchoredPosition = view.WorldToUIPosition(s.Pos);
            Show(pin, true);
        }
        for (int i = used; i < st.Pins.Count; i++) Show(st.Pins[i], false);

        // Route dots
        int dots = 0;
        float scale = 1f / Mathf.Max(0.05f, _zoom);
        foreach (var r in Routes.Values)
        {
            Vector2 a = view.WorldToUIPosition(r.From), b = view.WorldToUIPosition(r.To);
            float len = Vector2.Distance(a, b);
            float step = DotSpacing * scale;
            int n = Mathf.Clamp(Mathf.FloorToInt(len / Mathf.Max(0.5f, step)), 1, MaxDots);
            for (int i = 1; i < n && dots < MaxDots; i++)
            {
                var img = DotAt(st, dots);
                if (img == null) break;
                dots++;
                var rt = img.rectTransform;
                rt.anchoredPosition = Vector2.Lerp(a, b, i / (float)n);
                rt.localScale = new Vector3(scale, scale, 1f);
                img.color = r.Color;
                if (!img.gameObject.activeSelf) img.gameObject.SetActive(true);
            }
        }
        for (int i = dots; i < st.Dots.Count; i++)
        {
            var img = st.Dots[i];
            try { if (img != null && img.gameObject.activeSelf) img.gameObject.SetActive(false); } catch (Exception ex) { DebugLog.Error("core", ex, "MapMarkers.Update"); }
        }
    }

    // ---------------------------------------------------------------- clones

    private static RectTransform PinTemplate(MapView view)
    {
        try
        {
            var rects = view.pinpointIndicatorsRectTransforms;
            if (rects != null)
                for (int i = 0; i < rects.Count; i++)
                    if (rects[i] != null) return rects[i];
            var icons = view.pinpointIndicators;
            if (icons != null)
                for (int i = 0; i < icons.Count; i++)
                    if (icons[i] != null) return icons[i].transform.TryCast<RectTransform>();
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "MapMarkers.PinTemplate"); }
        return null;
    }

    private static PinClone PinAt(ViewState st, int index)
    {
        if (index < st.Pins.Count)
        {
            var have = st.Pins[index];
            if (have.Root != null && have.Rect != null) return have;
            st.Pins.RemoveAt(index);
        }
        if (index > st.Pins.Count) return null;
        var tpl = PinTemplate(st.View);
        if (tpl == null) return null; // not set up yet: try again next frame
        GameObject go = null;
        try
        {
            go = Object.Instantiate(tpl.gameObject, tpl.parent, false);
            go.name = "OTHPlus_MapPin_" + index;
            var rect = go.GetComponent<RectTransform>();
            if (rect == null) { Destroy(go); st.Broken = true; return null; }
            var icon = go.GetComponent<PinpointMapIcon>() ?? go.GetComponentInChildren<PinpointMapIcon>(true);
            try { icon?.SetLabelTextVisibility(true); } catch (Exception ex) { DebugLog.Error("core", ex, "MapMarkers.PinAt"); }
            var pin = new PinClone { Root = go, Rect = rect, Icon = icon, Label = null, Color = Color.clear, Shown = true };
            Show(pin, false);
            st.Pins.Add(pin);
            return pin;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "MapMarkers.PinAt");
            if (go != null) Destroy(go);
            st.Broken = true;
            LogOnce("pin", e);
            return null;
        }
    }

    private static Image DotAt(ViewState st, int index)
    {
        if (index < st.Dots.Count)
        {
            var have = st.Dots[index];
            if (have != null) return have;
            st.Dots.RemoveAt(index);
        }
        if (index > st.Dots.Count) return null;
        var tpl = PinTemplate(st.View);
        if (tpl == null) return null;
        try
        {
            var go = new GameObject("OTHPlus_MapRouteDot");
            go.transform.SetParent(tpl.parent, false);
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            var rt = img.rectTransform;
            rt.anchorMin = tpl.anchorMin;
            rt.anchorMax = tpl.anchorMax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(DotSize, DotSize);
            // Under the game's pins and player icons.
            try { go.transform.SetSiblingIndex(tpl.GetSiblingIndex()); } catch (Exception ex) { DebugLog.Error("core", ex, "MapMarkers.DotAt"); }
            go.SetActive(false);
            st.Dots.Add(img);
            return img;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "MapMarkers.DotAt");
            st.Broken = true;
            LogOnce("dot", e);
            return null;
        }
    }

    private static void Show(PinClone p, bool show)
    {
        if (p.Shown == show || p.Root == null) return;
        p.Shown = show;
        try { p.Root.SetActive(show); } catch (Exception ex) { DebugLog.Error("core", ex, "MapMarkers.Show"); }
    }

    private static void PruneViews()
    {
        List<IntPtr> dead = null;
        foreach (var kv in Views)
            if (kv.Value.View == null) (dead ??= new List<IntPtr>()).Add(kv.Key);
        if (dead != null) foreach (var k in dead) Views.Remove(k);
    }

    private static void Destroy(GameObject go)
    {
        try { if (go != null) Object.Destroy(go); } catch (Exception ex) { DebugLog.Error("core", ex, "MapMarkers.Destroy"); }
    }

    private static void LogOnce(string what, Exception e)
    {
        if (Logged.Add(what)) Core.CorePlugin.Logger?.LogWarning($"[Map] mod pins on the map ({what}): {e.GetType().Name}: {e.Message}");
    }
}
