using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Api;

public static partial class Career
{
    /// <summary>
    /// Loose things a career owns besides its cars, so they can be traded: cosmetics, liveries and upgrades that aren't on a
    /// car. Each is a kind (<see cref="Kinds"/>) and an id with a count. An upgrade's id is "&lt;upgrade id&gt;:&lt;level&gt;"
    /// (<see cref="UpgradeItem"/>); <see cref="FitUpgrade"/> puts one on an owned car and <see cref="TakeOffUpgrade"/> takes
    /// one off into this inventory. In a career with its own world, the career mod keeps the game's own cosmetic and livery
    /// items in step with these.
    /// </summary>
    public static class Items
    {
        public static class Kinds
        {
            public const string Cosmetic = "cosmetic", Livery = "livery", Upgrade = "upgrade";
            internal static readonly string[] All = { Cosmetic, Livery, Upgrade };
        }

        /// <summary>An item changed (its kind and id).</summary>
        public static event Action<string, string> Changed;

        private static string Key(string kind, string id) => kind + ":" + id;

        internal static bool KindOk(string kind) => Array.IndexOf(Kinds.All, kind) >= 0;

        /// <summary>An id: 1 to 64 of letters, digits, '_', '.', '-', ':' (a game item's id as text is fine: "3771754783").</summary>
        internal static bool IdOk(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-' || c == ':')) return false;
            return true;
        }

        public static int Count(string kind, string id) =>
            Book != null && KindOk(kind) && IdOk(id) && Book.Items.TryGetValue(Key(kind, id), out int n) ? n : 0;

        /// <summary>Every item of a kind with its count (empty without a career).</summary>
        public static Dictionary<string, int> All(string kind)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            if (Book == null || !KindOk(kind)) return d;
            string prefix = kind + ":";
            foreach (var kv in Book.Items)
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) d[kv.Key.Substring(prefix.Length)] = kv.Value;
            return d;
        }

        /// <summary>Adds items. Returns how many were added (0: no career playing, a bad kind or id, the inventory full).</summary>
        public static int Add(string kind, string id, int count, string reason)
        {
            if (!Playing || !KindOk(kind) || !IdOk(id) || count <= 0) return 0;
            string k = Key(kind, id);
            Book.Items.TryGetValue(k, out int have);
            if (have == 0 && Book.Items.Count >= CareerBook.MaxItems) return 0;
            int add = Math.Min(count, CareerBook.MaxItemCount - have);
            if (add <= 0) return 0;
            Book.Items[k] = have + add;
            Touch();
            Raise("Items.Changed", Changed, h => h(kind, id));
            return add;
        }

        /// <summary>Takes items away (sold, traded, fitted). Returns how many were taken.</summary>
        public static int Remove(string kind, string id, int count)
        {
            if (Book == null || !KindOk(kind) || !IdOk(id) || count <= 0) return 0;
            string k = Key(kind, id);
            if (!Book.Items.TryGetValue(k, out int have)) return 0;
            int take = Math.Min(have, count);
            if (have - take <= 0) Book.Items.Remove(k);
            else Book.Items[k] = have - take;
            Touch();
            Raise("Items.Changed", Changed, h => h(kind, id));
            return take;
        }

        /// <summary>The item id of an upgrade at a level ("engine:3").</summary>
        public static string UpgradeItem(string upgrade, int level) => upgrade + ":" + level;

        /// <summary>Takes an upgrade off an owned car into the inventory, at the level it had (the car goes back to stock for
        /// it). False when the car doesn't have it.</summary>
        public static bool TakeOffUpgrade(string car, string upgrade)
        {
            if (!Playing || !Garage.Owns(car)) return false;
            int level = Book.Level(car, upgrade);
            if (level <= 0) return false;
            if (Add(Kinds.Upgrade, UpgradeItem(upgrade, level), 1, "Taken off the " + car) == 0) return false;
            Book.SetLevel(car, upgrade, 0);
            Touch();
            Raise("Garage.Changed", Garage.ChangedEvent, h => h(car));
            return true;
        }

        /// <summary>Fits an upgrade from the inventory to an owned car (it replaces the level the car had, which goes into the
        /// inventory). False when there is none, or the upgrade doesn't fit the car.</summary>
        public static bool FitUpgrade(string car, string upgrade, int level)
        {
            if (!Playing || !Garage.Owns(car)) return false;
            var def = Upgrades.Find(upgrade);
            if (def == null || !def.Fits(car) || level < 1 || level > def.Levels.Count) return false;
            string item = UpgradeItem(upgrade, level);
            if (Count(Kinds.Upgrade, item) <= 0) return false;
            int had = Book.Level(car, upgrade);
            if (!Book.SetLevel(car, upgrade, level)) return false;
            Remove(Kinds.Upgrade, item, 1);
            if (had > 0) Add(Kinds.Upgrade, UpgradeItem(upgrade, had), 1, "Swapped off the " + car);
            Touch();
            Raise("Garage.Changed", Garage.ChangedEvent, h => h(car));
            return true;
        }

        internal static void Reset() => Changed = null;
    }
}
