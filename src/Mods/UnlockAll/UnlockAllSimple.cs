using System;
using System.Collections.Generic;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.UnlockAll;

/// <summary>
/// Unlock All's MODS page: the bulk unlocks, then one car, one map and items at a time. Bulk and destructive actions
/// are pressed twice (they change the profile for good); the small ones run on the first press.
/// </summary>
internal static class UnlockAllSimple
{
    private const float ConfirmSeconds = 4f;
    private static string _armed;
    private static float _armedAt;

    private static int _car, _region = -1, _amount;
    private static uint? _regionFor; // the loaded map the region choice was last moved to
    private static string _km = "", _customAmount = "";

    private static readonly string[] UpgradeModes = { "Owned", "In the shop" };
    private static readonly int[] Amounts = { 1_000, 10_000, 100_000 };
    private static readonly string[] AmountLabels = { "1,000", "10,000", "100,000", "Custom" };

    private static IReadOnlyList<UnlockPlan.CarEntry> _carsFor;
    private static string[] _carLabels;
    private static IReadOnlyList<UnlockPlan.Region> _regionsFor;
    private static string[] _regionLabels;

    internal static void Draw()
    {
        string blocker = Unlocker.Blocker;
        bool can = blocker == null;
        if (_armed != null && Time.unscaledTime - _armedAt > ConfirmSeconds) _armed = null;

        SimpleMenu.Heading("Unlock everything");
        if (!can) SimpleMenu.Note(blocker);
        if (UnlockAllConfig.UpgradesOwned != null)
            SimpleMenu.Choice("Unlocked upgrades are", "Owned: ready to fit. In the shop: unlocked, still to buy. Cosmetics are always owned.",
                UnlockAllConfig.Owned ? 0 : 1, UpgradeModes, i => UnlockAllConfig.UpgradesOwned.Value = i == 0);
        Bulk(Unlocker.What.Cars, "Unlock all cars", "Every car is added to your garage.", can);
        Bulk(Unlocker.What.Upgrades, "Unlock all upgrades",
            UnlockAllConfig.Owned ? "Every upgrade for every car, owned." : "Every upgrade for every car, ready in the shop.", can);
        Bulk(Unlocker.What.Areas, "Unlock all areas",
            "Every region, and every cabin on the loaded map. A cabin's first visit also clears the fog around it and " +
            "discovers the places nearby.", can);
        Bulk(Unlocker.What.Liveries, "Unlock all liveries", "Every livery of every car.", can);
        Bulk(Unlocker.What.Everything, "Unlock everything",
            "Cars, upgrades, areas, cabins (with their fog and places), cosmetics, liveries and tool slots.", can);

        if (can)
        {
            DrawCars();
            DrawMaps();
            DrawItems();
        }
        SimpleMenu.Note("Saved to your profile and can't be undone. Paid DLC is never unlocked. Achievements pause for this session.");
    }

    // ---------------------------------------------------------------- cars

    private static void DrawCars()
    {
        SimpleMenu.Heading("Cars");
        var cars = CarUnlocks.Listed;
        if (cars.Count == 0)
        {
            SimpleMenu.Note("No cars found yet.");
            return;
        }
        if (!ReferenceEquals(cars, _carsFor))
        {
            _carsFor = cars;
            _carLabels = new string[cars.Count];
            for (int i = 0; i < cars.Count; i++) _carLabels[i] = cars[i].Name;
        }
        _car = Mathf.Clamp(_car, 0, cars.Count - 1);
        var car = cars[_car];
        SimpleMenu.Choice("Car", $"Every car without a DLC ({cars.Count}).", _car, _carLabels, i => _car = i);

        int? km = CarUnlocks.OdometerKm(car.Id);
        bool owned = km != null;
        SimpleMenu.Note(owned ? $"{car.Name} is in your garage, {km.Value:N0} km on the odometer." : $"{car.Name} isn't in your garage yet.");

        SimpleMenu.Button("Unlock car", owned ? "Owned" : "Ready", "Adds this car to your garage.",
            () => Menu.SetStatus(CarUnlocks.Unlock(car, UnlockPlan.CarScope.Car, car.Name)), !owned);
        SimpleMenu.Button("Unlock its upgrades", UnlockAllConfig.Owned ? "Owned" : "In the shop",
            "Every upgrade of this car; the switch above picks owned or in the shop.",
            () => Menu.SetStatus(CarUnlocks.Unlock(car, UnlockPlan.CarScope.Upgrades, $"{car.Name} upgrades")));
        SimpleMenu.Button("Unlock its cosmetics", "Ready", "Every cosmetic part of this car, owned.",
            () => Menu.SetStatus(CarUnlocks.Unlock(car, UnlockPlan.CarScope.Cosmetics, $"{car.Name} cosmetics")));
        SimpleMenu.Button("Unlock its liveries", "Ready", "Every livery of this car.",
            () => Menu.SetStatus(CarUnlocks.Unlock(car, UnlockPlan.CarScope.Liveries, $"{car.Name} liveries")));

        _km = SimpleMenu.Text("Odometer (km)", _km, 9, $"0 to {UnlockPlan.MaxKm:N0} km. The game checks distance milestones while driving.");
        bool kmOk = UnlockPlan.TryParseKm(_km, out int newKm);
        Confirmed($"odo:{car.Id}", "Set odometer", owned ? kmOk ? $"{newKm:N0} km" : "Type km" : "Not owned",
            "Replaces this car's odometer reading.", () => CarUnlocks.SetOdometer(car, newKm), owned && kmOk);

        Confirmed($"car:{car.Id}", "Unlock everything for this car", "",
            "The car, its upgrades, cosmetics and liveries.", () => CarUnlocks.Unlock(car, UnlockPlan.CarScope.Everything, car.Name), true);
    }

    // ---------------------------------------------------------------- maps

    private static void DrawMaps()
    {
        SimpleMenu.Heading("Maps");
        var regions = MapUnlocks.Regions;
        if (regions.Count == 0)
        {
            SimpleMenu.Note("No maps found yet.");
            return;
        }
        if (!ReferenceEquals(regions, _regionsFor))
        {
            _regionsFor = regions;
            _regionLabels = new string[regions.Count];
            for (int i = 0; i < regions.Count; i++) _regionLabels[i] = regions[i].Label;
        }
        // Start on the loaded map, and move there again when another map loads.
        uint? loadedArea = MapUnlocks.LoadedAreaId;
        int loadedRegion = UnlockPlan.RegionOf(regions, loadedArea);
        if (loadedArea != null && loadedArea != _regionFor && loadedRegion >= 0)
        {
            _regionFor = loadedArea;
            _region = loadedRegion;
        }
        _region = Mathf.Clamp(_region, 0, regions.Count - 1);
        var region = regions[_region];
        bool loaded = _region == loadedRegion;
        SimpleMenu.Choice("Map", "Every region on the game's map list.", _region, _regionLabels, i => _region = i);
        SimpleMenu.Note(loaded ? "This is the loaded map." :
            "Not loaded: only its areas can be unlocked from here. Cabins, fog and places need this map loaded.");

        SimpleMenu.Button("Unlock its areas", "Ready", "Opens this region's areas on the map selection.",
            () => Menu.SetStatus(MapUnlocks.UnlockAreas(region)));
        string onlyLoaded = loaded ? "Ready" : "Not loaded";
        SimpleMenu.Button("Unlock its cabins", onlyLoaded,
            "Every cabin on the loaded map. A cabin's first visit also clears the fog around it and discovers the places nearby.",
            () => Menu.SetStatus(MapUnlocks.UnlockCabins()), loaded);
        SimpleMenu.Button("Reveal its fog", onlyLoaded, "Clears the fog from the whole loaded map.",
            () => Menu.SetStatus(MapUnlocks.RevealFog()), loaded);
        SimpleMenu.Button("Discover its places", onlyLoaded, "Marks every place on the loaded map as discovered.",
            () => Menu.SetStatus(MapUnlocks.DiscoverPlaces()), loaded);
        bool shown = MapUnlocks.UnknownIconsShown;
        SimpleMenu.Button("Show unknown places", shown ? "On" : "Off",
            "Shows the icons of places you haven't found yet on the map, for this session.",
            () => Menu.SetStatus(MapUnlocks.ShowUnknownIcons(!shown)), loaded || shown);

        Confirmed($"map:{region.Name}", "Unlock everything on this map", "",
            loaded ? "Its areas, cabins, fog and places." : "Its areas (cabins, fog and places need the map loaded).",
            () => MapUnlocks.Everything(region, loaded), true);
    }

    // ---------------------------------------------------------------- items

    private static void DrawItems()
    {
        SimpleMenu.Heading("Items");
        SimpleMenu.Button("Unlock tool slots", "Ready", "All three tool slots.", () => Menu.SetStatus(ItemUnlocks.UnlockToolSlots()));

        SimpleMenu.Choice("Money to add", "Pick an amount, or Custom to type one.", _amount, AmountLabels, i => _amount = i);
        int amount;
        bool ok;
        if (_amount < Amounts.Length)
        {
            amount = Amounts[_amount];
            ok = true;
        }
        else
        {
            _customAmount = SimpleMenu.Text("Custom amount", _customAmount, 12, "For example 25000, 50k or 2m.");
            ok = UnlockPlan.TryParseAmount(_customAmount, out amount);
        }
        int? money = ItemUnlocks.Money;
        SimpleMenu.Button("Add money", ok ? $"+{amount:N0}" : "Type an amount",
            money != null ? $"You have {money.Value:N0}." : "Adds to your wallet.",
            () => Menu.SetStatus(ItemUnlocks.AddMoney(amount)), ok);
        SimpleMenu.Note("Liveries go car by car under Cars, or all at once with Unlock all liveries.");
    }

    // ---------------------------------------------------------------- press twice

    private static void Bulk(Unlocker.What what, string label, string hint, bool can) =>
        Confirmed("bulk:" + what, label, can ? "Ready" : "", hint, () => Unlocker.Run(what), can);

    /// <summary>A button that needs a second press within a few seconds; <paramref name="run"/> returns the status.</summary>
    private static void Confirmed(string key, string label, string value, string hint, Func<string> run, bool enabled)
    {
        bool armed = _armed == key;
        SimpleMenu.Button(label, armed ? "Press again" : value, hint, () =>
        {
            if (!armed)
            {
                _armed = key;
                _armedAt = Time.unscaledTime;
                Menu.SetStatus($"{label}: press again to confirm.");
                return;
            }
            _armed = null;
            Menu.SetStatus(run());
        }, enabled);
    }
}
