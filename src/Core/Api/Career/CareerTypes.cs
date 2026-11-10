using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Api;

// The data types of Api.Career (docs/career-api.md). Plain data with no game types, so mods can build them anywhere and
// the tests can check them. Every text and number coming in is checked again where it is used.

/// <summary>A spot in the world: a name, a position (Unity axes, metres) and how close counts (metres, on the ground plane).
/// With <see cref="FollowPlayer"/> set it follows that player's car instead (a stuck car to help, say).</summary>
public readonly struct Place
{
    public readonly string Name;
    public readonly float X, Y, Z, Radius;
    /// <summary>The player (PlayerRef.RawEncoded) whose car this place follows; 0: a fixed spot.</summary>
    public readonly int FollowPlayer;

    public Place(string name, float x, float y, float z, float radius = 25f, int followPlayer = 0)
    {
        Name = name ?? "";
        X = x; Y = y; Z = z;
        Radius = float.IsFinite(radius) ? Math.Clamp(radius, 2f, 500f) : 25f;
        FollowPlayer = followPlayer;
    }

    /// <summary>A place that follows a player's car (the position is where it was last seen).</summary>
    public static Place Player(string name, int player, float x, float y, float z, float radius = 15f) => new(name, x, y, z, radius, player);

    /// <summary>Finite coordinates within 200 km of the origin. An unset Place (default) isn't valid, though its zeros are
    /// numbers: every constructor sets a name, so "no place" never passes for the world's origin.</summary>
    public bool IsValid => Name != null && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) &&
                           Math.Abs(X) < 200000f && Math.Abs(Y) < 200000f && Math.Abs(Z) < 200000f;

    /// <summary>The same place at another position (a followed car moved).</summary>
    public Place MovedTo(float x, float y, float z) => new(Name, x, y, z, Radius, FollowPlayer);

    /// <summary>Distance on the ground plane (height ignored: a spot on a slope still counts).</summary>
    public float GroundDistance(float x, float z)
    {
        float dx = x - X, dz = z - Z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>True when (x, z) is within <see cref="Radius"/>.</summary>
    public bool Contains(float x, float z) => IsValid && float.IsFinite(x) && float.IsFinite(z) && GroundDistance(x, z) <= Radius;

    public override string ToString() => $"{Name} ({X:0}, {Z:0})";
}

/// <summary>One change of a career's money.</summary>
public readonly struct WalletChange
{
    /// <summary>What changed (positive: earned, negative: spent).</summary>
    public readonly long Delta;
    /// <summary>The balance after it.</summary>
    public readonly long Balance;
    /// <summary>Why, in a few plain words ("Delivery to the depot", "Engine stage 2").</summary>
    public readonly string Reason;
    /// <summary>Who caused it (a mod id, "job", "merchant", ...), or "".</summary>
    public readonly string Source;
    /// <summary>When (Unix seconds, UTC).</summary>
    public readonly long When;

    public WalletChange(long delta, long balance, string reason, string source, long when)
    {
        Delta = delta;
        Balance = balance;
        Reason = reason ?? "";
        Source = source ?? "";
        When = when;
    }
}

/// <summary>A car for sale at the dealer.</summary>
public sealed class CarOffer
{
    /// <summary>The car's key: the name the game knows it by (its CarInfo name: "Brick", "Pixie", "Hilo").</summary>
    public string Car;
    /// <summary>Shown name ("Hilo 89").</summary>
    public string Name;
    /// <summary>One plain sentence.</summary>
    public string Description;
    /// <summary>The price in pebbles.</summary>
    public long Price;
    /// <summary>"car", "truck", "utv", "snowmobile", "boat", ... (groups the dealer list).</summary>
    public string Category = "car";
    /// <summary>The unlock it needs before it is for sale (null: none).</summary>
    public string Unlock;
    /// <summary>One of the cheap cars a new career can start with.</summary>
    public bool Starter;
    /// <summary>
    /// <see cref="Price"/> is a base price on the career's own table (a small trail 4x4 is about 12,000) and the dealer sells
    /// it on the career's money scale (<see cref="Career.Dealer.MoneyScale"/>). Leave it off for a price already in the
    /// game's money. Mods that add vehicles should set it.
    /// </summary>
    public bool OnMoneyScale;
    /// <summary>The mod that listed it (set by <see cref="Career.Dealer.List"/>).</summary>
    public string Owner;

    internal CarOffer Copy() => (CarOffer)MemberwiseClone();
}

/// <summary>One level of an upgrade: its name, price and what it does.</summary>
public sealed class UpgradeLevel
{
    /// <summary>"Stage 2", "Mud-terrain", "Short ratio".</summary>
    public string Name;
    /// <summary>Base price in pebbles (a career scales it by the car's value: <see cref="Career.Upgrades.PriceFor"/>).</summary>
    public long Price;
    /// <summary>What the car gets at this level (not added to the levels below: each level says its whole effect).</summary>
    public CarUpgradeEffect Effect = CarUpgradeEffect.None;
    /// <summary>One plain line ("+12% power").</summary>
    public string Note;
    /// <summary>The unlock this level needs before it is sold (null: none).</summary>
    public string Unlock;
}

/// <summary>An upgrade a car can get: levels bought one after the other (engine stages 1-5), or a single part (one level).</summary>
public sealed class UpgradeDef
{
    /// <summary>"engine", "turbo", "tread.mud": 2-40 of a-z, 0-9, '_', '.', '-'.</summary>
    public string Id;
    public string Name;
    public string Description;
    /// <summary>"engine", "drivetrain", "chassis", "tyres", "tools": groups the garage list.</summary>
    public string Category = "engine";
    /// <summary>Upgrades with the same group exclude each other (fitting one takes the others off): a short and a long
    /// final drive. Null: none.</summary>
    public string Group;
    /// <summary>The levels, lowest first (1 to <see cref="Career.Upgrades.MaxLevels"/>).</summary>
    public List<UpgradeLevel> Levels = new();
    /// <summary>The unlock it needs before it is sold (null: none).</summary>
    public string Unlock;
    /// <summary>Only these cars can get it (car keys); null or empty: every car.</summary>
    public string[] Cars;
    /// <summary>The mod that registered it (set by <see cref="Career.Upgrades.Register"/>).</summary>
    public string Owner;

    /// <summary>True when <paramref name="car"/> can get this upgrade.</summary>
    public bool Fits(string car)
    {
        if (Cars == null || Cars.Length == 0) return true;
        foreach (var c in Cars)
            if (string.Equals(c, car, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>A name for an unlock tag, for the career screen.</summary>
public sealed class UnlockDef
{
    /// <summary>"radio_tower_done", "market": 2-40 of a-z, 0-9, '_', '.', '-'.</summary>
    public string Tag;
    public string Name;
    public string Description;
    /// <summary>Not listed until it is unlocked.</summary>
    public bool Hidden;
    public string Owner;
}

/// <summary>Something a player carries and can sell: a fish, a crate of cargo.</summary>
public sealed class GoodDef
{
    /// <summary>"fish.lake_trout", "cargo.timber": 2-40 of a-z, 0-9, '_', '.', '-'.</summary>
    public string Id;
    public string Name;
    /// <summary>"kg", "crate", "load"...</summary>
    public string Unit = "kg";
    /// <summary>What one unit sells for, pebbles.</summary>
    public double Price;
    /// <summary>The most one career can hold (more is refused).</summary>
    public double MaxCarry = 1000;
    public string Owner;
}

/// <summary>
/// The rules the host picks for a session (solo: the player's own): the career's money, which parts of the overhaul are on,
/// and whether the host tools work. Every modded guest follows the host's (mod-net channel 48); vanilla players
/// never see them. Members are only ever added; a new one defaults to how things were before it existed.
/// </summary>
public sealed class CareerRuleSet
{
    /// <summary>Careers earn and spend in this session. Off: everyone plays free.</summary>
    public bool CareerOn = true;
    /// <summary>Job pay is multiplied by this (0.25 to 4), on top of <see cref="MoneyScale"/>.</summary>
    public float PayoutMultiplier = 1f;
    /// <summary>A finished job's unlocks go to every career player in the session, not only the one who did it.</summary>
    public bool SharedProgress;
    /// <summary>Co-op: players who helped with a job (near the driver at its stages) get <see cref="HelperShare"/> of its pay.</summary>
    public bool JobSharing = true;
    /// <summary>Guests' bought upgrades count on their cars. Off: guests drive their cars as they come.</summary>
    public bool GuestUpgrades = true;

    /// <summary>All money at once: pay and prices are multiplied by this (0.5 to 2).</summary>
    public float MoneyScale = 1f;
    /// <summary>What a new career starts with, in the game's base pebbles (before the career's own price scale).</summary>
    public long StartingMoney = DefaultStartingMoney;
    /// <summary>Guests play their own careers here (what they earn and buy sticks). Off: guests play free in this session.</summary>
    public bool GuestProgress = true;
    /// <summary>Car prices at the dealer are multiplied by this (0.25 to 4), on top of <see cref="MoneyScale"/>.</summary>
    public float CarPrices = 1f;
    /// <summary>Upgrades and parts cost money. Off: they're free.</summary>
    public bool PartsCost = true;

    /// <summary>Haulage's jobs: flatbed loads, far supply runs, the radio tower, semi runs.</summary>
    public bool Haulage = true;
    /// <summary>The player market (after the radio tower).</summary>
    public bool Market = true;
    /// <summary>Road clearing and the plow.</summary>
    public bool Plowing = true;
    /// <summary>Trips to the big map (Skadi's Hollow).</summary>
    public bool MapV2 = true;
    /// <summary>The extra vehicles at the dealer: side-by-sides, snowmobiles, boats.</summary>
    public bool Vehicles = true;
    /// <summary>The host tools work (solo and for the host only, always).</summary>
    public bool HostPowers = true;

    /// <summary>Counts the host's changes in a session (not a rule: <see cref="SameAs"/> skips it).</summary>
    public int Revision;

    /// <summary>What each helper gets of a shared job's pay.</summary>
    public const float HelperShare = 0.5f;
    public const float MinMultiplier = 0.25f, MaxMultiplier = 4f;
    public const float MinMoneyScale = 0.5f, MaxMoneyScale = 2f;
    public const long DefaultStartingMoney = 1500, MaxStartingMoney = 1_000_000;

    /// <summary>Ids for <see cref="Allows"/> and <see cref="Career.Rules.Allows"/>.</summary>
    public static class Features
    {
        public const string Haulage = "haulage", Market = "market", Plowing = "plowing", Map = "map", Vehicles = "vehicles", HostPowers = "hostpowers";
        internal static readonly string[] All = { Haulage, Market, Plowing, Map, Vehicles, HostPowers };
    }

    /// <summary>Dealer categories <see cref="Vehicles"/> covers.</summary>
    public static readonly string[] VehicleCategories = { "utv", "snowmobile", "boat" };

    /// <summary>The plow (Haulage's, the only one): <see cref="Plowing"/> covers it.</summary>
    public const string PlowUpgrade = "haulage.plow";
    /// <summary>The job kind of road clearing.</summary>
    public const string ClearingKind = "clearing";
    /// <summary>The mod id whose jobs <see cref="Haulage"/> covers.</summary>
    public const string HaulageOwner = "haulage";

    public CareerRuleSet Clamped() => new()
    {
        CareerOn = CareerOn,
        PayoutMultiplier = Clamp(PayoutMultiplier, MinMultiplier, MaxMultiplier),
        SharedProgress = SharedProgress,
        JobSharing = JobSharing,
        GuestUpgrades = GuestUpgrades,
        MoneyScale = Clamp(MoneyScale, MinMoneyScale, MaxMoneyScale),
        StartingMoney = Math.Clamp(StartingMoney, 0, MaxStartingMoney),
        GuestProgress = GuestProgress,
        CarPrices = Clamp(CarPrices, MinMultiplier, MaxMultiplier),
        PartsCost = PartsCost,
        Haulage = Haulage,
        Market = Market,
        Plowing = Plowing,
        MapV2 = MapV2,
        Vehicles = Vehicles,
        HostPowers = HostPowers,
        Revision = Math.Max(0, Revision),
    };

    private static float Clamp(float v, float min, float max) => float.IsFinite(v) ? Math.Clamp(v, min, max) : 1f;

    public CareerRuleSet Copy() => (CareerRuleSet)MemberwiseClone();

    /// <summary>Every rule the same (the revision aside).</summary>
    public bool SameAs(CareerRuleSet o) => o != null && CareerOn == o.CareerOn && Math.Abs(PayoutMultiplier - o.PayoutMultiplier) < 1e-4f &&
                                          SharedProgress == o.SharedProgress && JobSharing == o.JobSharing && GuestUpgrades == o.GuestUpgrades &&
                                          Math.Abs(MoneyScale - o.MoneyScale) < 1e-4f && StartingMoney == o.StartingMoney && GuestProgress == o.GuestProgress &&
                                          Math.Abs(CarPrices - o.CarPrices) < 1e-4f && PartsCost == o.PartsCost && Haulage == o.Haulage && Market == o.Market &&
                                          Plowing == o.Plowing && MapV2 == o.MapV2 && Vehicles == o.Vehicles && HostPowers == o.HostPowers;

    /// <summary>What job pay is multiplied by: <see cref="PayoutMultiplier"/> times <see cref="MoneyScale"/> (0.25 to 4).</summary>
    public float PayScale => Clamp(PayoutMultiplier * MoneyScale, MinMultiplier, MaxMultiplier);

    /// <summary>What dealer prices are multiplied by: <see cref="CarPrices"/> times <see cref="MoneyScale"/> (0.25 to 4).</summary>
    public float PriceScale => Clamp(CarPrices * MoneyScale, MinMultiplier, MaxMultiplier);

    /// <summary>What upgrade prices are multiplied by: 0 when parts are free, else <see cref="MoneyScale"/>.</summary>
    public float PartsScale => PartsCost ? Clamp(MoneyScale, MinMoneyScale, MaxMoneyScale) : 0f;

    /// <summary>A feature by its id (<see cref="Features"/>); true for ids it doesn't know.</summary>
    public bool Allows(string feature) => feature switch
    {
        Features.Haulage => Haulage,
        Features.Market => Market,
        Features.Plowing => Plowing,
        Features.Map => MapV2,
        Features.Vehicles => Vehicles,
        Features.HostPowers => HostPowers,
        _ => true,
    };

    /// <summary>A job template may be offered: road clearing needs <see cref="Plowing"/>, Haulage's other jobs <see cref="Haulage"/>.</summary>
    public bool AllowsJob(string owner, string kind)
    {
        if (string.Equals(kind, ClearingKind, StringComparison.Ordinal)) return Plowing;
        if (string.Equals(owner, HaulageOwner, StringComparison.Ordinal)) return Haulage;
        return true;
    }

    /// <summary>An upgrade may be bought: the plow needs <see cref="Plowing"/>.</summary>
    public bool AllowsUpgrade(string id) => Plowing || !string.Equals(id, PlowUpgrade, StringComparison.Ordinal);

    /// <summary>A dealer category may be sold: the extra vehicles need <see cref="Vehicles"/>.</summary>
    public bool AllowsCategory(string category) => Vehicles || category == null || Array.IndexOf(VehicleCategories, category) < 0;
}

// ---------------------------------------------------------------- jobs

/// <summary>What a stage of a job asks for.</summary>
public enum StageKind : byte
{
    /// <summary>Get to <see cref="JobStage.At"/>.</summary>
    Reach = 0,
    /// <summary>Pick something up at <see cref="JobStage.At"/> (with <see cref="JobStage.Need"/>, e.g. a trailer hitched).</summary>
    PickUp = 1,
    /// <summary>Bring what was picked up to <see cref="JobStage.At"/>.</summary>
    Deliver = 2,
    /// <summary>Haul <see cref="JobStage.Loads"/> loads from <see cref="JobStage.From"/> to <see cref="JobStage.At"/>, one after the other.</summary>
    Haul = 3,
    /// <summary>Stay at <see cref="JobStage.At"/> for <see cref="JobStage.Seconds"/> (unloading, pouring fuel, waiting for a crane).</summary>
    Timed = 4,
    /// <summary>Done when the job's template says so (<see cref="JobTemplate.Check"/> with <see cref="JobStage.Need"/>).</summary>
    Custom = 5,
}

/// <summary>One step of a job.</summary>
public sealed class JobStage
{
    public StageKind Kind;
    /// <summary>Shown while the stage runs ("Pick up the steel at the depot").</summary>
    public string Label;
    /// <summary>Where: the place to reach, pick up at, deliver to, stay at; a haul's drop-off.</summary>
    public Place At;
    /// <summary>A haul's pick-up.</summary>
    public Place From;
    /// <summary>A haul's number of loads (1 to <see cref="Career.Jobs.MaxLoads"/>).</summary>
    public int Loads = 1;
    /// <summary>How long a timed stage takes, seconds.</summary>
    public float Seconds;
    /// <summary>Seconds this stage may take (0: no limit). Running out fails the job.</summary>
    public float TimeLimit;
    /// <summary>
    /// What else the stage needs: "" nothing; "towing" a trailer hitched; "towing:hauler" a trailer of that kind
    /// ("utility", "camper", "hauler", "boat"); "empty" no trailer; "custom:&lt;key&gt;" the template's
    /// <see cref="JobTemplate.Check"/> with that key.
    /// </summary>
    public string Need = "";
    /// <summary>What is carried, for the screen ("steel beams", "a full jerry can").</summary>
    public string Cargo;
    /// <summary>The way there is a trip (a trailhead's trail), not a drive: the usual check that the car can't have got there
    /// this fast is off for this stage. Only the game that runs the job reads it.</summary>
    public bool Trip;

    internal JobStage Copy() => (JobStage)MemberwiseClone();
}

/// <summary>A job on a board: what it asks, what it pays.</summary>
public sealed class JobOffer
{
    public string Title;
    /// <summary>One or two plain sentences.</summary>
    public string Description;
    /// <summary>Pebbles for finishing it (before the host's payout multiplier).</summary>
    public long Pay;
    /// <summary>Extra pebbles for finishing within <see cref="BonusTime"/> seconds of taking it (0: no bonus).</summary>
    public long BonusPay;
    public float BonusTime;
    /// <summary>Seconds the whole job may take (0: no limit).</summary>
    public float TimeLimit;
    public List<JobStage> Stages = new();
    /// <summary>Unlocks granted when it is done.</summary>
    public string[] Unlocks;
    /// <summary>The template's own text, given back in every check and event (at most 256 characters).</summary>
    public string Data;
}

/// <summary>A kind of job a mod offers. The career runs it: it asks <see cref="Make"/> for offers, puts them on its boards,
/// runs the stages of a taken one and pays when it is done.</summary>
public sealed class JobTemplate
{
    /// <summary>"cargo.delivery", "haulage.radio_tower": 2-60 of a-z, 0-9, '_', '.', '-', unique.</summary>
    public string Id;
    /// <summary>The mod that registered it (set by <see cref="Career.Jobs.Register"/>).</summary>
    public string Owner;
    /// <summary>Shown on the board when the offer has no title.</summary>
    public string Name;
    /// <summary>Which board offers it: <see cref="Career.Boards"/> ("depot", "race", "help", "quest", "shop").</summary>
    public string Board = Career.Boards.Depot;
    /// <summary>"delivery", "haul", "supply", "build", "clearing", "race", "help", "quest": groups and labels the board.</summary>
    public string Kind = "delivery";
    /// <summary>The unlock it needs before it is offered (null: none).</summary>
    public string Unlock;
    /// <summary>False: once per career (a quest); it is not offered again after it is done.</summary>
    public bool Repeatable = true;
    /// <summary>How often it comes up next to the board's other templates (1 to 100).</summary>
    public int Weight = 10;
    /// <summary>Makes an offer for a board (null: none right now). Called by the career (on the host in a session) when a
    /// board needs one.</summary>
    public Func<OfferContext, JobOffer> Make;
    /// <summary>A "custom:&lt;key&gt;" stage's test: true when it is done (null: such stages never finish).</summary>
    public Func<JobCheck, bool> Check;
    /// <summary>Told about every lifecycle step of its jobs (null: nothing).</summary>
    public Action<JobEvent> Changed;
}

/// <summary>What <see cref="JobTemplate.Make"/> gets to build an offer from.</summary>
public sealed class OfferContext
{
    /// <summary>The board asking ("depot", ...).</summary>
    public string Board;
    /// <summary>Where the board is (the depot), or the player when the board isn't anywhere.</summary>
    public Place Here;
    public Random Random;
    /// <summary>Places of a kind the career knows: "depot", "poi" (the map's points of interest), "shop", "camp", "fuel",
    /// and every kind a world mod publishes. Never null.</summary>
    public Func<string, IReadOnlyList<Place>> Places = _ => Array.Empty<Place>();
    /// <summary>True when the career has the unlock.</summary>
    public Func<string, bool> Has = _ => false;
    /// <summary>Places of a kind wherever they are. <see cref="Places"/> leaves out places on another map, a trip away from
    /// <see cref="Here"/>, so a job can't send a trailer where it can't go; a quest that is about the trip itself reads these.</summary>
    public Func<string, IReadOnlyList<Place>> Anywhere = _ => Array.Empty<Place>();
    /// <summary>You can drive to this place from <see cref="Here"/> (a place on another map is a trip away).</summary>
    public Func<Place, bool> Reaches = _ => true;
}

/// <summary>What <see cref="JobTemplate.Check"/> gets.</summary>
public sealed class JobCheck
{
    /// <summary>The key after "custom:".</summary>
    public string Key;
    /// <summary>The player doing the job (PlayerRef.RawEncoded; 0 offline).</summary>
    public int Player;
    public long RunId;
    public int Stage;
    /// <summary>The offer's <see cref="JobOffer.Data"/>.</summary>
    public string Data;
    /// <summary>Where the player's car is.</summary>
    public float X, Y, Z;
}

public enum JobState : byte { Offered = 0, Active = 1, Done = 2, Failed = 3, Abandoned = 4 }

public enum JobEventKind : byte
{
    /// <summary>An offer went on a board.</summary>
    Offered = 0,
    /// <summary>A player took it.</summary>
    Accepted = 1,
    /// <summary>A stage (or one load of a haul) is done.</summary>
    Stage = 2,
    /// <summary>The job is done and paid.</summary>
    Completed = 3,
    /// <summary>It ran out of time, or its driver left.</summary>
    Failed = 4,
    /// <summary>The player gave it up.</summary>
    Abandoned = 5,
}

/// <summary>A step in a job's life.</summary>
public sealed class JobEvent
{
    public JobEventKind Kind;
    public long RunId;
    public string OfferId;
    public string TemplateId;
    /// <summary>The player (PlayerRef.RawEncoded; 0 offline, or the host's own).</summary>
    public int Player;
    /// <summary>The player is this game's own.</summary>
    public bool IsLocal;
    /// <summary>The stage it is on (after a <see cref="JobEventKind.Stage"/>, the next one).</summary>
    public int Stage;
    /// <summary>Loads hauled so far in the current haul stage.</summary>
    public int Loads;
    /// <summary>Completed: what the player got (after the host's multiplier).</summary>
    public long Pay;
    /// <summary>Failed / Abandoned: why, in plain words.</summary>
    public string Reason;
    /// <summary>The offer's <see cref="JobOffer.Data"/>.</summary>
    public string Data;
    /// <summary>Accepted: not a new job but one kept with the career, going on from <see cref="Stage"/> after a reload.</summary>
    public bool Resumed;
    /// <summary>Completed: the medal (0 none, 1 bronze, 2 silver, 3 gold).</summary>
    public byte Medal;
    /// <summary>Completed: how the load arrived, 0 (ruined) to 1 (not a scratch).</summary>
    public float LoadCare = 1f;
    /// <summary>Completed: a line for the driver about how it went, or null.</summary>
    public string Note;
}

/// <summary>A job as the career screen shows it: an offer on a board, or the one a player is doing.</summary>
public sealed class JobView
{
    /// <summary>The board's id for this offer (pass it to <see cref="Career.Jobs.Accept"/>).</summary>
    public string OfferId;
    public string TemplateId;
    public string Board;
    public string Kind;
    public string Title;
    public string Description;
    public long Pay, BonusPay;
    public float BonusTime, TimeLimit;
    public List<JobStage> Stages = new();
    public JobState State;
    /// <summary>Active: the run's id.</summary>
    public long RunId;
    /// <summary>Active: the stage it is on, the loads of the current haul, seconds left (job or stage, whichever is
    /// sooner; negative: no limit), the players helping.</summary>
    public int Stage, Loads;
    public float TimeLeft = -1f;
    public int Helpers;
    /// <summary>Active: something is picked up and on its way (in a haul: a load is on, so the next place is the drop-off).</summary>
    public bool Carrying;
    /// <summary>Active: how the load is doing, 0 (ruined) to 1 (not a scratch).</summary>
    public float LoadCare = 1f;
    /// <summary>Active: seconds the job should take, for its medal (0: not known here).</summary>
    public float Par;
    /// <summary>Where the board is (a depot job is taken there).</summary>
    public Place Where;

    /// <summary>The stage it is on, or null.</summary>
    public JobStage CurrentStage => Stage >= 0 && Stage < Stages.Count ? Stages[Stage] : null;
}
