using System;

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// Whose build counts, and whether it is a free-play one (free: this mod puts its own tune layer on the car; not free: the
/// career's upgrades, or none). Pure (unit-tested).
/// </summary>
public static class MeerkatBuildRules
{
    /// <summary>
    /// This game's own Meerkat. A career that owns one: its fitted upgrades. A career playing without one (the Meerkat
    /// is a free-play car here, a career guest's for example): the car as the dealer sells it, with nothing on top, never
    /// the free-play page's build. No career: the page's choices.
    /// </summary>
    public static MeerkatBuild Local(bool owns, bool careerPlaying, Func<MeerkatBuild> careerBuild, MeerkatBuild freeBuild,
        out bool free)
    {
        free = false;
        if (owns) return careerBuild();
        if (careerPlaying) return MeerkatBuild.Dealer;
        free = true;
        return freeBuild;
    }

    /// <summary>
    /// On the host: what a guest's claimed build counts as. A host playing a career (one is loaded and the rules have
    /// careers on) whose rules don't let guests' upgrades count: the dealer's car, no layer. Anything else (plain free play,
    /// where the rules still default to careers on but no career is loaded): the guest's own claim.
    /// </summary>
    public static MeerkatBuild Guest(MeerkatBuild claimed, bool claimedFree, bool hostCareerLoaded, bool careerOn,
        bool guestUpgrades, out bool free)
    {
        if (hostCareerLoaded && careerOn && !guestUpgrades)
        {
            free = false;
            return MeerkatBuild.Dealer;
        }
        free = claimedFree;
        return claimed;
    }

    /// <summary>The page's arrows on a priced choice: true when landing on option <paramref name="owned"/> may switch the
    /// car at once (free play without prices, a choice that costs nothing, or one already bought); false when it only
    /// moves the preview (the Buy row charges). A step never charges.</summary>
    public static bool StepApplies(bool prices, bool costsNothing, bool owned) => !prices || costsNothing || owned;
}
