using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Api;

/// <summary>What a purchase came to.</summary>
public enum BuyResult
{
    Ok,
    /// <summary>No career is playing.</summary>
    NoCareer,
    /// <summary>Not for sale (not listed, or the car can't get that upgrade).</summary>
    NotForSale,
    /// <summary>It needs an unlock the career doesn't have.</summary>
    Locked,
    /// <summary>The career has it already (the car, or the upgrade's last level).</summary>
    Owned,
    /// <summary>Not enough money.</summary>
    TooPoor,
    /// <summary>The career can't hold more.</summary>
    Full,
    /// <summary>The career doesn't own that car.</summary>
    NoCar,
}

public static partial class Career
{
    /// <summary>
    /// The cars a career owns, the one it drives, and every car's upgrades. Owning gates driving: in a career only owned
    /// cars can be picked in the garage. In a career with its own world the game's garage follows this list.
    /// </summary>
    public static class Garage
    {
        /// <summary>A car was added, sold, picked, or its upgrades changed (the car's key).</summary>
        public static event Action<string> Changed;

        /// <summary>The event's subscribers, for the other parts of the API that change a car.</summary>
        internal static Action<string> ChangedEvent => Changed;

        /// <summary>The owned cars' keys, sorted (empty without a career).</summary>
        public static IReadOnlyCollection<string> Owned => Book != null ? Book.Cars : Array.Empty<string>();

        public static bool Owns(string car) => Book != null && car != null && Book.Cars.Contains(car);

        /// <summary>The car the career drives now ("" when unknown).</summary>
        public static string InUse => Book?.CarInUse ?? "";

        /// <summary>Adds a car for free (a quest's reward). False without a career playing, for a bad key, an owned car or a full garage.</summary>
        public static bool Grant(string car, string reason)
        {
            if (!Playing || !IsCar(car) || Book.Cars.Contains(car) || Book.Cars.Count >= CareerBook.MaxCars) return false;
            Book.Cars.Add(car);
            if (string.IsNullOrEmpty(Book.CarInUse)) Book.CarInUse = car;
            Touch();
            Raise("Garage.Changed", Changed, h => h(car));
            return true;
        }

        /// <summary>
        /// Adds a car with upgrade levels it comes with (a car bought on the player market). Levels of unknown upgrades,
        /// upgrades that don't fit the car and levels beyond an upgrade's own are left out. False like <see cref="Grant(string, string)"/>.
        /// </summary>
        public static bool Grant(string car, IReadOnlyDictionary<string, int> levels, string reason)
        {
            if (!Grant(car, reason)) return false;
            if (levels != null)
                foreach (var kv in levels)
                {
                    var def = Upgrades.Find(kv.Key);
                    if (def == null || !def.Fits(car) || kv.Value < 1) continue;
                    Book.SetLevel(car, def.Id, Math.Min(kv.Value, def.Levels.Count));
                }
            Touch();
            Raise("Garage.Changed", Changed, h => h(car));
            return true;
        }

        /// <summary>
        /// Takes an owned car out of the garage without paying anything (a player-market sale): returns its upgrade levels
        /// (they go with the car), or null when it can't go (no career playing, not owned, the car in use or the last one).
        /// </summary>
        public static Dictionary<string, int> Release(string car, string reason)
        {
            if (!Playing || !Owns(car) || Book.Cars.Count <= 1) return null;
            if (string.Equals(car, Book.CarInUse, StringComparison.OrdinalIgnoreCase)) return null;
            var levels = new Dictionary<string, int>(Book.LevelsOf(car), StringComparer.Ordinal);
            Book.Cars.Remove(car);
            Book.Levels.Remove(car);
            Book.Unfitted.Remove(car);
            Book.Records.Remove(car);
            Touch();
            Raise("Garage.Changed", Changed, h => h(car));
            return levels;
        }

        /// <summary>Buys a car the dealer lists (<see cref="Dealer"/>).</summary>
        public static BuyResult Buy(string car)
        {
            if (!Playing) return BuyResult.NoCareer;
            var offer = Dealer.Find(car);
            if (offer == null || !Rules.AllowsCategory(offer.Category)) return BuyResult.NotForSale;
            if (Book.Cars.Contains(offer.Car)) return BuyResult.Owned;
            if (!string.IsNullOrEmpty(offer.Unlock) && !Book.Unlocks.Contains(offer.Unlock)) return BuyResult.Locked;
            if (Book.Cars.Count >= CareerBook.MaxCars) return BuyResult.Full;
            if (!Wallet.Spend(offer.Price, "Bought the " + offer.Name, "dealer")) return BuyResult.TooPoor;
            Book.Cars.Add(offer.Car);
            Book.Records[offer.Car] = new CarRecord { Paid = offer.Price };
            if (string.IsNullOrEmpty(Book.CarInUse)) Book.CarInUse = offer.Car;
            Touch();
            Raise("Garage.Changed", Changed, h => h(offer.Car));
            return BuyResult.Ok;
        }

        /// <summary>
        /// Buys one particular car the dealer has in the yard, for its own price, with the clock and wear it comes with. It needs
        /// what a new one needs (listed, unlocked, a free spot). <paramref name="record"/> is kept as it is.
        /// </summary>
        internal static BuyResult BuySecondHand(string car, long price, CarRecord record)
        {
            if (!Playing) return BuyResult.NoCareer;
            var offer = Dealer.Find(car);
            if (offer == null || record == null || price <= 0 || !Rules.AllowsCategory(offer.Category)) return BuyResult.NotForSale;
            if (Book.Cars.Contains(offer.Car)) return BuyResult.Owned;
            if (!string.IsNullOrEmpty(offer.Unlock) && !Book.Unlocks.Contains(offer.Unlock)) return BuyResult.Locked;
            if (Book.Cars.Count >= CareerBook.MaxCars) return BuyResult.Full;
            if (!Wallet.Spend(price, "Bought a used " + offer.Name, "dealer")) return BuyResult.TooPoor;
            Book.Cars.Add(offer.Car);
            Book.Records[offer.Car] = record;
            if (string.IsNullOrEmpty(Book.CarInUse)) Book.CarInUse = offer.Car;
            Touch();
            Raise("Garage.Changed", Changed, h => h(offer.Car));
            return BuyResult.Ok;
        }

        /// <summary>What the career mod says a car is worth to the dealer (its new price and record in, pebbles out). Null: half its price.</summary>
        internal static Func<long, CarRecord, long> Appraiser { get; set; }

        /// <summary>What selling a car gets back: what the dealer gives for it with its clock and wear, or half its dealer price
        /// without a career mod (0 for a car the dealer doesn't list).</summary>
        public static long SellPrice(string car)
        {
            var offer = Dealer.Find(car);
            if (offer == null) return 0;
            if (Appraiser == null || Book == null) return offer.Price / 2;
            return Math.Max(0, Appraiser(offer.Price, Book.Records.TryGetValue(car, out var r) ? r : null));
        }

        /// <summary>Kilometres on a car's clock (0 without a career).</summary>
        public static double Km(string car) => Book != null && car != null && Book.Records.TryGetValue(car, out var r) ? r.Km : 0;

        /// <summary>What is left of a car, 0 to 1 (1 without a record).</summary>
        public static float Condition(string car) => Book != null && car != null && Book.Records.TryGetValue(car, out var r) ? r.Condition : 1f;

        /// <summary>Sells an owned car for <see cref="SellPrice"/>, with its upgrades. Never the car in use or the last car.</summary>
        public static bool Sell(string car)
        {
            if (!Playing || !Owns(car) || Book.Cars.Count <= 1) return false;
            if (string.Equals(car, Book.CarInUse, StringComparison.OrdinalIgnoreCase)) return false;
            long back = SellPrice(car);
            Book.Cars.Remove(car);
            Book.Levels.Remove(car);
            Book.Unfitted.Remove(car);
            Book.Records.Remove(car);
            if (back > 0) Wallet.Credit(back, "Sold the " + (Dealer.Find(car)?.Name ?? car), "dealer");
            Touch();
            Raise("Garage.Changed", Changed, h => h(car));
            return true;
        }

        // ---------------------------------------------------------------- upgrades

        /// <summary>The level of an upgrade the car has (0: stock).</summary>
        public static int Level(string car, string upgrade) => Book?.Level(car, upgrade) ?? 0;

        /// <summary>True when the car has the upgrade fitted (owned and not swapped out for another of its group).</summary>
        public static bool Fitted(string car, string upgrade) => Book != null && Book.Fitted(car, upgrade);

        /// <summary>A car's upgrades and levels (empty without a career).</summary>
        public static IReadOnlyDictionary<string, int> Levels(string car) =>
            Book != null ? Book.LevelsOf(car) : (IReadOnlyDictionary<string, int>)new Dictionary<string, int>();

        /// <summary>Buys the next level of an upgrade for an owned car (<see cref="Upgrades.PriceFor"/>). Fitting one of a
        /// group takes the others of the group off (they stay owned: <see cref="Fit"/> puts one back for free).</summary>
        public static BuyResult BuyUpgrade(string car, string upgrade)
        {
            if (!Playing) return BuyResult.NoCareer;
            if (!Owns(car)) return BuyResult.NoCar;
            var def = Upgrades.Find(upgrade);
            if (def == null || !def.Fits(car) || !Rules.AllowsUpgrade(def.Id)) return BuyResult.NotForSale;
            if (!string.IsNullOrEmpty(def.Unlock) && !Book.Unlocks.Contains(def.Unlock)) return BuyResult.Locked;
            int next = Book.Level(car, def.Id) + 1;
            if (next > def.Levels.Count) return BuyResult.Owned;
            string gate = def.Levels[next - 1]?.Unlock;
            if (!string.IsNullOrEmpty(gate) && !Book.Unlocks.Contains(gate)) return BuyResult.Locked;
            long price = Upgrades.PriceFor(car, def.Id, next);
            if (price < 0) return BuyResult.NotForSale;
            if (price > Book.Money) return BuyResult.TooPoor;
            if (!Book.SetLevel(car, def.Id, next)) return BuyResult.Full;
            if (price > 0 && !Wallet.Spend(price, def.Name + ": " + LevelName(def, next) + " for the " + CarName(car), "garage"))
            {
                Book.SetLevel(car, def.Id, next - 1);
                return BuyResult.TooPoor;
            }
            FitOnly(car, def);
            Touch();
            Raise("Garage.Changed", Changed, h => h(car));
            return BuyResult.Ok;
        }

        /// <summary>Fits (or takes off) an upgrade the car owns. Fitting one of a group takes the others of the group off.</summary>
        public static bool Fit(string car, string upgrade, bool on)
        {
            if (!Playing || !Owns(car) || Book.Level(car, upgrade) <= 0) return false;
            var def = Upgrades.Find(upgrade);
            if (on && def != null) FitOnly(car, def);
            else Book.SetFitted(car, upgrade, on);
            Touch();
            Raise("Garage.Changed", Changed, h => h(car));
            return true;
        }

        private static void FitOnly(string car, UpgradeDef def)
        {
            Book.SetFitted(car, def.Id, true);
            if (string.IsNullOrEmpty(def.Group)) return;
            foreach (var other in Upgrades.All)
                if (!ReferenceEquals(other, def) && string.Equals(other.Group, def.Group, StringComparison.Ordinal) && Book.Level(car, other.Id) > 0)
                    Book.SetFitted(car, other.Id, false);
        }

        /// <summary>Everything the car's fitted upgrades do together (an effect that changes nothing without a career).</summary>
        public static CarUpgradeEffect EffectFor(string car)
        {
            var total = CarUpgradeEffect.None;
            if (Book == null || car == null) return total;
            foreach (var kv in Book.LevelsOf(car))
            {
                if (!Book.Fitted(car, kv.Key)) continue;
                var def = Upgrades.Find(kv.Key);
                if (def == null || kv.Value <= 0) continue;
                var lvl = def.Levels[Math.Min(kv.Value, def.Levels.Count) - 1];
                if (lvl?.Effect != null) total = total.Times(lvl.Effect);
            }
            return total;
        }

        /// <summary>The career mod: the game says the career drives <paramref name="car"/> now.</summary>
        internal static void SetInUse(string car)
        {
            if (Book == null || !Owns(car) || string.Equals(Book.CarInUse, car, StringComparison.OrdinalIgnoreCase)) return;
            Book.CarInUse = car;
            Touch();
            Raise("Garage.Changed", Changed, h => h(car));
        }

        /// <summary>The career mod: a car left the career's world (sold at the game's merchant): it goes from the book too.</summary>
        internal static void Remove(string car)
        {
            if (Book == null || !Owns(car)) return;
            Book.Cars.Remove(car);
            Book.Records.Remove(car);
            if (string.Equals(Book.CarInUse, car, StringComparison.OrdinalIgnoreCase)) Book.CarInUse = "";
            Touch();
            Raise("Garage.Changed", Changed, h => h(car));
        }

        private static string LevelName(UpgradeDef def, int level)
        {
            var l = level >= 1 && level <= def.Levels.Count ? def.Levels[level - 1] : null;
            return !string.IsNullOrEmpty(l?.Name) ? l.Name : "level " + level;
        }

        private static string CarName(string car) => Dealer.Find(car)?.Name ?? car;

        internal static void Reset()
        {
            Changed = null;
            Appraiser = null;
        }
    }

    /// <summary>
    /// The cars for sale. Mods that add vehicles list them here with a price; the career lists the game's cars and the car
    /// pack's. A car is bought where the career has a dealer (a shop a world mod publishes, or the garage).
    /// </summary>
    public static class Dealer
    {
        private static readonly Dictionary<string, CarOffer> ByCar = new(StringComparer.OrdinalIgnoreCase);
        private static List<CarOffer> _sorted;

        /// <summary>The list changed.</summary>
        public static event Action Changed;

        /// <summary>The price a car without a listing counts as when its upgrades are priced.</summary>
        public const long ReferenceValue = 12000;

        /// <summary>The most a car may cost.</summary>
        public const long MaxPrice = 10_000_000;

        /// <summary>
        /// The career's money scale against the game's own prices (1 until a career has read the game's catalog). Offers
        /// listed with <see cref="CarOffer.OnMoneyScale"/> are sold at their price times this, rounded like the career's.
        /// </summary>
        public static float MoneyScale { get; private set; } = 1f;

        /// <summary>The career sets the money scale once it has read the game's prices.</summary>
        internal static void SetMoneyScale(float scale)
        {
            scale = float.IsFinite(scale) && scale > 0 ? Math.Clamp(scale, 0.2f, 20f) : 1f;
            if (Math.Abs(scale - MoneyScale) < 1e-6f) return;
            MoneyScale = scale;
            _sorted = null;
            Raise("Dealer.Changed", Changed, h => h());
        }

        /// <summary>A base price on the money scale, rounded to a tidy number (to 50 under 10,000, else to 500).</summary>
        public static long OnScale(long basePrice)
        {
            if (basePrice <= 0) return 0;
            double p = basePrice * (double)MoneyScale;
            long step = p < 10000 ? 50 : 500;
            return Math.Min(MaxPrice, Math.Max(step, (long)Math.Round(p / step) * step));
        }

        /// <summary>A listing as it is sold: on the money scale, then times the host's car prices (<see cref="CareerRuleSet.PriceScale"/>).</summary>
        private static CarOffer Out(CarOffer o)
        {
            var c = o.Copy();
            long p = c.OnMoneyScale ? OnScale(o.Price) : o.Price;
            c.Price = Rules.PriceScaled(p);
            return c;
        }

        /// <summary>The host's rules changed: prices and what is for sale may have too.</summary>
        internal static void RulesChanged()
        {
            _sorted = null;
            Raise("Dealer.Changed", Changed, h => h());
        }

        /// <summary>
        /// Lists (or relists) a car for sale. False (and logged) for a bad mod id, car key, name or price, or a car another
        /// mod listed. <paramref name="owner"/> is your mod id. The dealer keeps its own copy.
        /// </summary>
        public static bool List(string owner, CarOffer offer)
        {
            if (!IsId(owner) || offer == null || !IsCar(offer.Car) || offer.Price < 0 || offer.Price > MaxPrice)
            {
                Warn("dealer " + owner + "/" + offer?.Car, $"Dealer.List from {owner}: refused (bad mod id, car key or price)");
                return false;
            }
            if (ByCar.TryGetValue(offer.Car, out var had) && !string.Equals(had.Owner, owner, StringComparison.Ordinal))
            {
                Warn("dealer-taken " + offer.Car, $"Dealer.List from {owner}: {offer.Car} is already listed by {had.Owner}");
                return false;
            }
            var copy = offer.Copy();
            copy.Owner = owner;
            copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? copy.Car : Clean(copy.Name, 40);
            copy.Description = Clean(copy.Description, 200);
            copy.Category = IsId(copy.Category ?? "", 24) ? copy.Category : "car";
            copy.Unlock = string.IsNullOrEmpty(copy.Unlock) ? null : copy.Unlock;
            ByCar[copy.Car] = copy;
            _sorted = null;
            Raise("Dealer.Changed", Changed, h => h());
            return true;
        }

        /// <summary>Takes a car <paramref name="owner"/> listed off the list.</summary>
        public static bool Unlist(string owner, string car)
        {
            if (car == null || !ByCar.TryGetValue(car, out var had) || !string.Equals(had.Owner, owner, StringComparison.Ordinal)) return false;
            ByCar.Remove(car);
            _sorted = null;
            Raise("Dealer.Changed", Changed, h => h());
            return true;
        }

        /// <summary>Every listed car (copies), by category then price.</summary>
        public static IReadOnlyList<CarOffer> Offers
        {
            get
            {
                if (_sorted != null) return _sorted;
                var l = new List<CarOffer>();
                foreach (var o in ByCar.Values) if (Rules.AllowsCategory(o.Category)) l.Add(Out(o));
                l.Sort((a, b) =>
                {
                    int c = string.Compare(a.Category, b.Category, StringComparison.Ordinal);
                    if (c != 0) return c;
                    c = a.Price.CompareTo(b.Price);
                    return c != 0 ? c : string.Compare(a.Car, b.Car, StringComparison.OrdinalIgnoreCase);
                });
                return _sorted = l;
            }
        }

        /// <summary>The cars a new career can start with (copies), cheapest first.</summary>
        public static IReadOnlyList<CarOffer> Starters
        {
            get
            {
                var l = new List<CarOffer>();
                foreach (var o in ByCar.Values) if (o.Starter && Rules.AllowsCategory(o.Category)) l.Add(Out(o));
                l.Sort((a, b) => a.Price != b.Price ? a.Price.CompareTo(b.Price) : string.Compare(a.Car, b.Car, StringComparison.OrdinalIgnoreCase));
                return l;
            }
        }

        /// <summary>True when some mod listed a car of this category (whatever the host's rules say about selling it).</summary>
        public static bool Lists(string category)
        {
            foreach (var o in ByCar.Values) if (string.Equals(o.Category, category, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>One car's listing (a copy), or null.</summary>
        public static CarOffer Find(string car) => car != null && ByCar.TryGetValue(car, out var o) ? Out(o) : null;

        /// <summary>A car's value for pricing its upgrades: its dealer price, else <see cref="ReferenceValue"/>.</summary>
        public static long ValueOf(string car)
        {
            var o = car != null && ByCar.TryGetValue(car, out var x) ? x : null;
            return o != null && o.Price > 0 ? (o.OnMoneyScale ? OnScale(o.Price) : o.Price) : ReferenceValue;
        }

        /// <summary>True when the loaded career could buy the car now (listed, unlocked, not owned; money aside).</summary>
        public static bool ForSale(string car)
        {
            var o = car != null && ByCar.TryGetValue(car, out var x) ? x : null;
            return o != null && Active && Rules.AllowsCategory(o.Category) && !Book.Cars.Contains(o.Car) && (string.IsNullOrEmpty(o.Unlock) || Book.Unlocks.Contains(o.Unlock));
        }

        internal static void Reset()
        {
            ByCar.Clear();
            _sorted = null;
            Changed = null;
            MoneyScale = 1f;
        }
    }

    /// <summary>
    /// The upgrades for sale: engine stages, turbo, gearing, tyres, lift, brakes, tank, winch from the career; more from
    /// any mod (a plow for road clearing). Prices scale with the car's value (<see cref="PriceFor"/>).
    /// </summary>
    public static class Upgrades
    {
        private static readonly Dictionary<string, UpgradeDef> ById = new(StringComparer.Ordinal);
        private static readonly List<UpgradeDef> Ordered = new();

        public const int MaxLevels = 10;
        public const long MaxPrice = 5_000_000;

        /// <summary>A car worth this much pays the base prices; cheaper and dearer cars pay between these factors of them.</summary>
        public const float MinValueFactor = 0.6f, MaxValueFactor = 2.5f;

        /// <summary>
        /// Registers (or, for your own id, replaces) an upgrade. False (and logged) for a bad mod id, upgrade id or level,
        /// or an id another mod registered. <paramref name="owner"/> is your mod id. The registry keeps its own copy.
        /// </summary>
        public static bool Register(string owner, UpgradeDef def)
        {
            string problem = Problem(owner, def);
            if (problem != null)
            {
                Warn("upgrade " + owner + "/" + def?.Id, $"Upgrades.Register from {owner}: {problem}");
                return false;
            }
            var copy = new UpgradeDef
            {
                Id = def.Id, Name = string.IsNullOrWhiteSpace(def.Name) ? def.Id : Clean(def.Name, 40), Description = Clean(def.Description, 240),
                Category = IsId(def.Category ?? "", 24) ? def.Category : "engine", Group = IsId(def.Group ?? "", 40) ? def.Group : null,
                Unlock = string.IsNullOrEmpty(def.Unlock) ? null : def.Unlock, Cars = def.Cars != null ? (string[])def.Cars.Clone() : null,
                Owner = owner,
            };
            foreach (var l in def.Levels)
                copy.Levels.Add(new UpgradeLevel { Name = Clean(l.Name, 32), Price = l.Price, Effect = (l.Effect ?? CarUpgradeEffect.None).Clamped(), Note = Clean(l.Note, 80), Unlock = string.IsNullOrEmpty(l.Unlock) ? null : l.Unlock });
            if (ById.TryGetValue(def.Id, out var old)) Ordered.Remove(old);
            ById[def.Id] = copy;
            Ordered.Add(copy);
            return true;
        }

        private static string Problem(string owner, UpgradeDef def)
        {
            if (!IsId(owner)) return "the mod id is not valid";
            if (def == null || !IsId(def.Id)) return "the upgrade id is not valid (2-40 of a-z, 0-9, '_', '.', '-')";
            if (ById.TryGetValue(def.Id, out var had) && !string.Equals(had.Owner, owner, StringComparison.Ordinal)) return $"{def.Id} belongs to {had.Owner}";
            if (def.Levels == null || def.Levels.Count == 0 || def.Levels.Count > MaxLevels) return $"it needs 1 to {MaxLevels} levels";
            foreach (var l in def.Levels)
                if (l == null || l.Price < 0 || l.Price > MaxPrice) return "a level is missing or its price is out of range";
            return null;
        }

        /// <summary>Every upgrade, in the order they were registered.</summary>
        public static IReadOnlyList<UpgradeDef> All => Ordered;

        public static UpgradeDef Find(string id) => id != null && ById.TryGetValue(id, out var d) ? d : null;

        /// <summary>The upgrades a car can get.</summary>
        public static List<UpgradeDef> For(string car)
        {
            var l = new List<UpgradeDef>();
            foreach (var d in Ordered) if (d.Fits(car) && Rules.AllowsUpgrade(d.Id)) l.Add(d);
            return l;
        }

        /// <summary>How much of the base prices a car pays: its value over <see cref="Dealer.ReferenceValue"/>, between
        /// <see cref="MinValueFactor"/> and <see cref="MaxValueFactor"/>.</summary>
        public static float ValueFactor(string car) =>
            Math.Clamp(Dealer.ValueOf(car) / (float)Dealer.ReferenceValue, MinValueFactor, MaxValueFactor);

        /// <summary>The price of <paramref name="level"/> (1-based) of an upgrade for a car, rounded to 50 pebbles; -1 when
        /// there is no such level. Times the money scale of the host's rules; 0 when parts are free there.</summary>
        public static long PriceFor(string car, string id, int level)
        {
            var d = Find(id);
            if (d == null || level < 1 || level > d.Levels.Count) return -1;
            long basePrice = d.Levels[level - 1].Price;
            float parts = Rules.PartsScale;
            if (basePrice <= 0 || parts <= 0f) return 0;
            double p = basePrice * (double)ValueFactor(car) * parts;
            return Math.Max(50, (long)Math.Round(p / 50.0) * 50);
        }

        internal static void Reset()
        {
            ById.Clear();
            Ordered.Clear();
        }
    }
}
