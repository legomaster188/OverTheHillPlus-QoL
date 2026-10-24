using System;
using System.Collections.Generic;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Trailers;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>The Trailers MODS page: pick a trailer, get it, hitch, put it back, load the car hauler; host limits.</summary>
internal static class TrailersSimple
{
    private static readonly string[] KindLabels = { "Utility trailer", "Teardrop camper", "Car hauler", "Boat trailer" };
    private static readonly int[] PerPlayerSteps = { 1, 2 };
    private static readonly int[] SessionSteps = { 1, 2, 3, 4, 5, 6, 7, 8 };

    private static List<ItemCatalog.Item> _cars;
    private static float _carsAt = -100f;
    private static int _carPick;

    internal static void Draw()
    {
        SimpleMenu.Heading("Trailers");
        string failed = TrailerPrefabs.FailedReason ?? ModManager.FailedReason(TrailersPlugin.ModId);
        if (failed != null) SimpleMenu.Note("Trailers are off this session: " + failed + ".");
        else if (!TrailerPrefabs.Ready) SimpleMenu.Note("Getting the trailers ready (waiting for " + (TrailerPrefabs.Waiting ?? "the game") + ").");
        else SimpleMenu.Note("Tow a trailer behind your car. Get one while stopped: it comes hitched. Unhitched, its parking brake " +
                             "holds it; hitched, it brakes by itself when you slow down. Everyone on a server needs this mod.");
        if (TrailersConfig.Kind == null) return; // the mod didn't start: nothing else to show

        var spec = TrailerSpecs.Of(TrailersConfig.Kind.Value) ?? TrailerSpecs.All[0];
        SimpleMenu.Choice("Trailer", spec.Description, (int)TrailersConfig.Kind.Value, KindLabels,
            i => TrailersConfig.Kind.Value = (TrailerKind)i);

        string why = Blocker();
        // towing needs the tow bar upgrade on the car (unhitching and removing never do)
        string noBar = why == null && !CarHasTowHitch() ? "No tow hitch" : null;
        string towWhy = why ?? noBar;
        int mine = TrailersActions.MineCount(out var hitched, out var any);
        if (noBar != null) SimpleMenu.Note(TowHitch.Need + " (the Tow Bar in your car's upgrades).");
        SimpleMenu.Button("Get this trailer", towWhy ?? (mine > 0 ? $"{mine} out" : "Ready"),
            "Spawns it right behind your car's tow hitch, hitched (while stopped).", () => TrailersActions.Spawn(), towWhy == null);
        bool unhitching = hitched != null;
        string hitchWhy = unhitching ? why : towWhy;
        SimpleMenu.Button(unhitching ? "Unhitch" : "Hitch", hitchWhy ?? (any == null ? "—" : unhitching ? "Hitched" : "Loose"),
            unhitching ? "Leaves the trailer where it stands, parking brake on." : "Back up until your car's tow hitch is near the trailer's coupler (2.5 m), then hitch.",
            () => TrailersActions.ToggleHitch(), hitchWhy == null && any != null);
        SimpleMenu.Button("Put it back behind me", towWhy ?? (any == null ? "—" : "Ready"),
            "Moves your trailer right behind your car's tow hitch and hitches it (while stopped): for a trailer on its side or left far away.",
            () => TrailersActions.ResetBehind(), towWhy == null && any != null);
        SimpleMenu.Button("Remove my trailer", why ?? (any == null ? "—" : "Ready"), "Takes your trailer away.",
            () => TrailersActions.Remove(), why == null && any != null);

        // the car hauler's load: one of your own cars, shown on the deck
        TrailerLive hauler = null;
        int me = -1;
        try { me = GameState.Runner != null ? GameState.Runner.LocalPlayer.RawEncoded : -1; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailersSimple.Draw"); }
        foreach (var t in TrailerWorld.All) if (t.State.Owner == me && t.Kind == TrailerKind.Hauler) { hauler = t; break; }
        if (hauler != null)
        {
            var cars = OwnedCars();
            if (cars.Count > 0)
            {
                _carPick = Mathf.Clamp(_carPick, 0, cars.Count - 1);
                var labels = new string[cars.Count];
                for (int i = 0; i < cars.Count; i++) labels[i] = cars[i].Name;
                SimpleMenu.Choice("Car to load", "One of your own cars, as it looks in the garage.", _carPick, labels, i => _carPick = i);
                uint pick = cars[_carPick].Id;
                // a tractor unit is longer than the deck: its rear wheels would hang off the end
                string loadWhy = why ?? (CargoModels.FitsDeck(pick, hauler.Model) ? null : "Too long for the deck");
                SimpleMenu.Button(hauler.State.CargoCarId != 0 ? "Swap the load" : "Load it", loadWhy ?? "Ready",
                    "Puts that car on the deck (it adds weight). It is a load, not a car you can drive off.",
                    () => TrailersActions.Load(pick), loadWhy == null);
            }
            if (hauler.State.CargoCarId != 0)
                SimpleMenu.Button("Unload", why ?? "Ready", "Empties the deck.", () => TrailersActions.Unload(), why == null);
        }

        SimpleMenu.Note("Hotkeys: hitch / unhitch " + KeyName(TrailersConfig.HitchKey.Value) + ", put it back " +
                        KeyName(TrailersConfig.ResetKey.Value) + " (set them under Keybinds).");
        if (!string.IsNullOrEmpty(TrailerNet.LastMessage) && Time.unscaledTime - TrailerNet.LastMessageAt < 8f)
            SimpleMenu.Note(TrailerNet.LastMessage + ".");

        SimpleMenu.Heading("When hosting");
        SimpleMenu.IntChoice("Trailers per player", "How many trailers each player may have at once.", TrailersConfig.PerPlayer.Value,
            PerPlayerSteps, v => v.ToString(), v => TrailersConfig.PerPlayer.Value = v);
        SimpleMenu.IntChoice("Trailers per server", "How many trailers the whole session may have.", TrailersConfig.SessionMax.Value,
            SessionSteps, v => v.ToString(), v => TrailersConfig.SessionMax.Value = v);
        SimpleMenu.Toggle(TrailersConfig.GuestsMay, "Guests may tow", "Guests with this mod can get and hitch their own trailers.");
        bool host = GameState.Runner != null && GameState.IsAuthority;
        SimpleMenu.Button("Remove all trailers", host ? $"{TrailerWorld.Count} out" : "Host only", "Takes every trailer in the session away.",
            () => Menu.SetStatus($"Removed {TrailerHost.DespawnAll(DespawnReason.Removed)} trailers"), host && TrailerWorld.Count > 0);
    }

    /// <summary>Why the buttons can't be used right now (null: they can).</summary>
    private static string Blocker()
    {
        if (!TrailerPrefabs.Ready) return "Not ready";
        if (GameState.Runner == null) return "In a world";
        if (!ModManager.ServerModActive(TrailersPlugin.ModId)) return "Not on this server";
        if (TrailerNet.Waiting) return "Asking...";
        return null;
    }

    /// <summary>Whether the local player's car has its tow bar mounted (true when there is no car to ask about: the
    /// request then says what is missing).</summary>
    private static bool CarHasTowHitch()
    {
        try
        {
            var car = TrailerHost.CarOf(GameState.Runner.LocalPlayer);
            return car == null || TowBar.Mounted(car);
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailersSimple.CarHasTowHitch"); return true; }
    }

    private static string KeyName(Key k) => k == Key.None ? "none" : k.ToString();

    private static List<ItemCatalog.Item> OwnedCars()
    {
        if (_cars != null && Time.unscaledTime - _carsAt < 5f) return _cars;
        _carsAt = Time.unscaledTime;
        var l = new List<ItemCatalog.Item>();
        try
        {
            foreach (var c in ItemCatalog.Cars())
                if (CargoModels.Owned(c.Id) && CargoModels.CarName(c.Id) != null) l.Add(c);
        }
        catch (Exception e) { DebugLog.Error("trailers", e, "TrailersSimple.OwnedCars"); TrailersPlugin.LogOnce("cars", "[Trailers] your cars: " + e.Message); }
        return _cars = l;
    }
}
