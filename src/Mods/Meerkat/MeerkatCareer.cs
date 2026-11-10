using System;
using OverTheHillPlus.Api;

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// The Meerkat in a career (core's Api.Career): listed at the dealer, its upgrades registered for this car only. The career
/// mod puts a career's bought upgrades on the car as a tune layer itself (on the host for every career guest too); this
/// mod reads which are fitted for what the car shows and its tyres. Without a career all of this does nothing.
/// </summary>
internal static class MeerkatCareer
{
    private static float _travel = MeerkatUpgrades.AssumedTravel;

    internal static void Install()
    {
        try { Career.Dealer.List(MeerkatPlugin.ModId, MeerkatDefinition.DealerOffer()); }
        catch (Exception e) { MeerkatPlugin.Logger.LogWarning($"Meerkat: couldn't list it at the career dealer: {e.Message}"); }
        Register();
    }

    /// <summary>The template's suspension travel is known (MeerkatRig): the lift stages are registered again with the
    /// travel multipliers that give exactly their heights.</summary>
    internal static void TravelKnown(float travel)
    {
        if (!(travel > 0.05f) || Math.Abs(travel - _travel) < 0.0005f) return;
        _travel = travel;
        Register();
        MeerkatPlugin.Logger?.LogInfo($"[Meerkat] suspension travel {travel:0.000} m: lift stages x{MeerkatBuild.TravelFactor(LiftStage.Stock, travel):0.000} " +
                                      $"/ x{MeerkatBuild.TravelFactor(LiftStage.TwoInch, travel):0.000} / x{MeerkatBuild.TravelFactor(LiftStage.FourInch, travel):0.000}.");
    }

    internal static float Travel => _travel;

    private static void Register()
    {
        foreach (var d in MeerkatUpgrades.All(_travel))
        {
            try
            {
                if (!Career.Upgrades.Register(MeerkatPlugin.ModId, d))
                    MeerkatPlugin.LogOnce("upgrade " + d.Id, $"the career refused the upgrade {d.Id}");
            }
            catch (Exception e) { MeerkatPlugin.LogOnce("upgrade-x " + d.Id, $"registering {d.Id}: {e.Message}"); }
        }
    }

    /// <summary>True when this game's career owns a Meerkat (its build then comes from the career's upgrades).</summary>
    internal static bool Owns => Career.Playing && Career.Garage.Owns(MeerkatDefinition.CarName);

    /// <summary>This game's Meerkat's build (<see cref="MeerkatBuildRules.Local"/>): the career's fitted upgrades; in a career
    /// that doesn't own one, the dealer's car with nothing on top; the free-play choices only outside a career.</summary>
    internal static MeerkatBuild LocalBuild(out bool free)
    {
        string car = MeerkatDefinition.CarName;
        return MeerkatBuildRules.Local(Owns, Career.Playing,
            () => MeerkatUpgrades.FromCareer(id => Career.Garage.Fitted(car, id), id => Career.Garage.Level(car, id)),
            MeerkatConfig.FreeBuild, out free);
    }

    /// <summary>On the host: what a guest's claim counts as (<see cref="MeerkatBuildRules.Guest"/>; only a host that is
    /// playing a career and doesn't let guests' upgrades count changes it).</summary>
    internal static MeerkatBuild GuestBuild(MeerkatBuild claimed, bool claimedFree, out bool free)
    {
        bool loaded = Career.Active;
        bool on = loaded && Career.Rules.CareerOn;
        bool guests = !on || Career.Rules.Current.GuestUpgrades;
        return MeerkatBuildRules.Guest(claimed, claimedFree, loaded, on, guests, out free);
    }
}
