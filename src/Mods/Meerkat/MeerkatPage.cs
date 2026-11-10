using System;
using System.Collections.Generic;
using OverTheHillPlus.Api;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.Meerkat;

/// <summary>The Meerkat's build on its MODS page (under the framework's paint, livery and engine rows). Free play: pick
/// everything here, for nothing (or, with "Prices in free play" on, each choice the car doesn't come with once for its price
/// in the game's pebbles). In a career the bought upgrades are switched on and off here; the career sells them.</summary>
internal static class MeerkatPage
{
    private static readonly string[] Lifts = { "Stock height", "2 in lift", "3 in lift", "4 in lift" };
    private static readonly string[] Tyres = { "Road", "All-terrain", "Mud-terrain" };
    private static readonly string[] Tops = { "Hardtop", "Soft top", "No top" };
    private static readonly string[] Engines = { "Stock", "Intake and exhaust", "Cams and turbo kit" };

    private static readonly string[] LiftIds = { MeerkatUpgrades.Lift0, MeerkatUpgrades.Lift2, null, MeerkatUpgrades.Lift4 };
    private static readonly string[] TyreIds = { MeerkatUpgrades.TyreStreet, null, MeerkatUpgrades.TyreMud };
    private static readonly string[] TopIds = { null, MeerkatUpgrades.TopSoft, MeerkatUpgrades.TopOff };

    internal static void Draw()
    {
        if (MeerkatConfig.Lift == null) return;
        SimpleMenu.Heading("Build");
        if (MeerkatCareer.Owns) { DrawCareer(); return; }
        if (Career.Playing)
        {
            // a career without a Meerkat drives it as the dealer sells it: no free-play build on top
            SimpleMenu.Note("Your career doesn't own a Meerkat: it drives as the dealer sells it. Buy one at the dealer, then " +
                            "its upgrades in the career's upgrades.");
            SimpleMenu.Toggle(MeerkatConfig.PopUps, "Pop-up headlamps", "The headlamps rise when the lights come on (off: they stay up).");
            return;
        }
        bool prices = MeerkatConfig.PricesInFreePlay.Value && !Career.Active;
        SimpleMenu.Note(prices
            ? $"Free play with prices: each new choice costs its price once ({MeerkatShop.Pebbles():N0} pebbles to spend). Other players with this mod see your build."
            : "Free play: pick the lift, tyres, top and parts for nothing. Other players with this mod see your build.");
        var b = MeerkatConfig.FreeBuild;
        Choice("Lift", "How high it stands. Stock height runs the 14 in wheels, the 4 in lift a size bigger tyres.", (int)b.Lift, Lifts, i => LiftIds[i], 1,
            MeerkatConfig.Lift, prices);
        Choice("Tyres", "Road tyres grip on tarmac, mud-terrains in mud and snow; the all-terrains are in between.", (int)b.Tyre,
            Tyres, i => TyreIds[i], 1, MeerkatConfig.Tyre, prices);
        Choice("Top", "The hardtop carries the roof rack; without it the lamps sit on a bar over the windscreen.", (int)b.Top, Tops,
            i => TopIds[i], 1, MeerkatConfig.Top, prices);
        Choice("Engine", "The 1.8 twin-cam: stock, a freer intake and exhaust (+8 %), or cams and a turbo kit (+32 %).", b.Engine,
            Engines, i => i == 0 ? null : MeerkatUpgrades.Engine, 0, MeerkatConfig.Engine, prices);
        Part(MeerkatConfig.Skid, MeerkatUpgrades.Skid, "Skid plate", "The aluminium plate under the nose.", prices);
        Part(MeerkatConfig.Rack, MeerkatUpgrades.Rack, "Rack and lamps", "The roof rack with two driving lamps (a lamp bar without the hardtop).", prices);
        Part(MeerkatConfig.Flares, MeerkatUpgrades.Flares, "Fender flares", "Black flares over the arches.", prices);
        Part(MeerkatConfig.Flaps, MeerkatUpgrades.Flaps, "Mud flaps", "Rally flaps behind the wheels.", prices);
        Part(MeerkatConfig.Hook, MeerkatUpgrades.Hook, "Tow hook", "The tow eye out of the front mouth.", prices);
        Part(MeerkatConfig.Snorkel, MeerkatUpgrades.Snorkel, "Snorkel", "The raised air intake up the right pillar.", prices);
        Part(MeerkatConfig.Spare, MeerkatUpgrades.Spare, "Spare wheel", "The spare strapped flat on the trunk lid.", prices);
        SimpleMenu.Toggle(MeerkatConfig.PopUps, "Pop-up headlamps", "The headlamps rise when the lights come on (off: they stay up).");
        SimpleMenu.Toggle(MeerkatConfig.PricesInFreePlay, "Prices in free play",
            "Each choice the car doesn't come with costs its price once, in pebbles. Off: free outside a career.", !Career.Active);
        SimpleMenu.Button("Safari build", "Reset", "Back to the build it comes as: the 3 in lift, all-terrains, the hardtop with the " +
                          "rack, skid plate, flares, flaps and tow hook.", Reset);
    }

    // The option each priced choice's arrows point at (label -> index) while it isn't bought: a preview, not the car's.
    private static readonly Dictionary<string, int> Preview = new();

    /// <summary>A pick of several. The arrows never charge: with prices on, landing on a choice not bought yet only moves
    /// the preview (its price shows) and a Buy row under it charges and fits it; bought or free choices switch at once (the
    /// level is the option's index when <paramref name="levelIsIndex"/> is 0, else 1).</summary>
    private static void Choice(string label, string hint, int index, string[] names, Func<int, string> idOf, int levelIsIndex,
        OverTheHillPlus.Config.ConfigEntry<int> entry, bool prices)
    {
        var owned = prices ? MeerkatFreePrices.Parse(MeerkatConfig.Owned.Value) : null;
        bool Owned(int i) => !prices || idOf(i) == null || MeerkatFreePrices.Owned(owned, idOf(i), levelIsIndex == 0 ? i : 1);
        var shown = names;
        if (prices)
        {
            shown = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
                shown[i] = Owned(i) ? names[i]
                    : names[i] + " (" + MeerkatFreePrices.PriceOf(idOf(i), levelIsIndex == 0 ? i : 1).ToString("N0") + ")";
        }
        if (!prices) Preview.Remove(label);
        int at = Preview.TryGetValue(label, out int p) && p >= 0 && p < names.Length && p != index ? p : index;
        SimpleMenu.Choice(label, hint, at, shown, i =>
        {
            if (MeerkatBuildRules.StepApplies(prices, idOf(i) == null, Owned(i)))
            {
                Preview.Remove(label);
                Save(entry, i);
            }
            else Preview[label] = i;
        });
        if (at == index) return;
        int pick = at;
        string id = idOf(pick);
        int level = levelIsIndex == 0 ? pick : 1;
        long price = MeerkatFreePrices.PriceOf(id, level);
        SimpleMenu.Button("  Buy " + names[pick], "Buy " + price.ToString("N0"),
            $"Fits the {names[pick]} for {price:N0} pebbles (once: after that it switches for free).", () =>
            {
                if (!Buy(id, level, names[pick])) return;
                Preview.Remove(label);
                Save(entry, pick);
            });
    }

    private static void Part(OverTheHillPlus.Config.ConfigEntry<bool> entry, string id, string label, string hint, bool prices)
    {
        if (!prices || entry.Value || MeerkatFreePrices.Owned(MeerkatFreePrices.Parse(MeerkatConfig.Owned.Value), id))
        {
            SimpleMenu.Toggle(entry, label, hint);
            return;
        }
        long price = MeerkatFreePrices.PriceOf(id);
        SimpleMenu.Button(label, "Buy " + price.ToString("N0"), hint, () =>
        {
            if (!Buy(id, 1, label)) return;
            entry.Value = true;
            MeerkatConfig.File?.Save();
        });
    }

    private static bool Buy(string id, int level, string what)
    {
        var owned = MeerkatFreePrices.Parse(MeerkatConfig.Owned.Value);
        if (MeerkatFreePrices.Owned(owned, id, level)) return true;
        long price = MeerkatFreePrices.PriceOf(id, level);
        if (!MeerkatShop.Spend(price))
        {
            Menu.SetStatus($"Not enough pebbles for the {what} ({price:N0}).");
            return false;
        }
        owned.Add(MeerkatFreePrices.Key(id, level));
        MeerkatConfig.Owned.Value = MeerkatFreePrices.Format(owned);
        MeerkatConfig.File?.Save();
        Menu.SetStatus($"Bought: {what} ({price:N0} pebbles).");
        return true;
    }

    private static void Save(OverTheHillPlus.Config.ConfigEntry<int> e, int v)
    {
        e.Value = v;
        MeerkatConfig.File?.Save();
    }

    private static void Reset()
    {
        var d = MeerkatBuild.Safari;
        MeerkatConfig.Lift.Value = (int)d.Lift;
        MeerkatConfig.Tyre.Value = (int)d.Tyre;
        MeerkatConfig.Top.Value = (int)d.Top;
        MeerkatConfig.Engine.Value = d.Engine;
        MeerkatConfig.Skid.Value = MeerkatConfig.Rack.Value = MeerkatConfig.Flares.Value = MeerkatConfig.Flaps.Value = MeerkatConfig.Hook.Value = true;
        MeerkatConfig.Snorkel.Value = MeerkatConfig.Spare.Value = false;
        MeerkatConfig.File?.Save();
        Menu.SetStatus("Safari build.");
    }

    private static void DrawCareer()
    {
        string car = MeerkatDefinition.CarName;
        var b = MeerkatCareer.LocalBuild(out _);
        SimpleMenu.Note($"Your career's Meerkat: {Lifts[(int)b.Lift]}, {Tyres[(int)b.Tyre]} tyres, {Tops[(int)b.Top]}. Buy upgrades in " +
                        "the career's upgrades; switch the ones you own here.");
        var rows = new List<(string Id, string Label)>
        {
            (MeerkatUpgrades.Lift0, "Stock height"), (MeerkatUpgrades.Lift2, "2 in lift"), (MeerkatUpgrades.Lift4, "4 in lift"),
            (MeerkatUpgrades.TyreStreet, "Road tyres"), (MeerkatUpgrades.TyreMud, "Mud-terrain tyres"),
            (MeerkatUpgrades.TopSoft, "Soft top"), (MeerkatUpgrades.TopOff, "No top"),
            (MeerkatUpgrades.Skid, "Skid plate"), (MeerkatUpgrades.Rack, "Rack and lamps"), (MeerkatUpgrades.Flares, "Fender flares"),
            (MeerkatUpgrades.Flaps, "Mud flaps"), (MeerkatUpgrades.Hook, "Tow hook"), (MeerkatUpgrades.Snorkel, "Snorkel"),
            (MeerkatUpgrades.Spare, "Spare wheel"), (MeerkatUpgrades.Engine, "Engine stage"),
        };
        bool any = false;
        foreach (var (id, label) in rows)
        {
            if (Career.Garage.Level(car, id) <= 0) continue;
            any = true;
            bool on = Career.Garage.Fitted(car, id);
            string uid = id;
            SimpleMenu.Button(label, on ? "On" : "Off", on ? "Fitted. Click to take it off (you keep it)." : "Owned. Click to fit it.",
                () => { Career.Garage.Fit(car, uid, !on); Menu.SetStatus(on ? label + " off." : label + " on."); });
        }
        if (!any) SimpleMenu.Note("No Meerkat upgrades bought yet.");
        SimpleMenu.Toggle(MeerkatConfig.PopUps, "Pop-up headlamps", "The headlamps rise when the lights come on (off: they stay up).");
    }
}
