using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.Flares;

/// <summary>
/// Far-away name tags. The tags themselves are the game's (PlayerNameBillboard above every other player's car; Map &amp;
/// HUD restores and refreshes them in big lobbies), so none are made here. Each tag already tracks its car with a
/// WorldTrackingRectTransform that fades it by distance (fadeByDistance, startFade, drawDistance); this raises those
/// two distances to the configured range. After each of its position updates a postfix then
/// - fades the tag with a smooth curve (never above what the game itself allows, so a tag behind you stays hidden),
/// - shrinks it with distance down to a minimum size (a tag on the screen overlay is otherwise the same size at any
///   distance),
/// - optionally fades it out while terrain or buildings are between the camera and the car,
/// - and adds the distance in metres under the name (a second line of the same text, put back whenever the game
///   rewrites the name).
/// Purely local. The tags are looked up twice a second; the postfix only touches tags in that table.
/// </summary>
internal static class NameTags
{
    private const string Marker = "\n<size=65%>";

    private sealed class Tag
    {
        public PlayerNameBillboard Billboard;
        public WorldTrackingRectTransform Tracker;
        public Transform Car;
        public float OrigDraw, OrigFade;
        public bool Raised, OrigFadeByDistance;
        public int Metres = -1;
        public float NextTextCheck, NextRay, Occlusion = 1f;
        public bool Blocked;
        public float LastWritten = -1f, NativeSeen = 1f;
    }

    private static Dictionary<IntPtr, Tag> _tags = new();
    private static float _nextScan;
    private static int _errors;
    private static Camera _cam;
    private static int _camFrame = -1;

    /// <summary>Twice a second: finds the tags of the other players' cars and sets the game's distances on them.</summary>
    internal static void Tick()
    {
        if (Time.unscaledTime < _nextScan) return;
        _nextScan = Time.unscaledTime + 0.5f;

        bool on = FlaresConfig.TagsFar.Value && _errors < 5;
        var next = new Dictionary<IntPtr, Tag>();
        if (on)
        {
            foreach (var fp in GameState.FusionPlayers)
            {
                try
                {
                    if (fp == null || fp.WasCollected || fp.HasInputAuthority) continue; // our own car has no tag
                    var b = fp.playerNameBillboard;
                    if (b == null || b.WasCollected) continue;
                    var wtr = b.worldTrackingRectTransform;
                    if (wtr == null || wtr.WasCollected) continue;
                    if (!_tags.TryGetValue(wtr.Pointer, out var tag)) tag = new Tag();
                    tag.Billboard = b;
                    tag.Tracker = wtr;
                    tag.Car = (fp.vehicleBase != null ? fp.vehicleBase.transform : fp.transform);
                    next[wtr.Pointer] = tag;
                    Raise(tag);
                }
                catch (Exception e) { DebugLog.Error(FlaresPlugin.ModId, e, "NameTags.Tick"); }
            }
        }
        foreach (var kv in _tags)
            if (!next.ContainsKey(kv.Key)) Restore(kv.Value);
        _tags = next;
    }

    internal static void RestoreAll()
    {
        foreach (var t in _tags.Values) Restore(t);
        _tags = new Dictionary<IntPtr, Tag>();
    }

    private static void Raise(Tag t)
    {
        var w = t.Tracker;
        if (!t.Raised)
        {
            t.OrigDraw = w.drawDistance;
            t.OrigFade = w.startFade;
            t.OrigFadeByDistance = w.fadeByDistance;
            t.Raised = true;
        }
        float far = FlaresConfig.TagDistance.Value;
        if (!w.fadeByDistance) w.fadeByDistance = true;
        if (Math.Abs(w.drawDistance - far) > 0.5f) w.drawDistance = far;
        float start = far * 0.98f;
        if (Math.Abs(w.startFade - start) > 0.5f) w.startFade = start;
    }

    private static void Restore(Tag t)
    {
        try
        {
            if (t.Tracker == null || t.Tracker.WasCollected) return;
            if (t.Raised)
            {
                t.Tracker.drawDistance = t.OrigDraw;
                t.Tracker.startFade = t.OrigFade;
                t.Tracker.fadeByDistance = t.OrigFadeByDistance;
                t.Raised = false;
            }
            t.Tracker.transform.localScale = Vector3.one;
            var g = t.Tracker.canvasGroup;
            if (g != null) g.alpha = 1f;
            t.LastWritten = -1f;
            t.NativeSeen = 1f;
            SetMetres(t, -1);
        }
        catch (Exception e) { DebugLog.Error(FlaresPlugin.ModId, e, "NameTags.Restore"); }
    }

    private static Camera Cam()
    {
        int f = Time.frameCount;
        if (f != _camFrame)
        {
            _camFrame = f;
            _cam = Camera.main;
        }
        return _cam;
    }

    // Called by the postfix below for every tag the game places.
    internal static void After(WorldTrackingRectTransform w)
    {
        if (_tags.Count == 0 || !_tags.TryGetValue(w.Pointer, out var t) || t.Car == null) return;
        try
        {
            var cam = Cam();
            if (cam == null) return;
            var b = t.Billboard;
            if (b == null || b.WasCollected || b.isUIHidden) return; // the game's own hiding wins

            float now = Time.unscaledTime;
            var camPos = cam.transform.position;
            var carPos = t.Car.position;
            float dist = Vector3.Distance(camPos, carPos);
            float far = FlaresConfig.TagDistance.Value;

            float alpha = FlareMath.NameAlpha(dist, far, FlaresConfig.TagFadeStart.Value);
            if (FlaresConfig.TagOcclusion.Value)
            {
                if (now >= t.NextRay)
                {
                    t.NextRay = now + 0.2f;
                    t.Blocked = alpha > 0f && IsBlocked(camPos, carPos, t.Car);
                }
                t.Occlusion = Mathf.MoveTowards(t.Occlusion, t.Blocked ? 0f : 1f, Time.unscaledDeltaTime * 5f);
                alpha *= t.Occlusion;
            }
            else t.Occlusion = 1f;

            var group = w.canvasGroup;
            if (group != null)
            {
                // The game's own alpha (it hides a tag behind us or off screen) when it wrote one since we did; ours only lowers it.
                float seen = group.alpha;
                if (Math.Abs(seen - t.LastWritten) > 0.001f) t.NativeSeen = seen;
                float final = Math.Min(t.NativeSeen, alpha);
                group.alpha = final;
                t.LastWritten = final;
            }

            float scale = FlareMath.NameScale(dist, FlaresConfig.TagFullSize.Value, far, FlaresConfig.TagMinSize.Value);
            var tr = w.transform;
            if (Math.Abs(tr.localScale.x - scale) > 0.005f) tr.localScale = new Vector3(scale, scale, 1f);

            int metres = FlaresConfig.TagMetres.Value ? Math.Max(0, (int)Math.Round(dist)) : -1;
            if (metres != t.Metres || now >= t.NextTextCheck)
            {
                t.NextTextCheck = now + 0.5f;
                SetMetres(t, metres);
            }
        }
        catch (Exception e)
        {
            DebugLog.Error(FlaresPlugin.ModId, e, "NameTags.After");
            if (++_errors == 5) FlaresCore.LogOnce("tags", "name tag changes failed five times and are switched off for this session: " + e.Message);
        }
    }

    /// <summary>Puts "N m" under the tag's name (-1: takes it off). The name is whatever the game has written, cut before our line.</summary>
    private static void SetMetres(Tag t, int metres)
    {
        t.Metres = metres;
        var text = t.Billboard != null && !t.Billboard.WasCollected ? t.Billboard.playerNameText : null;
        if (text == null) return;
        string shown = text.text ?? "";
        int at = shown.IndexOf(Marker, StringComparison.Ordinal);
        string name = at >= 0 ? shown.Substring(0, at) : shown;
        if (name.Length == 0) return;
        string want = metres < 0 ? name : name + Marker + FlareMath.Metres(metres) + "</size>";
        if (want != shown) text.text = want;
    }

    private static bool IsBlocked(Vector3 from, Vector3 carPos, Transform car)
    {
        var to = carPos + Vector3.up * 1.5f;
        var dir = to - from;
        float len = dir.magnitude;
        if (len < 3f) return false;
        var hits = Physics.RaycastAll(from, dir / len, len - 1f);
        if (hits == null) return false;
        var carRoot = car.root;
        var mine = GameState.LocalCar != null ? GameState.LocalCar.transform.root : null;
        for (int i = 0; i < hits.Length; i++)
        {
            var c = hits[i].collider;
            if (c == null || c.isTrigger) continue;
            var root = c.transform.root;
            if (root == carRoot || (mine != null && root == mine)) continue; // the car itself, and ours
            return true;
        }
        return false;
    }
}

[HarmonyPatch(typeof(WorldTrackingRectTransform), nameof(WorldTrackingRectTransform.UpdateTrackedPosition))]
internal static class TagTrackerPatch
{
    private static void Postfix(WorldTrackingRectTransform __instance) => NameTags.After(__instance);
}
