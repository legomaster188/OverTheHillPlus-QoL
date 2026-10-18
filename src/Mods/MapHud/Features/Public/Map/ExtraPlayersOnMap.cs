using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Fusion;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using OverTheHillPlus.UI;
using Object = UnityEngine.Object;
using OverTheHillPlus.MapHud;

namespace OverTheHillPlus.Features.Public.Map;

/// <summary>
/// Players beyond the game's 4 player slots on the map screen.
///
/// What the game does (decoded from MapView with Cpp2IL):
/// - MapView.playerIndicators is a list of MapPlayerImage (one per slot). SetupPlayerNames and
///   UpdatePlayerIndicators only loop over OverlandSessionPlayers.OrderedPlayerRefs (4 entries), so a player who
///   is not in a slot never gets an icon, a name or a colour.
/// - Per frame (MapView.Update, while the local player is spawned) for each slot player that is SpawnedInMap and
///   whose OverlandNetworkRunner.GetPlayerPositionAndRotation succeeds:
///   movingParent.anchoredPosition = WorldToUIPosition(pos); rotatingParent.rotation = Euler(0, 0, -yaw);
///   otherwise movingParent's GameObject is hidden.
/// - Name zoom: UpdatePlayersNameZoom(zoom) sets playerTextLabel.localScale = Vector3.one / zoom.
/// - Map pins: MapNetworkManager.PlayerRefToPinpointPosition (capacity 7) drives pinpointIndicators /
///   pinpointIndicatorsRectTransforms, again only for slot players.
///
/// This feature clones the game's own player indicator (and pin) once per extra player and drives the clones the
/// same way, from a postfix on UpdatePlayerIndicators. Purely local and visual: nothing is sent to anyone.
/// </summary>
internal sealed class ExtraPlayersOnMap : IFeature
{
    private const string Section = "MapExtraPlayers";

    internal static ConfigEntry<bool> Enabled, ShowNames, ShowPins;

    public void Register()
    {
        var f = MapHudConfig.File;
        Enabled = f.Bind(Section, "Enabled", true,
            "Show players beyond the game's 4 player slots on the map (car icon, name and colour).");
        ShowNames = f.Bind(Section, "ShowNames", true, "Show the names of those extra players next to their map icon.");
        ShowPins = f.Bind(Section, "ShowPins", true, "Show the map pins placed by those extra players.");

        Registry.Section(Registry.PlusMode, "Lobby", DrawMenu, 40);
        MapExtraPlayers.Install();
    }

    private static void DrawMenu()
    {
        Menu.Card("Extra players on the map", "The game's map only draws 4 players. Players past that get their own icon, name, colour and pin here.", () =>
        {
            Menu.Switch(Enabled, "Show extra players on the map", "Car icon + colour for players 5 and up", true);
            Menu.Switch(ShowNames, "Show their names", null, Enabled.Value);
            Menu.Switch(ShowPins, "Show their map pins", null, Enabled.Value);

            var extras = MapExtraPlayers.Extras;
            Menu.KeyValue("Extra players now", extras.Count == 0 ? "none" : extras.Count.ToString());
            for (int i = 0; i < extras.Count && i < 12; i++)
            {
                var r = Menu.NextRow(20f);
                Theme.Fill(new Rect(r.x + 4f, r.y + 4f, 12f, 12f), extras[i].Color, 6);
                GUI.Label(new Rect(r.x + 24f, r.y, r.width - 24f, 20f), extras[i].Name ?? "?", Theme.Label);
            }
            if (MapExtraPlayers.Problem != null) Menu.Banner(MapExtraPlayers.Problem, Theme.Warn);
        });
    }
}

/// <summary>The map-side work: finding extra players, cloning and driving their indicators and pins.</summary>
internal static class MapExtraPlayers
{
    internal sealed class Extra
    {
        public PlayerRef Ref;
        public int Raw;
        public FusionPlayer Player;
        public string Name;
        public Color Color;
    }

    private sealed class Marker
    {
        public GameObject Root;
        public MapPlayerImage Image;
        public RectTransform Moving, Rotating;
        public GameObject Label;
        public bool? Shown;
        public bool? NameShown;
        public string Name;
        public Color Color;
    }

    private sealed class Pin
    {
        public GameObject Root;
        public RectTransform Rect;
        public PinpointMapIcon Icon;
        public bool? Shown;
        public string Name;
        public Color Color;
    }

    private sealed class ViewState
    {
        public MapView View;
        public readonly Dictionary<int, Marker> Markers = new();
        public readonly Dictionary<int, Pin> Pins = new();
        public bool MarkersBroken, PinsBroken;
    }

    /// <summary>Players with a car who are not in one of the game's 4 slots (refreshed about once a second).</summary>
    internal static readonly List<Extra> Extras = new();
    /// <summary>Shown in the menu when something could not be set up.</summary>
    internal static string Problem;

    private static readonly Dictionary<IntPtr, ViewState> Views = new();
    private static readonly Dictionary<int, int> PaletteSlot = new();
    private static readonly HashSet<string> Logged = new();
    private static float _nextScan;
    private static float _zoom = -1f;
    private static Color? _defaultGameColor;

    // Distinct from each other and readable on the map; extra players keep their colour for the session.
    private static readonly Color[] Palette =
    {
        new(0.95f, 0.45f, 0.85f), new(0.30f, 0.90f, 0.85f), new(1.00f, 0.82f, 0.25f), new(0.62f, 0.50f, 1.00f),
        new(0.55f, 0.95f, 0.40f), new(1.00f, 0.55f, 0.35f), new(0.40f, 0.70f, 1.00f), new(0.92f, 0.92f, 0.92f),
        new(0.90f, 0.30f, 0.35f), new(0.25f, 0.70f, 0.45f), new(1.00f, 0.72f, 0.82f), new(0.78f, 0.62f, 0.40f),
    };

    // ---- patches (own Harmony instance so a failure here never affects the rest of the mod) ----

    internal static void Install()
    {
        var h = new Harmony(MapHudPlugin.Guid + ".mapextraplayers");
        Patch(h, nameof(MapView.UpdatePlayerIndicators), nameof(AfterUpdatePlayerIndicators));
        Patch(h, nameof(MapView.UpdatePlayersNameZoom), nameof(AfterUpdatePlayersNameZoom));
        Patch(h, nameof(MapView.SetupPlayerNames), nameof(AfterSetupPlayerNames));
        Patch(h, nameof(MapView.OnDestroy), nameof(AfterOnDestroy));
    }

    private static void Patch(Harmony h, string target, string postfix)
    {
        try
        {
            var original = AccessTools.Method(typeof(MapView), target);
            if (original == null) { Problem = $"MapView.{target} not found: extra players can't be shown."; MapHudPlugin.Logger.LogWarning("[Map] " + Problem); return; }
            h.Patch(original, postfix: new HarmonyMethod(typeof(MapExtraPlayers), postfix));
        }
        catch (Exception e)
        {
            DebugLog.Error("maphud", e, "MapExtraPlayers.Patch");
            Problem = $"Couldn't hook MapView.{target}.";
            MapHudPlugin.Logger.LogWarning($"[Map] patch MapView.{target}: {e.Message}");
        }
    }

    private static void AfterUpdatePlayerIndicators(MapView __instance)
    {
        try { Update(__instance); }
        catch (Exception e) { DebugLog.Error("maphud", e, "MapExtraPlayers.AfterUpdatePlayerIndicators"); LogOnce("update", e); }
    }

    private static void AfterUpdatePlayersNameZoom(MapView __instance, float currentZoom)
    {
        try
        {
            if (currentZoom > 0.0001f) _zoom = currentZoom;
            if (!Views.TryGetValue(__instance.Pointer, out var st)) return;
            foreach (var m in st.Markers.Values) ApplyNameScale(m, null);
        }
        catch (Exception e) { DebugLog.Error("maphud", e, "MapExtraPlayers.AfterUpdatePlayersNameZoom"); LogOnce("zoom", e); }
    }

    /// <summary>The game re-applies names (player joined/left, name changes): re-check ours soon.</summary>
    private static void AfterSetupPlayerNames() => _nextScan = 0f;

    private static void AfterOnDestroy(MapView __instance)
    {
        try { Views.Remove(__instance.Pointer); }
        catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.AfterOnDestroy"); }
    }

    // ---- per frame ----

    private static void Update(MapView view)
    {
        if (view == null) return;
        if (!Views.TryGetValue(view.Pointer, out var st))
        {
            PruneViews();
            st = new ViewState { View = view };
            Views[view.Pointer] = st;
        }

        if (!ExtraPlayersOnMap.Enabled.Value)
        {
            foreach (var m in st.Markers.Values) SetShown(m, false);
            foreach (var p in st.Pins.Values) SetShown(p, false);
            return;
        }

        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 1f;
            Scan();
        }

        var sp = OverlandSessionPlayers.Instance;
        var runner = OverlandNetworkRunner.Instance;

        // Car icons
        for (int i = 0; i < Extras.Count; i++)
        {
            var x = Extras[i];
            if (st.MarkersBroken) break;
            if (!TryGetPose(sp, runner, x, out var pos, out var rot))
            {
                if (st.Markers.TryGetValue(x.Raw, out var hidden)) SetShown(hidden, false);
                continue;
            }
            var m = GetMarker(st, x);
            if (m == null) continue;
            if (m.Name != x.Name) { m.Name = x.Name; SafeName(m); }
            if (m.Color != x.Color) { m.Color = x.Color; try { m.Image.SetInnerColor(x.Color); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.Update"); } }
            bool showName = ExtraPlayersOnMap.ShowNames.Value && !string.IsNullOrEmpty(x.Name);
            if (m.NameShown != showName) { m.NameShown = showName; try { m.Image.ShowPlayerName(showName); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.Update"); } }

            m.Moving.anchoredPosition = view.WorldToUIPosition(pos);
            if (m.Rotating != null) m.Rotating.rotation = Quaternion.Euler(0f, 0f, -rot.eulerAngles.y);
            SetShown(m, true);
        }
        RemoveGone(st.Markers, m => Destroy(m.Root));

        // Pins
        if (!ExtraPlayersOnMap.ShowPins.Value || st.PinsBroken)
        {
            foreach (var p in st.Pins.Values) SetShown(p, false);
        }
        else
        {
            MapNetworkManager mn = null;
            try { mn = MapNetworkManager.Instance; } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.Update"); }
            for (int i = 0; i < Extras.Count; i++)
            {
                var x = Extras[i];
                Vector3 pinPos = default;
                bool has = false;
                try
                {
                    if (mn != null)
                    {
                        var pins = mn.PlayerRefToPinpointPosition;
                        if (pins.ContainsKey(x.Ref)) { pinPos = pins.Get(x.Ref); has = true; }
                    }
                }
                catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.Update"); }
                if (!has)
                {
                    if (st.Pins.TryGetValue(x.Raw, out var hidden)) SetShown(hidden, false);
                    continue;
                }
                var p = GetPin(st, x);
                if (p == null) break;
                if (p.Name != x.Name) { p.Name = x.Name; try { p.Icon?.SetLabelText(x.Name ?? ""); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.Update"); } }
                if (p.Color != x.Color) { p.Color = x.Color; try { p.Icon?.SetColor(x.Color); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.Update"); } }
                p.Rect.anchoredPosition = view.WorldToUIPosition(pinPos);
                SetShown(p, true);
            }
        }
        RemoveGone(st.Pins, p => Destroy(p.Root));
    }

    /// <summary>Same source as the game (GetPlayerPositionAndRotation), falling back to the car's transform.</summary>
    private static bool TryGetPose(OverlandSessionPlayers sp, OverlandNetworkRunner runner, Extra x, out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = Quaternion.identity;
        try { if (sp != null && !sp.SpawnedInMap(x.Ref)) return false; }
        catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.TryGetPose"); }
        try
        {
            if (runner != null && runner.GetPlayerPositionAndRotation(x.Ref, out pos, out rot)) return true;
        }
        catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.TryGetPose"); }
        try
        {
            var fp = x.Player;
            if (fp == null) return false;
            var t = fp.vehicleBase != null ? fp.vehicleBase.transform : fp.transform;
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            pos = t.position;
            rot = t.rotation;
            return true;
        }
        catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.TryGetPose"); return false; }
    }

    // ---- finding extra players ----

    private static void Scan()
    {
        var found = new List<Extra>();
        try
        {
            var sp = OverlandSessionPlayers.Instance;
            if (sp == null) { Extras.Clear(); return; }

            var inSlots = new HashSet<int>();
            var order = sp.OrderedPlayerRefs;
            for (int i = 0; i < order.Length; i++) inSlots.Add(order.Get(i).RawEncoded);

            var seen = new HashSet<int>();
            foreach (var o in GameState.FusionPlayers)
            {
                var fp = o.TryCast<FusionPlayer>();
                if (fp == null) continue;
                PlayerRef p;
                try
                {
                    if (fp.Object == null) continue;
                    p = fp.Object.InputAuthority;
                    if (!p.IsRealPlayer) continue;
                }
                catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.Scan"); continue; }
                int raw = p.RawEncoded;
                if (inSlots.Contains(raw) || !seen.Add(raw)) continue;

                string name = null;
                try { name = sp.GetPlayerName(p); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.Scan"); }
                if (string.IsNullOrEmpty(name)) name = $"Player {p.PlayerId}";
                found.Add(new Extra { Ref = p, Raw = raw, Player = fp, Name = Clean(name) });
            }
        }
        catch (Exception e) { DebugLog.Error("maphud", e, "MapExtraPlayers.Scan"); LogOnce("scan", e); }

        found.Sort((a, b) => a.Raw.CompareTo(b.Raw));
        AssignColors(found);
        if (found.Count != Extras.Count)
            DebugLog.Verbose(MapHudPlugin.ModId, () => $"map: {found.Count} extra player(s) past the game's 4 slots (was {Extras.Count})");
        Extras.Clear();
        Extras.AddRange(found);
    }

    /// <summary>Names arrive from other players: one plain line, no rich-text tags, for the map label.</summary>
    private static string Clean(string name)
    {
        name = name.Replace('\n', ' ').Replace('\r', ' ').Replace("<", "").Replace(">", "").Trim();
        return name.Length > 32 ? name.Substring(0, 32) : name;
    }

    private static void AssignColors(List<Extra> list)
    {
        // Drop palette slots of players who are gone, then give newcomers the first free one.
        var current = new HashSet<int>();
        foreach (var x in list) current.Add(x.Raw);
        var stale = new List<int>();
        foreach (var k in PaletteSlot.Keys) if (!current.Contains(k)) stale.Add(k);
        foreach (var k in stale) PaletteSlot.Remove(k);

        foreach (var x in list)
        {
            // If anything (the game or another mod) gives this player a real colour, use it so all UI agrees.
            if (TryGameColor(x.Ref, out var game)) { x.Color = game; continue; }
            if (!PaletteSlot.TryGetValue(x.Raw, out int slot))
            {
                var used = new HashSet<int>(PaletteSlot.Values);
                slot = 0;
                while (used.Contains(slot) && slot < Palette.Length * 4) slot++;
                PaletteSlot[x.Raw] = slot;
            }
            x.Color = Palette[slot % Palette.Length];
        }
    }

    private static bool TryGameColor(PlayerRef p, out Color c)
    {
        c = default;
        try
        {
            // A player ref that is never in the colour table gives the game's fallback colour.
            _defaultGameColor ??= UIManager.GetPlayerColor(PlayerRef.FromEncoded(30000));
            c = UIManager.GetPlayerColor(p);
            var d = _defaultGameColor.Value;
            return Mathf.Abs(c.r - d.r) + Mathf.Abs(c.g - d.g) + Mathf.Abs(c.b - d.b) > 0.03f && c.a > 0.1f;
        }
        catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.TryGameColor"); return false; }
    }

    // ---- clones ----

    private static Marker GetMarker(ViewState st, Extra x)
    {
        if (st.Markers.TryGetValue(x.Raw, out var m))
        {
            if (m.Root != null && m.Moving != null) return m;
            st.Markers.Remove(x.Raw);
        }

        var list = st.View.playerIndicators;
        MapPlayerImage tpl = null;
        if (list != null)
            for (int i = 0; i < list.Count && tpl == null; i++)
                if (list[i] != null) tpl = list[i];
        if (tpl == null) return null; // not set up yet; try again next frame

        GameObject go = null;
        try
        {
            go = Object.Instantiate(tpl.gameObject, tpl.transform.parent, false);
            go.name = "OTHPlus_MapExtraPlayer_" + x.Raw;
            var img = go.GetComponent<MapPlayerImage>();
            var moving = img != null ? img.movingParent : null;
            var rotating = img != null ? img.rotatingParent : null;
            if (img == null || moving == null || !Inside(moving.transform, go.transform) ||
                (rotating != null && !Inside(rotating.transform, go.transform)))
            {
                Destroy(go);
                st.MarkersBroken = true;
                Problem = "The map's player icon couldn't be copied, so extra players can't be drawn on this map.";
                MapHudPlugin.Logger.LogWarning("[Map] MapPlayerImage clone doesn't own its moving/rotating parts; extra player icons disabled for this map");
                return null;
            }

            // Draw under the game's own player icons.
            try { go.transform.SetSiblingIndex(tpl.transform.GetSiblingIndex()); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.GetMarker"); }
            go.SetActive(true);

            m = new Marker
            {
                Root = go, Image = img, Moving = moving, Rotating = rotating,
                Label = img.playerTextLabel, Name = x.Name, Color = x.Color,
            };
            SafeName(m);
            try { img.SetInnerColor(x.Color); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.GetMarker"); }
            try
            {
                var sp = OverlandSessionPlayers.Instance;
                bool supporter = false, dev = false;
                try { supporter = sp != null && sp.OwnsSupporterEdition(x.Ref); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.GetMarker"); }
                try { dev = sp != null && sp.IsDevTeamMember(x.Ref); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.GetMarker"); }
                img.SetPlayerBadges(supporter, dev);
            }
            catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.GetMarker"); }
            ApplyNameScale(m, tpl);
            m.Shown = null;
            SetShown(m, false);
            st.Markers[x.Raw] = m;
            MapHudPlugin.Logger.LogInfo($"[Map] Showing extra player '{x.Name}' (id {x.Ref.PlayerId}) on the map");
            return m;
        }
        catch (Exception e)
        {
            DebugLog.Error("maphud", e, "MapExtraPlayers.GetMarker");
            if (go != null) Destroy(go);
            st.MarkersBroken = true;
            Problem = "Extra player icons failed to set up: " + e.Message;
            LogOnce("marker", e);
            return null;
        }
    }

    private static Pin GetPin(ViewState st, Extra x)
    {
        if (st.Pins.TryGetValue(x.Raw, out var p))
        {
            if (p.Root != null && p.Rect != null) return p;
            st.Pins.Remove(x.Raw);
        }

        RectTransform tpl = null;
        try
        {
            var rects = st.View.pinpointIndicatorsRectTransforms;
            if (rects != null)
                for (int i = 0; i < rects.Count && tpl == null; i++)
                    if (rects[i] != null) tpl = rects[i];
            if (tpl == null)
            {
                var icons = st.View.pinpointIndicators;
                if (icons != null)
                    for (int i = 0; i < icons.Count && tpl == null; i++)
                        if (icons[i] != null) tpl = icons[i].transform.TryCast<RectTransform>();
            }
        }
        catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.GetPin"); }
        if (tpl == null) return null;

        GameObject go = null;
        try
        {
            go = Object.Instantiate(tpl.gameObject, tpl.parent, false);
            go.name = "OTHPlus_MapExtraPin_" + x.Raw;
            var rect = go.GetComponent<RectTransform>();
            var icon = go.GetComponent<PinpointMapIcon>() ?? go.GetComponentInChildren<PinpointMapIcon>(true);
            if (rect == null)
            {
                Destroy(go);
                st.PinsBroken = true;
                return null;
            }
            p = new Pin { Root = go, Rect = rect, Icon = icon, Name = x.Name, Color = x.Color };
            try { icon?.SetLabelText(x.Name ?? ""); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.GetPin"); }
            try { icon?.SetColor(x.Color); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.GetPin"); }
            p.Shown = null;
            SetShown(p, false);
            st.Pins[x.Raw] = p;
            return p;
        }
        catch (Exception e)
        {
            DebugLog.Error("maphud", e, "MapExtraPlayers.GetPin");
            if (go != null) Destroy(go);
            st.PinsBroken = true;
            LogOnce("pin", e);
            return null;
        }
    }

    private static void SafeName(Marker m)
    {
        try { m.Image.SetPlayerName(m.Name ?? ""); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.SafeName"); }
    }

    private static void ApplyNameScale(Marker m, MapPlayerImage tpl)
    {
        try
        {
            if (_zoom > 0f) { m.Image.SetPlayerNameScale(Vector3.one / _zoom); return; }
            // No zoom seen yet: copy the game's current scale from its own indicator.
            if (tpl != null && tpl.playerTextLabel != null && m.Label != null)
                m.Label.transform.localScale = tpl.playerTextLabel.transform.localScale;
        }
        catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.ApplyNameScale"); }
    }

    private static bool Inside(Transform t, Transform root) => t == root || t.IsChildOf(root);

    private static void SetShown(Marker m, bool show)
    {
        if (m.Shown == show || m.Moving == null) return;
        m.Shown = show;
        try { m.Moving.gameObject.SetActive(show); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.SetShown"); }
    }

    private static void SetShown(Pin p, bool show)
    {
        if (p.Shown == show || p.Root == null) return;
        p.Shown = show;
        try { p.Root.SetActive(show); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.SetShown"); }
    }

    private static void RemoveGone<T>(Dictionary<int, T> map, Action<T> destroy)
    {
        if (map.Count == 0) return;
        List<int> gone = null;
        foreach (var k in map.Keys)
        {
            bool still = false;
            for (int i = 0; i < Extras.Count; i++) if (Extras[i].Raw == k) { still = true; break; }
            if (!still) (gone ??= new List<int>()).Add(k);
        }
        if (gone == null) return;
        foreach (var k in gone)
        {
            try { destroy(map[k]); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.RemoveGone"); }
            map.Remove(k);
        }
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
        try { if (go != null) Object.Destroy(go); } catch (Exception ex) { DebugLog.Error("maphud", ex, "MapExtraPlayers.Destroy"); }
    }

    private static void LogOnce(string what, Exception e)
    {
        if (Logged.Add(what)) MapHudPlugin.Logger.LogWarning($"[Map] extra players ({what}): {e.GetType().Name}: {e.Message}");
    }
}
