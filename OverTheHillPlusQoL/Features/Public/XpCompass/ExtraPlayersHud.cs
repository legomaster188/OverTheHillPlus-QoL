using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Fusion;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.XpCompass;

/// <summary>
/// Players 5+ (beyond the game's 4-slot OrderedPlayerRefs table) on the compass and on the repair health bars.
///
/// What the game does (decoded from Cpp2IL):
/// - CompassController.LateUpdate calls UpdateOtherCarsCompassIcon every frame. For each OrderedPlayerRefs entry that
///   is not the local player, is SpawnedInMap and has a player object, it skips the player if a trackedCars icon already
///   targets the player object's transform; otherwise it calls RegisterTarget(transform, smiley sprite, Properties 3,
///   null), sets icon.image.color = UIManager.GetPlayerColor(p), clears the background colour and adds the icon to
///   trackedCars. UpdateIcons removes and destroys icons whose target transform is gone.
/// - CarHealthUI.SetupPlayerCarHealthStatus clears carHealthStatusIxByPlayerRef and, for each OrderedPlayerRefs entry
///   with a player object, maps it to the next carHealthStatusButtons index, points the button's
///   WorldTrackingRectTransform at the car and fills its health (same as SetHealthUI). It runs on player spawn and on
///   PlayerDataUpdated; UpdateHealthUI/SetHealthUI later look buttons up through the dictionary.
///
/// Both are mirrored here in postfixes for the players that are not in OrderedPlayerRefs.
/// </summary>
internal sealed class ExtraPlayersHudFeature : IFeature
{
    public void Register()
    {
        ExtraPlayersHud.Bind();
        Registry.OnTick(ExtraPlayersHud.Tick);
        Registry.Section(Registry.PlusMode, "Lobby", ExtraPlayersHud.DrawMenu, 40);
    }
}

internal static class ExtraPlayersHud
{
    public static ConfigEntry<bool> Compass, Health;

    private const string SmileyPath = "Art/UI/Icons/Smileys/Smiley_Circle_Happy";
    private const CompassIcon.Properties CarIconProperties = (CompassIcon.Properties)3; // same value the game passes
    private const int MaxHealthButtons = 20;

    private struct Extra
    {
        public PlayerRef Player;
        public NetworkObject Object;
        public FusionPlayer Fusion;
    }

    private static readonly List<Extra> Extras = new();
    private static float _nextScan, _nextCompass, _nextHealthCheck;

    // compass icons we created, by PlayerRef.RawEncoded
    private static readonly Dictionary<int, CompassIcon> Icons = new();
    private static CompassController _compass;
    private static Sprite _sprite;

    private static CarHealthUI _healthUi;
    private static int _healthBars, _clonedButtons;
    private static bool _healthWasOn;

    private static readonly HashSet<string> LoggedOnce = new();

    private static readonly Color[] Fallback =
    {
        new(1.00f, 0.55f, 0.15f), // orange
        new(0.62f, 0.42f, 1.00f), // violet
        new(0.20f, 0.88f, 0.78f), // teal
        new(1.00f, 0.42f, 0.70f), // pink
        new(0.72f, 0.95f, 0.25f), // lime
        new(0.35f, 0.62f, 1.00f), // blue
        new(1.00f, 0.86f, 0.30f), // yellow
        new(0.86f, 0.86f, 0.86f), // silver
    };

    public static void Bind()
    {
        var f = ModConfig.File;
        Compass = f.Bind("ExtraPlayersHud", "Compass", true,
            "Show players beyond the game's 4 player slots (big lobbies) on the compass.");
        Health = f.Bind("ExtraPlayersHud", "HealthBars", true,
            "Give players beyond the game's 4 player slots the car health bar that pops up on repairs/damage.");
        _healthWasOn = Health.Value;
    }

    // ---- shared: who is an extra player ----

    private static void LogOnce(string key, string message)
    {
        if (LoggedOnce.Add(key)) Plugin.Logger.LogWarning("[ExtraPlayersHud] " + message);
    }

    /// <summary>Players with a car that are not in OrderedPlayerRefs. Rescanned at most once a second.</summary>
    private static List<Extra> GetExtras(bool force = false)
    {
        if (!force && Time.unscaledTime < _nextScan) return Extras;
        _nextScan = Time.unscaledTime + 1f;
        Extras.Clear();
        try
        {
            var runner = GameState.Runner;
            var sp = OverlandSessionPlayers.Instance;
            if (runner == null || sp == null) return Extras;

            var inSlots = new HashSet<int>();
            try
            {
                var order = sp.OrderedPlayerRefs;
                for (int i = 0; i < order.Length; i++) inSlots.Add(order.Get(i).RawEncoded);
            }
            catch { return Extras; } // not spawned yet: networked properties aren't readable

            var seen = new HashSet<int>();
            foreach (var o in GameState.FusionPlayers)
            {
                var fp = o.TryCast<FusionPlayer>();
                if (fp == null) continue;
                NetworkObject own;
                PlayerRef p;
                try
                {
                    own = fp.Object;
                    if (own == null) continue;
                    p = own.InputAuthority;
                }
                catch { continue; }
                if (!p.IsRealPlayer || inSlots.Contains(p.RawEncoded) || !seen.Add(p.RawEncoded)) continue;

                NetworkObject no = null;
                try { if (runner.TryGetPlayerObject(p, out var po) && po != null) no = po; } catch { }
                Extras.Add(new Extra { Player = p, Object = no ?? own, Fusion = fp });
            }
        }
        catch (Exception e) { LogOnce("scan", "scan failed: " + e.Message); }
        return Extras;
    }

    private static Color ColorFor(PlayerRef p)
    {
        try
        {
            var sp = OverlandSessionPlayers.Instance;
            if (sp != null)
            {
                var colors = sp.PlayerColors;
                for (int i = 0; i < colors.Length; i++)
                    if (colors.Get(i).RawEncoded == p.RawEncoded) return UIManager.GetPlayerColor(p);
            }
        }
        catch { }
        int id = Math.Max(0, p.PlayerId);
        return Fallback[id % Fallback.Length];
    }

    private static bool IsLocal(PlayerRef p)
    {
        try
        {
            var r = GameState.Runner;
            return r != null && r.LocalPlayer.RawEncoded == p.RawEncoded;
        }
        catch { return false; }
    }

    // ---- compass ----

    internal static void AfterCompassCars(CompassController c)
    {
        if (c == null) return;
        if (!Compass.Value)
        {
            if (Icons.Count > 0) RemoveIcons(c);
            return;
        }
        if (Time.unscaledTime < _nextCompass) return;
        _nextCompass = Time.unscaledTime + 0.5f;
        _compass = c;

        var cars = c.trackedCars;
        if (cars == null) return;
        PruneIcons();

        var sp = OverlandSessionPlayers.Instance;
        foreach (var x in GetExtras())
        {
            var p = x.Player;
            if (IsLocal(p)) continue;
            if (Icons.TryGetValue(p.RawEncoded, out var mine))
            {
                SetIconColor(mine, ColorFor(p)); // follows a promotion into a coloured slot
                continue;
            }
            try { if (sp != null && !sp.SpawnedInMap(p)) continue; } catch { }

            Transform target;
            try { target = x.Object != null ? x.Object.transform : null; } catch { target = null; }
            if (target == null) continue;
            if (AlreadyTracked(cars, target)) continue;

            try
            {
                var sprite = GetSprite(c);
                var icon = c.RegisterTarget(target, sprite, CarIconProperties, new Il2CppSystem.Nullable<PointOfInterestType>());
                if (icon == null) continue;
                SetIconColor(icon, ColorFor(p));
                icon.SetBackgroundImageColor(new Color(0f, 0f, 0f, 0f));
                cars.Add(icon);
                Icons[p.RawEncoded] = icon;
            }
            catch (Exception e) { LogOnce("register", "compass icon failed: " + e.Message); }
        }
    }

    private static bool AlreadyTracked(Il2CppSystem.Collections.Generic.List<CompassIcon> cars, Transform target)
    {
        var ptr = target.Pointer;
        for (int i = 0; i < cars.Count; i++)
        {
            var icon = cars[i];
            if (icon == null) continue;
            var t = icon.Target;
            if (t != null && t.Pointer == ptr) return true;
        }
        return false;
    }

    private static void SetIconColor(CompassIcon icon, Color color)
    {
        try
        {
            if (icon == null) return;
            var img = icon.image;
            if (img != null && img.color != color) img.color = color;
        }
        catch { }
    }

    /// <summary>The same smiley the game uses: copied from an existing car icon, else loaded like the game does.</summary>
    private static Sprite GetSprite(CompassController c)
    {
        if (_sprite != null) return _sprite;
        try
        {
            var cars = c.trackedCars;
            for (int i = 0; cars != null && i < cars.Count; i++)
            {
                var s = cars[i]?.image?.sprite;
                if (s != null) return _sprite = s;
            }
        }
        catch { }
        try
        {
            var rm = FunCore.Managers.ResourcesManager.Instance;
            if (rm != null) _sprite = rm.Load<Sprite>(SmileyPath, null);
        }
        catch (Exception e) { LogOnce("sprite", "smiley sprite: " + e.Message); }
        return _sprite;
    }

    /// <summary>Forgets icons the game already destroyed (target car gone).</summary>
    private static void PruneIcons()
    {
        if (Icons.Count == 0) return;
        List<int> dead = null;
        foreach (var kv in Icons)
        {
            bool alive;
            try { alive = kv.Value != null && kv.Value.Target != null; } catch { alive = false; }
            if (!alive) (dead ??= new List<int>()).Add(kv.Key);
        }
        if (dead != null) foreach (var k in dead) Icons.Remove(k);
    }

    private static void RemoveIcons(CompassController c)
    {
        foreach (var icon in Icons.Values)
        {
            try
            {
                if (icon == null) continue;
                c.trackedCars?.Remove(icon);
                c.tracked?.Remove(icon);
                Object.Destroy(icon.gameObject);
            }
            catch { }
        }
        Icons.Clear();
    }

    // ---- car health bars ----

    internal static void AfterHealthSetup(CarHealthUI ui)
    {
        if (ui == null) return;
        _healthUi = ui;
        _healthBars = 0;
        if (!Health.Value) return;
        try
        {
            var buttons = ui.carHealthStatusButtons;
            var dict = ui.carHealthStatusIxByPlayerRef;
            if (buttons == null || dict == null || buttons.Count == 0) return;

            int ix = dict.Count; // the game hands out indices 0..n-1 in order after clearing
            Camera cam = null;
            foreach (var x in GetExtras(force: true))
            {
                var p = x.Player;
                if (dict.ContainsKey(p)) continue;
                var cds = DamageSystem(x);
                if (cds == null) continue;

                while (ix >= buttons.Count)
                {
                    if (buttons.Count >= MaxHealthButtons) return;
                    var clone = CloneButton(buttons[0], buttons.Count);
                    if (clone == null) return;
                    buttons.Add(clone);
                }
                var btn = buttons[ix];
                if (btn == null) return;

                cam ??= HealthCamera(buttons[0]);
                dict.Add(p, ix);
                ix++;
                try { btn.WorldTrackingRectTransform?.SetTransformToTrack(cam, x.Object.transform); }
                catch (Exception e) { LogOnce("track", "health bar tracking: " + e.Message); }
                try { ui.SetHealthUI(p, cds); }
                catch (Exception e) { LogOnce("sethealth", "SetHealthUI: " + e.Message); }
                _healthBars++;
            }
        }
        catch (Exception e) { LogOnce("health", "health bars failed: " + e.Message); }
    }

    private static CarDamageSystem DamageSystem(Extra x)
    {
        try
        {
            var cds = x.Object != null ? x.Object.GetComponentInChildren<CarDamageSystem>(true) : null;
            if (cds == null && x.Fusion != null && x.Fusion.vehicleBase != null)
                cds = x.Fusion.vehicleBase.GetComponentInChildren<CarDamageSystem>(true);
            return cds;
        }
        catch { return null; }
    }

    private static ContextUICarHealthStatusButton CloneButton(ContextUICarHealthStatusButton template, int index)
    {
        try
        {
            if (template == null) return null;
            var go = Object.Instantiate(template.gameObject, template.transform.parent, false);
            go.name = template.gameObject.name + "_OTHPlus" + index;
            var b = go.GetComponent<ContextUICarHealthStatusButton>();
            if (b == null) { Object.Destroy(go); return null; }
            // hidden until the game shows it, like the original buttons after CarHealthUI.Start
            b.WorldTrackingRectTransform?.gameObject.SetActive(false);
            _clonedButtons++;
            return b;
        }
        catch (Exception e)
        {
            LogOnce("clone", "health bar clone failed: " + e.Message);
            return null;
        }
    }

    private static Camera HealthCamera(ContextUICarHealthStatusButton template)
    {
        try
        {
            var brain = SceneReferences.Instance?.mainCameraBrain;
            var cam = brain != null ? brain.OutputCamera : null;
            if (cam != null) return cam;
        }
        catch { }
        try
        {
            var cam = template?.WorldTrackingRectTransform?.referenceCamera;
            if (cam != null) return cam;
        }
        catch { }
        return Camera.main;
    }

    // ---- tick ----

    public static void Tick()
    {
        if (Time.unscaledTime < _nextHealthCheck) return;
        _nextHealthCheck = Time.unscaledTime + 2f;

        bool on = Health.Value;
        bool turnedOff = _healthWasOn && !on;
        _healthWasOn = on;

        var ui = _healthUi;
        try { if (ui == null || !ui.isActiveAndEnabled) return; }
        catch { _healthUi = null; return; }

        try
        {
            if (turnedOff) { ui.SetupPlayerCarHealthStatus(); return; } // rebuild without the extras
            if (!on || GameState.Runner == null) return;
            // An extra player's car appeared without the game re-running its setup: re-run it (our postfix adds them).
            var dict = ui.carHealthStatusIxByPlayerRef;
            if (dict == null) return;
            foreach (var x in GetExtras())
            {
                if (dict.ContainsKey(x.Player) || DamageSystem(x) == null) continue;
                ui.SetupPlayerCarHealthStatus();
                // Still missing (no free button, ...): don't rebuild every 2 s.
                if (!ui.carHealthStatusIxByPlayerRef.ContainsKey(x.Player)) _nextHealthCheck = Time.unscaledTime + 15f;
                return;
            }
        }
        catch (Exception e) { LogOnce("tick", "health re-check: " + e.Message); }
    }

    // ---- menu ----

    public static void DrawMenu()
    {
        Menu.Card("Extra players on the HUD", "Players 5+ in big lobbies, who the game leaves off its 4-player HUD.", () =>
        {
            Menu.Switch(Compass, "On the compass", "Coloured smiley icon like players 2-4", true);
            Menu.Switch(Health, "Car health bars", "The bar that pops up over a car on repairs and damage", true);
            int icons = 0;
            foreach (var i in Icons.Values) { try { if (i != null) icons++; } catch { } }
            Menu.KeyValue("Extra players", GameState.Runner == null ? "not in a session" : Extras.Count.ToString());
            Menu.KeyValue("Shown", $"{icons} on compass, {_healthBars} health bars ({_clonedButtons} added)");
        });
    }
}

[HarmonyPatch(typeof(CompassController), nameof(CompassController.UpdateOtherCarsCompassIcon))]
internal static class ExtraPlayersCompassPatch
{
    private static void Postfix(CompassController __instance)
    {
        try { ExtraPlayersHud.AfterCompassCars(__instance); }
        catch { }
    }
}

[HarmonyPatch(typeof(CarHealthUI), nameof(CarHealthUI.SetupPlayerCarHealthStatus))]
internal static class ExtraPlayersHealthPatch
{
    private static void Postfix(CarHealthUI __instance)
    {
        try { ExtraPlayersHud.AfterHealthSetup(__instance); }
        catch { }
    }
}
