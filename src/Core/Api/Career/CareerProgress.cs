using System;
using System.Collections.Generic;
using System.Globalization;

namespace OverTheHillPlus.Api;

public static partial class Career
{
    /// <summary>
    /// Unlocks: tags a career earns ("radio_tower_done" opens the player market). A finished job grants its offer's
    /// <see cref="JobOffer.Unlocks"/>; any mod may grant one too. Give them a name with <see cref="Describe"/> so the career
    /// screen can list them.
    /// </summary>
    public static class Unlocks
    {
        private static readonly Dictionary<string, UnlockDef> Defs = new(StringComparer.Ordinal);

        /// <summary>The loaded career got an unlock (its tag).</summary>
        public static event Action<string> Granted;

        public static bool Has(string tag) => Book != null && tag != null && Book.Unlocks.Contains(tag);

        /// <summary>The loaded career's unlocks (empty without a career).</summary>
        public static IReadOnlyCollection<string> All => Book != null ? Book.Unlocks : Array.Empty<string>();

        /// <summary>Grants an unlock. False without a career, for a bad tag, or when the career has it already.</summary>
        public static bool Grant(string tag, string reason)
        {
            if (Book == null || !IsId(tag) || Book.Unlocks.Contains(tag) || Book.Unlocks.Count >= CareerBook.MaxUnlocks) return false;
            Book.Unlocks.Add(tag);
            Touch();
            Raise("Unlocks.Granted", Granted, h => h(tag));
            return true;
        }

        /// <summary>Names an unlock for the career screen (your mod id in <paramref name="owner"/>). False for a bad id or tag,
        /// or a tag another mod named.</summary>
        public static bool Describe(string owner, UnlockDef def)
        {
            if (!IsId(owner) || def == null || !IsId(def.Tag))
            {
                Warn("unlock " + owner + "/" + def?.Tag, $"Unlocks.Describe from {owner}: refused (bad mod id or tag)");
                return false;
            }
            if (Defs.TryGetValue(def.Tag, out var had) && !string.Equals(had.Owner, owner, StringComparison.Ordinal)) return false;
            Defs[def.Tag] = new UnlockDef
            {
                Tag = def.Tag, Name = string.IsNullOrWhiteSpace(def.Name) ? def.Tag : Clean(def.Name, 48), Description = Clean(def.Description, 200),
                Hidden = def.Hidden, Owner = owner,
            };
            return true;
        }

        /// <summary>An unlock's name and description, or null when nobody named it.</summary>
        public static UnlockDef Find(string tag) => tag != null && Defs.TryGetValue(tag, out var d) ? d : null;

        /// <summary>Every named unlock.</summary>
        public static IReadOnlyCollection<UnlockDef> Named => Defs.Values;

        internal static void Reset()
        {
            Defs.Clear();
            Granted = null;
        }
    }

    /// <summary>
    /// Goods: things a career carries and sells (the ice-fishing catch, crates of cargo). Register a kind with its price,
    /// add what the player gets, and the career sells it at a shop or a depot (or call <see cref="Sell"/> yourself).
    /// </summary>
    public static class Goods
    {
        private static readonly Dictionary<string, GoodDef> Defs = new(StringComparer.Ordinal);

        /// <summary>The loaded career's goods changed (the good's id).</summary>
        public static event Action<string> Changed;

        /// <summary>Registers (or, for your own id, replaces) a kind of good. False for a bad mod id, good id or price, or an id
        /// another mod registered.</summary>
        public static bool Register(string owner, GoodDef def)
        {
            if (!IsId(owner) || def == null || !IsId(def.Id) || !(def.Price >= 0 && def.Price <= 1_000_000) || !(def.MaxCarry > 0 && def.MaxCarry <= 1_000_000))
            {
                Warn("good " + owner + "/" + def?.Id, $"Goods.Register from {owner}: refused (bad mod id, good id, price or carry limit)");
                return false;
            }
            if (Defs.TryGetValue(def.Id, out var had) && !string.Equals(had.Owner, owner, StringComparison.Ordinal)) return false;
            Defs[def.Id] = new GoodDef
            {
                Id = def.Id, Name = string.IsNullOrWhiteSpace(def.Name) ? def.Id : Clean(def.Name, 40), Unit = Clean(def.Unit, 12),
                Price = def.Price, MaxCarry = def.MaxCarry, Owner = owner,
            };
            return true;
        }

        public static GoodDef Find(string id) => id != null && Defs.TryGetValue(id, out var d) ? d : null;

        /// <summary>Every registered kind of good.</summary>
        public static IReadOnlyCollection<GoodDef> Kinds => Defs.Values;

        /// <summary>How much of a good the career holds.</summary>
        public static double Count(string id) => Book != null && id != null && Book.Goods.TryGetValue(id, out var n) ? n : 0;

        /// <summary>Everything the career holds (empty without a career).</summary>
        public static IReadOnlyDictionary<string, double> All =>
            Book != null ? Book.Goods : (IReadOnlyDictionary<string, double>)new Dictionary<string, double>();

        /// <summary>Adds <paramref name="amount"/> of a registered good (no more than its carry limit). Returns what was added.</summary>
        public static double Add(string id, double amount, string reason)
        {
            var d = Find(id);
            if (!Playing || d == null || !(amount > 0) || double.IsInfinity(amount)) return 0;
            double have = Count(id);
            double add = Math.Min(amount, d.MaxCarry - have);
            if (add <= 0) return 0;
            if (!Book.Goods.ContainsKey(id) && Book.Goods.Count >= CareerBook.MaxGoods) return 0;
            Book.Goods[id] = Math.Round(have + add, 3);
            Touch();
            Raise("Goods.Changed", Changed, h => h(id));
            return add;
        }

        /// <summary>Takes up to <paramref name="amount"/> of a good away (used, lost, traded). Returns what was taken.</summary>
        public static double Remove(string id, double amount)
        {
            if (Book == null || id == null || !(amount > 0) || !Book.Goods.TryGetValue(id, out var have)) return 0;
            double take = Math.Min(have, amount);
            double left = Math.Round(have - take, 3);
            if (left <= 0.0005) Book.Goods.Remove(id);
            else Book.Goods[id] = left;
            Touch();
            Raise("Goods.Changed", Changed, h => h(id));
            return take;
        }

        /// <summary>What <paramref name="amount"/> of a good sells for, pebbles (rounded down).</summary>
        public static long ValueOf(string id, double amount)
        {
            var d = Find(id);
            if (d == null || !(amount > 0) || double.IsInfinity(amount)) return 0;
            double v = d.Price * amount;
            return v >= CareerBook.MaxMoney ? CareerBook.MaxMoney : (long)Math.Floor(v);
        }

        /// <summary>Sells up to <paramref name="amount"/> of a good at its price (no multiplier: it is a sale). Returns the
        /// pebbles got (0: nothing sold).</summary>
        public static long Sell(string id, double amount)
        {
            if (!Playing) return 0;
            var d = Find(id);
            double have = Count(id);
            double sold = Math.Min(have, amount);
            if (d == null || !(sold > 0)) return 0;
            long pay = ValueOf(id, sold);
            Remove(id, sold);
            if (pay <= 0) return 0;
            string what = d.Unit == "kg" ? sold.ToString("0.0", CultureInfo.InvariantCulture) + " kg of " + d.Name.ToLowerInvariant()
                                         : sold.ToString("0.##", CultureInfo.InvariantCulture) + " " + d.Unit + " of " + d.Name.ToLowerInvariant();
            return Wallet.Credit(pay, "Sold " + what, d.Owner);
        }

        internal static void Reset()
        {
            Defs.Clear();
            Changed = null;
        }
    }

    /// <summary>
    /// Per-career progress a mod keeps (the radio tower's loads so far, a quest's step): numbers and short texts, saved with
    /// the career. Keys are "&lt;your mod id&gt;.&lt;name&gt;" (3-64 of a-z, 0-9, '_', '.', '-').
    /// </summary>
    public static class Progress
    {
        /// <summary>A key changed.</summary>
        public static event Action<string> Changed;

        public static long Get(string key) => Book != null && key != null && Book.Counters.TryGetValue(key, out var v) ? v : 0;

        /// <summary>Sets a number. False without a career or for a bad key.</summary>
        public static bool Set(string key, long value)
        {
            if (Book == null || !KeyOk(key)) return false;
            if (!Book.Counters.ContainsKey(key) && Book.Counters.Count >= CareerBook.MaxProgress) return false;
            Book.Counters[key] = value;
            Touch();
            Raise("Progress.Changed", Changed, h => h(key));
            return true;
        }

        /// <summary>Adds to a number; returns the new value (0 without a career).</summary>
        public static long Add(string key, long delta)
        {
            if (Book == null || !KeyOk(key)) return 0;
            long v = Get(key);
            long next = delta > 0 && v > long.MaxValue - delta ? long.MaxValue : delta < 0 && v < long.MinValue - delta ? long.MinValue : v + delta;
            return Set(key, next) ? next : v;
        }

        public static string Text(string key) => Book != null && key != null && Book.Texts.TryGetValue(key, out var t) ? t : "";

        /// <summary>Sets a short text (at most 256 characters; null or empty removes it).</summary>
        public static bool SetText(string key, string text)
        {
            if (Book == null || !KeyOk(key)) return false;
            if (string.IsNullOrEmpty(text)) Book.Texts.Remove(key);
            else
            {
                if (!Book.Texts.ContainsKey(key) && Book.Texts.Count >= CareerBook.MaxProgress) return false;
                Book.Texts[key] = text.Length > CareerBook.MaxText ? text.Substring(0, CareerBook.MaxText) : text;
            }
            Touch();
            Raise("Progress.Changed", Changed, h => h(key));
            return true;
        }

        internal static bool KeyOk(string key) => IsId(key, 64) && key.Length >= 3 && key.IndexOf('.') > 0;

        internal static void Reset() => Changed = null;
    }

    /// <summary>
    /// The rules the host picks in its host settings (<see cref="CareerRuleSet"/>: careers, money, pay and prices, guests,
    /// which parts of the overhaul are on, the host tools). Solo and hosting: the player's own; a guest: the
    /// host's, as they arrive. Every modded guest follows them; vanilla players never see them.
    /// </summary>
    public static class Rules
    {
        private static CareerRuleSet _current = new();

        /// <summary>The rules changed (a host's message arrived, or the host changed its own).</summary>
        public static event Action Changed;

        /// <summary>The rules in effect (a copy).</summary>
        public static CareerRuleSet Current => _current.Copy();

        /// <summary>The rules come from a host (this game is a guest).</summary>
        public static bool FromHost { get; private set; }

        /// <summary>A feature of the overhaul is on (<see cref="CareerRuleSet.Features"/>: "haulage", "market",
        /// "plowing", "map", "vehicles", "hostpowers"). Cheap: no copy. True for ids it doesn't know.</summary>
        public static bool Allows(string feature) => _current.Allows(feature);

        /// <summary>Careers earn and spend in this session (no copy).</summary>
        public static bool CareerOn => _current.CareerOn;

        /// <summary>What job pay is multiplied by (<see cref="CareerRuleSet.PayScale"/>, no copy).</summary>
        public static float PayScale => _current.PayScale;

        /// <summary>What upgrade prices are multiplied by (0: parts are free).</summary>
        public static float PartsScale => _current.PartsScale;

        /// <summary>A dealer category may be sold under these rules.</summary>
        public static bool AllowsCategory(string category) => _current.AllowsCategory(category);

        /// <summary>An upgrade may be bought under these rules.</summary>
        public static bool AllowsUpgrade(string id) => _current.AllowsUpgrade(id);

        /// <summary>A job template may be offered under these rules.</summary>
        public static bool AllowsJob(JobTemplate t) => t == null || _current.AllowsJob(t.Owner, t.Kind);

        /// <summary>A dealer price times the car prices of these rules, rounded like the dealer's (50 under 10,000, else 500).</summary>
        public static long PriceScaled(long price)
        {
            float s = _current.PriceScale;
            if (price <= 0 || Math.Abs(s - 1f) < 1e-4f) return price;
            double p = price * (double)s;
            long step = p < 10000 ? 50 : 500;
            return Math.Min(Dealer.MaxPrice, Math.Max(step, (long)Math.Round(p / step) * step));
        }

        /// <summary>Guests of this host keep what they earn and buy (always true solo and hosting).</summary>
        public static bool GuestProgress => !FromHost || _current.GuestProgress;

        /// <summary>The career mod: these rules are in effect now.</summary>
        internal static void Set(CareerRuleSet rules, bool fromHost)
        {
            var r = (rules ?? new CareerRuleSet()).Clamped();
            bool same = r.SameAs(_current) && fromHost == FromHost;
            _current = r;
            FromHost = fromHost;
            if (same) return;
            Dealer.RulesChanged();
            Raise("Rules.Changed", Changed, h => h());
        }

        internal static void Reset()
        {
            _current = new CareerRuleSet();
            FromHost = false;
            Changed = null;
        }
    }
}
