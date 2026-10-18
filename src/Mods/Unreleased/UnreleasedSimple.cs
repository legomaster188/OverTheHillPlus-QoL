using System;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.Unreleased;

/// <summary>Unreleased Cars' MODS page: a switch and an "Add to my garage" button per car.</summary>
internal static class UnreleasedSimple
{
    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (UnreleasedConfig.File == null) return;
        if (UnreleasedPlugin.FailedReason != null)
        {
            SimpleMenu.Note($"Off this session: {UnreleasedPlugin.FailedReason}. The cars aren't set up, so nothing of them reaches a save.");
            return;
        }
        SimpleMenu.Heading("Cars");
        if (!UnreleasedCars.Ready) SimpleMenu.Note("Setting the cars up: they're ready once the main menu has loaded.");
        string guest = GuestGate.Blocker;
        if (guest != null) SimpleMenu.Note(guest);
        string copies = GuestGate.CopiesBlocker;
        if (copies != null) SimpleMenu.Note(copies);
        string addBlocker = AddBlocker();

        foreach (var car in UnreleasedCars.All)
        {
            SimpleMenu.Toggle(car.Enabled, car.Name, $"Show the {car.Name} in your garage once it's added. {car.Description}");
            bool owned = car.Registered && UnreleasedPatches.Owns(car.Id);
            string value = owned ? "In your garage"
                : UnreleasedCars.Ready && !car.Registered ? "Unavailable"
                : addBlocker != null ? "" : "Add";
            string hint = owned ? $"Pick the {car.Name} in the garage."
                : UnreleasedCars.Ready && !car.Registered ? $"The {car.Name} {car.Problem ?? "isn't available"}."
                : addBlocker ?? $"Adds the {car.Name} to your profile's garage. Achievements pause for this session.";
            SimpleMenu.Button($"Add the {car.Name} to my garage", value, hint, () => Menu.SetStatus(Add(car)),
                !owned && car.Registered && addBlocker == null);
        }
        if (UnreleasedConfig.AutoAdd != null)
            SimpleMenu.Toggle(UnreleasedConfig.AutoAdd, "Add them all automatically", "Every switched-on car is added to your garage by itself once a profile is loaded.");
        SimpleMenu.Note("Guests need a host with this mod to drive them. Your save stays loadable without the mod: " +
                        "the cars are kept in oth.unreleased.stash.txt, and a stock car stands in while one is in use.");
    }

    private static float _nextAuto;

    /// <summary>With "add them all automatically" on: adds each switched-on car that isn't in the garage yet, as soon as
    /// adding is possible (checked every 2 s, one car per check).</summary>
    internal static void AutoAddTick()
    {
        if (UnreleasedConfig.AutoAdd == null || !UnreleasedConfig.AutoAdd.Value) return;
        if (UnityEngine.Time.unscaledTime < _nextAuto) return;
        _nextAuto = UnityEngine.Time.unscaledTime + 2f;
        try
        {
            if (AddBlocker() != null) return;
            foreach (var car in UnreleasedCars.All)
            {
                if (!car.IsOn || !car.Registered || UnreleasedPatches.Owns(car.Id)) continue;
                UnreleasedPlugin.Logger.LogInfo("[Unreleased] auto add: " + Add(car));
                return;
            }
        }
        catch (Exception e) { DebugLog.Error("unreleased", e, "UnreleasedSimple.AutoAddTick"); }
    }

    /// <summary>Why a car can't be added right now, or null.</summary>
    private static string AddBlocker()
    {
        if (!UnreleasedCars.Ready) return "Wait for the main menu.";
        if (!GameState.IsAuthority) return "Host or solo only: as a guest the host keeps the cars.";
        if (SaveGuard.Inventory() == null) return "Load a profile first.";
        try { if (OverlandSaver.CurrentProfileSave == null) return "Load a profile first."; }
        catch (Exception ex) { DebugLog.Error("unreleased", ex, "UnreleasedSimple.AddBlocker"); return "Load a profile first."; }
        return null;
    }

    private static string Add(UnreleasedCar car)
    {
        string blocked = AddBlocker();
        if (blocked != null) return blocked;
        if (!car.Registered) return $"The {car.Name} {car.Problem ?? "isn't available"}.";
        if (UnreleasedPatches.Owns(car.Id)) return $"The {car.Name} is in your garage already.";
        try
        {
            AdvantageFlag.MarkUsed(); // core: achievements stay locked for the session
            SaveGuard.Inventory().AddVehicleItem(car.Id, 0, 1, false, true);
            try { OverlandSaver.SaveGame(); }
            catch (Exception e) { DebugLog.Error("unreleased", e, "UnreleasedSimple.Add"); UnreleasedPlugin.Logger.LogWarning("[Unreleased] save after adding failed: " + e.GetBaseException().Message); }
            UnreleasedPlugin.Logger.LogInfo($"[Unreleased] the {car.Name} was added to the garage");
            return car.IsOn ? $"The {car.Name} is in your garage: pick it there." : $"The {car.Name} was added; switch it on to see it in the garage.";
        }
        catch (Exception e)
        {
            UnreleasedPlugin.Logger.LogError($"[Unreleased] adding the {car.Name} failed: {e}");
            return $"Adding the {car.Name} failed (see the log).";
        }
    }
}
