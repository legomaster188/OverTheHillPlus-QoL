using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.Tag;

/// <summary>
/// The round on the game's map screen, after the game placed its player icons (MapView.UpdatePlayerIndicators runs
/// per frame while the map is open; the icon at index i is the player in OverlandSessionPlayers.OrderedPlayerRefs[i]).
/// Map &amp; HUD's icons for players past the 4 slots are found by their object name (OTHPlus_MapExtraPlayer_&lt;id&gt;).
/// <list type="bullet">
/// <item>Tag: the player who is it gets a red, bigger icon.</item>
/// <item>Hide and seek: a seeker's map does not show the hiders.</item>
/// </list>
/// Everything changed is put back when the round ends. Purely local: nothing is sent.
/// </summary>
[HarmonyPatch(typeof(MapView), nameof(MapView.UpdatePlayerIndicators))]
internal static class TagMapPatch
{
    private const string ExtraPrefix = "OTHPlus_MapExtraPlayer_";
    private static readonly Color ItColor = new(1f, 0.18f, 0.16f);

    private sealed class Changed
    {
        public MapPlayerImage Image;
        public Color OuterColor;
        public Vector3 Scale;
        public bool Marked, Hidden;
    }

    private static readonly Dictionary<IntPtr, Changed> Touched = new();
    private static readonly Dictionary<int, MapPlayerImage> Extras = new();
    private static float _nextExtraScan;
    private static bool _loggedError;

    private static void Postfix(MapView __instance)
    {
        try { Apply(__instance); }
        catch (Exception e)
        {
            DebugLog.Error("tag", e, "TagMapPatch.Postfix");
            if (_loggedError) return;
            _loggedError = true;
            TagPlugin.Logger.LogWarning("[Tag] map icons failed (further errors suppressed): " + e.Message);
        }
    }

    private static void Apply(MapView view)
    {
        var snap = TagGame.View;
        bool running = TagGame.Running;
        if (!running)
        {
            if (Touched.Count > 0) RestoreAll();
            return;
        }
        int me = TagCars.Me;
        var sp = OverlandSessionPlayers.Instance;
        var icons = view.playerIndicators;
        if (sp == null || icons == null) return;

        var order = sp.OrderedPlayerRefs;
        for (int i = 0; i < order.Length && i < icons.Count; i++)
            Style(icons[i], order.Get(i).RawEncoded, snap, me);

        if (Time.unscaledTime >= _nextExtraScan) ScanExtras(icons);
        foreach (var kv in Extras) Style(kv.Value, kv.Key, snap, me);
    }

    private static void Style(MapPlayerImage img, int id, TagSnapshot snap, int me)
    {
        if (img == null) return;
        bool mark = snap.Mode == TagMode.Tag && snap.It == id && TagConfig.ShowItMarker.Value;
        bool hide = id != me && snap.HiddenFrom(me, id);
        if (!Touched.TryGetValue(img.Pointer, out var c))
        {
            if (!mark && !hide) return;
            c = new Changed { Image = img, Scale = img.movingParent != null ? img.movingParent.localScale : Vector3.one };
            try { c.OuterColor = img.outerImage != null ? img.outerImage.color : Color.white; } catch (Exception ex) { DebugLog.Error("tag", ex, "TagMapPatch.Style"); c.OuterColor = Color.white; }
            Touched[img.Pointer] = c;
        }
        if (mark != c.Marked)
        {
            c.Marked = mark;
            try { if (img.outerImage != null) img.outerImage.color = mark ? ItColor : c.OuterColor; } catch (Exception ex) { DebugLog.Error("tag", ex, "TagMapPatch.Style"); }
            try { if (img.movingParent != null) img.movingParent.localScale = mark ? c.Scale * 1.45f : c.Scale; } catch (Exception ex) { DebugLog.Error("tag", ex, "TagMapPatch.Style"); }
        }
        // The game shows the icon again every frame it knows the position, so hiding is redone every frame.
        if (hide)
        {
            c.Hidden = true;
            try { var m = img.movingParent; if (m != null && m.gameObject.activeSelf) m.gameObject.SetActive(false); } catch (Exception ex) { DebugLog.Error("tag", ex, "TagMapPatch.Style"); }
        }
        else if (c.Hidden)
        {
            c.Hidden = false;
            try { var m = img.movingParent; if (m != null) m.gameObject.SetActive(true); } catch (Exception ex) { DebugLog.Error("tag", ex, "TagMapPatch.Style"); }
        }
    }

    private static void ScanExtras(Il2CppSystem.Collections.Generic.List<MapPlayerImage> icons)
    {
        _nextExtraScan = Time.unscaledTime + 1f;
        Extras.Clear();
        Transform parent = null;
        for (int i = 0; i < icons.Count && parent == null; i++)
            if (icons[i] != null) parent = icons[i].transform.parent;
        if (parent == null) return;
        for (int i = 0; i < parent.childCount; i++)
        {
            var t = parent.GetChild(i);
            if (t == null) continue;
            string n = t.name;
            if (n == null || !n.StartsWith(ExtraPrefix, StringComparison.Ordinal)) continue;
            if (!int.TryParse(n.Substring(ExtraPrefix.Length), out int id)) continue;
            var img = t.GetComponent<MapPlayerImage>();
            if (img != null) Extras[id] = img;
        }
    }

    private static void RestoreAll()
    {
        foreach (var c in Touched.Values)
        {
            try
            {
                if (c.Image == null) continue;
                if (c.Marked)
                {
                    if (c.Image.outerImage != null) c.Image.outerImage.color = c.OuterColor;
                    if (c.Image.movingParent != null) c.Image.movingParent.localScale = c.Scale;
                }
                if (c.Hidden && c.Image.movingParent != null) c.Image.movingParent.gameObject.SetActive(true);
            }
            catch (Exception ex) { DebugLog.Error("tag", ex, "TagMapPatch.RestoreAll"); }
        }
        Touched.Clear();
        Extras.Clear();
    }
}
