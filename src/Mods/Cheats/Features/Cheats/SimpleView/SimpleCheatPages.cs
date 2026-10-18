using System.Collections.Generic;
using System.Linq;
using OverTheHillPlus.CheatsPlus;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.SimpleView;

/// <summary>
/// Cheats+'s everyday rows, first on its MODS page (the old CHEATS menu's Simple view): the popular ones (car assists,
/// no damage, fly, nitro, jump, pebbles, car switch). Every row writes the same settings as the Advanced fold, where
/// everything else is. (Moving players is Host Powers', the AI convoy Convoy AI's.)
/// </summary>
internal static class SimpleCheatPages
{
    private static readonly string[] OffOn = { "Off", "On" };
    private static readonly float[] GripSteps = { 1f, 1.25f, 1.5f, 2f, 3f };
    private static readonly float[] AccelSteps = { 0f, 3f, 6f, 10f, 20f };
    private static readonly float[] FlySteps = { 10f, 25f, 40f, 60f, 80f };
    private static readonly float[] NitroSteps = { 10f, 18f, 30f, 45f, 60f };
    private static readonly float[] JumpSteps = { 5f, 9f, 15f, 22f, 30f };

    private const string GuestNote = "Needs you to be solo or hosting: the host's game simulates every car.";

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (CheatsConfig.NoDamage == null) return;
        SimpleMenu.TitledPage("Car", Car, true);
        SimpleMenu.TitledPage("Moves", Moves, false);
        SimpleMenu.TitledPage("Garage", Garage, false);
    }

    // ---------------------------------------------------------------- Car

    private static void Car()
    {
        bool phys = GameState.PhysicsAllowed;
        if (!phys) SimpleMenu.Note(GuestNote);
        SimpleMenu.Toggle(CheatsConfig.NoDamage, "No damage", "Your car takes no damage.", phys);
        SimpleMenu.Toggle(CheatsConfig.AssistsEnabled, "Driving assists", "Master switch for the grip, anti-slip, traction and anti-rollover rows below.", phys);
        bool assists = phys && CheatsConfig.AssistsEnabled.Value;
        SimpleMenu.FloatChoice("Extra grip", "Tire grip on every surface.", CheatsConfig.GripMultiplier.Value, GripSteps,
            v => v <= 1.001f ? "Stock" : v.ToString("0.##") + "x", v => CheatsConfig.GripMultiplier.Value = v, assists);
        SimpleMenu.Toggle(CheatsConfig.SlipperyBoost, "Anti-slip", "Extra grip on mud, snow, ice, sand, water and loose rock.", assists);
        SimpleMenu.Toggle(CheatsConfig.TractionControl, "Traction control", "Eases off the throttle when the wheels spin.", assists);
        SimpleMenu.Toggle(CheatsConfig.AntiRoll, "Anti-rollover", "Pushes the car back upright when it leans hard.", assists);
        SimpleMenu.FloatChoice("Extra acceleration", "A push forward while you hold the throttle.", CheatsConfig.ExtraAcceleration.Value, AccelSteps,
            v => v <= 0f ? "Off" : "+" + v.ToString("0"), v => CheatsConfig.ExtraAcceleration.Value = v, phys);
        SimpleMenu.Button("Repair car", null, "Fixes all damage.", () => { Assists.RepairNow(); Menu.SetStatus("Repaired car"); }, phys);
        SimpleMenu.Button("Flip upright", CheatsConfig.FlipKey.Value.ToString(), "Puts the car back on its wheels.",
            () => { Assists.FlipUpright(); Menu.SetStatus("Flipped car"); }, phys);
    }

    // ---------------------------------------------------------------- Moves

    private static void Moves()
    {
        bool phys = GameState.PhysicsAllowed;
        if (!phys) SimpleMenu.Note(GuestNote);
        SimpleMenu.Choice("Fly mode", $"WASD, Space up, Ctrl down, Shift fast. {CheatsConfig.FlyKey.Value} toggles it.",
            Movement.Flying ? 1 : 0, OffOn, i => Movement.SetFlying(i == 1), phys);
        SimpleMenu.FloatChoice("Fly speed", "Shift flies three times as fast.", CheatsConfig.FlySpeed.Value, FlySteps,
            v => v.ToString("0") + " m/s", v => CheatsConfig.FlySpeed.Value = v, phys);
        SimpleMenu.FloatChoice("Nitro power", $"Hold {CheatsConfig.NitroKey.Value} for nitro.", CheatsConfig.NitroAcceleration.Value, NitroSteps,
            v => v.ToString("0"), v => CheatsConfig.NitroAcceleration.Value = v, phys);
        SimpleMenu.FloatChoice("Super jump", $"Press {CheatsConfig.JumpKey.Value} to jump.", CheatsConfig.JumpSpeed.Value, JumpSteps,
            v => v.ToString("0") + " m/s", v => CheatsConfig.JumpSpeed.Value = v, phys);
    }

    // ---------------------------------------------------------------- Garage

    private static int _car = -1;
    private static string[] _carNames = new string[0];
    private static List<ItemCatalog.Item> _cars = new();
    private static float _carsAt = -10f;

    private static void Garage()
    {
        int balance = Economy.Balance;
        SimpleMenu.Heading("Pebbles");
        string bal = balance < 0 ? "Load a world first" : $"Have {balance:N0}";
        SimpleMenu.Button("Add 10,000 pebbles", bal, "The game's money, saved to your own profile.",
            () => Menu.SetStatus(Economy.AddPebbles(10000)), balance >= 0);
        SimpleMenu.Button("Add 100,000 pebbles", bal, "The game's money, saved to your own profile.",
            () => Menu.SetStatus(Economy.AddPebbles(100000)), balance >= 0);

        SimpleMenu.Heading("Switch car");
        RefreshCars();
        if (_cars.Count == 0)
        {
            SimpleMenu.Note("Load into a world to switch cars.");
            return;
        }
        if (_car < 0 || _car >= _cars.Count)
        {
            var current = Presets.CurrentCar();
            _car = current == null ? 0 : Mathf.Max(0, _cars.FindIndex(c => c.Name == current.Name));
        }
        SimpleMenu.Choice("Car", "Pick a car with the arrows, then press Switch.", _car, _carNames, i => _car = i);
        var pick = _cars[_car];
        SimpleMenu.Button("Switch to it", pick.Name, "Swaps your car right where you are.", () => Menu.SetStatus(Vehicles.SwitchCar(pick)));
    }

    /// <summary>The car list, re-read every few seconds (not on every IMGUI event).</summary>
    private static void RefreshCars()
    {
        if (Time.unscaledTime - _carsAt < 3f && _cars.Count > 0) return;
        _carsAt = Time.unscaledTime;
        var cars = ItemCatalog.Cars().ToList();
        if (cars.Count == _cars.Count && cars.Select(c => c.Name).SequenceEqual(_carNames)) return;
        string keep = _car >= 0 && _car < _cars.Count ? _cars[_car].Name : null;
        _cars = cars;
        _carNames = cars.Select(c => c.Name).ToArray();
        _car = keep == null ? -1 : _cars.FindIndex(c => c.Name == keep);
    }
}
