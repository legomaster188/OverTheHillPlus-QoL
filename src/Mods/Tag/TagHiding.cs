using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.Tag;

/// <summary>
/// Hide and seek, on a seeker's screen: besides the map (<see cref="TagMapPatch"/>), the hiders' name tags above
/// their cars (the game's own PlayerNameBillboard on each FusionPlayer) and their icons on the game's compass
/// (CompassController.trackedCars, whose CompassIcon.Target is the player's object or car) are hidden. Everything is
/// put back when the viewer stops seeking or the round ends. Purely local: nothing is sent, no other mod is patched
/// (Map &amp; HUD's own compass icons for players past the 4 slots are not covered).
/// </summary>
internal static class TagHiding
{
    private static readonly Dictionary<IntPtr, GameObject> HiddenTags = new();
    private static readonly Dictionary<IntPtr, (CanvasGroup group, float alpha)> HiddenIcons = new();
    private static bool _loggedError;

    /// <summary>A hider is hidden from this game's player right now.</summary>
    internal static bool Hidden(int id)
    {
        var snap = TagGame.View;
        int me = TagCars.Me;
        return snap != null && TagGame.Running && id >= 0 && id != me && snap.HiddenFrom(me, id);
    }

    /// <summary>Name tags, every frame (the game shows a tag again when it refreshes it).</summary>
    internal static void Tick()
    {
        try
        {
            bool any = TagGame.Running && TagGame.View.Mode == TagMode.HideAndSeek;
            if (!any) { if (HiddenTags.Count > 0) RestoreTags(); return; }
            foreach (var c in TagCars.All)
            {
                GameObject go = null;
                try { var b = c.Player != null ? c.Player.playerNameBillboard : null; if (b != null) go = b.gameObject; } catch (Exception ex) { DebugLog.Error("tag", ex, "TagHiding.Tick"); }
                if (go == null) continue;
                if (Hidden(c.Id))
                {
                    if (go.activeSelf) go.SetActive(false);
                    HiddenTags[go.Pointer] = go;
                }
                else if (HiddenTags.Remove(go.Pointer)) go.SetActive(true);
            }
        }
        catch (Exception e) { DebugLog.Error("tag", e, "TagHiding.Tick"); LogOnce(e); }
    }

    private static void RestoreTags()
    {
        foreach (var go in HiddenTags.Values)
            try { if (go != null) go.SetActive(true); } catch (Exception ex) { DebugLog.Error("tag", ex, "TagHiding.RestoreTags"); }
        HiddenTags.Clear();
    }

    /// <summary>Compass icons, after the compass's own per-frame update (LateUpdate has no parameters).</summary>
    internal static void Compass(CompassController compass)
    {
        try
        {
            bool any = TagGame.Running && TagGame.View.Mode == TagMode.HideAndSeek;
            if (!any) { if (HiddenIcons.Count > 0) RestoreIcons(); return; }
            var cars = compass.trackedCars;
            if (cars == null) return;
            for (int i = 0; i < cars.Count; i++)
            {
                var icon = cars[i];
                if (icon == null) continue;
                int id = -1;
                try { var t = icon.Target; if (t != null) id = TagCars.OfTransform(t.Pointer); } catch (Exception ex) { DebugLog.Error("tag", ex, "TagHiding.Compass"); }
                var group = icon.canvasGroup;
                if (group == null) continue;
                if (Hidden(id))
                {
                    if (!HiddenIcons.ContainsKey(group.Pointer)) HiddenIcons[group.Pointer] = (group, group.alpha);
                    group.alpha = 0f;
                }
                else if (HiddenIcons.TryGetValue(group.Pointer, out var was))
                {
                    HiddenIcons.Remove(group.Pointer);
                    group.alpha = was.alpha;
                }
            }
        }
        catch (Exception e) { DebugLog.Error("tag", e, "TagHiding.Compass"); LogOnce(e); }
    }

    private static void RestoreIcons()
    {
        foreach (var (group, alpha) in HiddenIcons.Values)
            try { if (group != null) group.alpha = alpha; } catch (Exception ex) { DebugLog.Error("tag", ex, "TagHiding.RestoreIcons"); }
        HiddenIcons.Clear();
    }

    private static void LogOnce(Exception e)
    {
        if (_loggedError) return;
        _loggedError = true;
        TagPlugin.Logger.LogWarning("[Tag] hiding hiders' tags / compass icons failed (further errors suppressed): " + e.Message);
    }
}

[HarmonyPatch(typeof(CompassController), nameof(CompassController.LateUpdate))]
internal static class TagCompassPatch
{
    private static void Postfix(CompassController __instance) => TagHiding.Compass(__instance);
}
