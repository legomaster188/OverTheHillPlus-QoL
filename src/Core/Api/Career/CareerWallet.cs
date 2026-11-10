using System;
using System.Collections.Generic;
using System.Globalization;

namespace OverTheHillPlus.Api;

public static partial class Career
{
    /// <summary>
    /// The loaded career's money, in pebbles (the game's own money: in a career with its own world the game's pebble
    /// count follows it, so the game's merchant spends the same money). Every change has a reason, kept in
    /// <see cref="Recent"/> and shown on the career screen. Changes are refused while no career is <see cref="Playing"/>.
    /// </summary>
    public static class Wallet
    {
        /// <summary>The name of the money, for text.</summary>
        public const string Unit = "pebbles";

        /// <summary>The money changed (also when the game's own merchant took or gave some).</summary>
        public static event Action<WalletChange> Changed;

        /// <summary>The balance (0 without a career).</summary>
        public static long Balance => Book?.Money ?? 0;

        /// <summary>True when the career can pay <paramref name="amount"/>.</summary>
        public static bool CanAfford(long amount) => Playing && amount >= 0 && Book.Money >= amount;

        /// <summary>The last changes, oldest first (at most 60).</summary>
        public static IReadOnlyList<WalletChange> Recent => Book != null ? Book.Ledger : Array.Empty<WalletChange>();

        /// <summary>
        /// Pays the career for something it did (a job, a reward): <paramref name="amount"/> times the session's payout
        /// multiplier (<see cref="CareerRuleSet.PayScale"/>: job pay times the money scale). Returns what was added (0: no career playing, or a bad
        /// amount). <paramref name="source"/> is your mod id (or "job", ...).
        /// </summary>
        public static long Earn(long amount, string reason, string source = null)
        {
            if (!Playing || amount <= 0) return 0;
            double m = Rules.PayScale;
            long scaled = (long)Math.Round(Math.Min(amount, CareerBook.MaxMoney) * m);
            return Add(scaled, reason, source, earned: true);
        }

        /// <summary>
        /// Adds exactly <paramref name="amount"/> (no multiplier): a refund, a sale at an agreed price, starting money, pay the
        /// host already worked out. Returns what was added (the balance never goes above the cap).
        /// </summary>
        public static long Credit(long amount, string reason, string source = null)
        {
            if (!Playing || amount <= 0) return 0;
            return Add(Math.Min(amount, CareerBook.MaxMoney), reason, source, earned: true);
        }

        /// <summary>Takes <paramref name="amount"/> when the career can pay it. False (and nothing taken) otherwise.</summary>
        public static bool Spend(long amount, string reason, string source = null)
        {
            if (!Playing || amount <= 0 || amount > CareerBook.MaxMoney || Book.Money < amount) return false;
            Book.Money -= amount;
            Book.Spent += amount;
            Record(-amount, reason, source);
            return true;
        }

        private static long Add(long amount, string reason, string source, bool earned)
        {
            if (amount <= 0) return 0;
            long room = CareerBook.MaxMoney - Book.Money;
            if (room <= 0)
            {
                Warn("wallet-full", "the career's money is at its cap; more is not added");
                return 0;
            }
            long add = Math.Min(amount, room);
            Book.Money += add;
            if (earned) Book.Earned += add;
            Record(add, reason, source);
            return add;
        }

        /// <summary>The career mod: the game's own pebble count moved by <paramref name="delta"/> (its merchant sold
        /// something, a point of interest paid): the book follows, with <paramref name="reason"/>. Refused below 0.</summary>
        internal static bool FromGame(long delta, string reason)
        {
            if (Book == null || delta == 0) return false;
            if (delta < 0)
            {
                long take = Math.Min(-delta, Book.Money);
                if (take <= 0) return false;
                Book.Money -= take;
                Book.Spent += take;
                Record(-take, reason, "game");
                return true;
            }
            long add = Math.Min(delta, CareerBook.MaxMoney - Book.Money);
            if (add <= 0) return false;
            Book.Money += add;
            Book.Earned += add;
            Record(add, reason, "game");
            return true;
        }

        private static void Record(long delta, string reason, string source)
        {
            var c = new WalletChange(delta, Book.Money, Clean(reason, 60), Clean(source, 24), Now());
            Book.AddLedger(c);
            Touch();
            Raise("Wallet.Changed", Changed, h => h(c));
        }

        /// <summary>"12,400 pebbles".</summary>
        public static string Format(long amount) => amount.ToString("N0", CultureInfo.InvariantCulture) + " " + Unit;

        /// <summary>"+1,250" / "-300".</summary>
        public static string FormatDelta(long delta) =>
            (delta >= 0 ? "+" : "-") + Math.Abs(delta).ToString("N0", CultureInfo.InvariantCulture);

        internal static void Reset() => Changed = null;
    }
}
