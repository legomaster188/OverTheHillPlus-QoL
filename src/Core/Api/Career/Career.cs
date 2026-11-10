using System;
using System.Collections.Generic;
using System.Text;

namespace OverTheHillPlus.Api;

public static partial class Career
{
    /// <summary>The API's version: new members only add to it; anything that breaks bumps the major.</summary>
    public const string Version = "1.0";

    /// <summary>The board ids (<see cref="JobTemplate.Board"/>).</summary>
    public static class Boards
    {
        /// <summary>The job board at depots: deliveries, hauls, supply runs.</summary>
        public const string Depot = "depot";
        /// <summary>Race events.</summary>
        public const string Race = "race";
        /// <summary>Helping: other players, stranded cars.</summary>
        public const string Help = "help";
        /// <summary>Quests: one-off jobs, taken anywhere.</summary>
        public const string Quest = "quest";
        /// <summary>Jobs a shop gives out.</summary>
        public const string Shop = "shop";

        internal static readonly string[] All = { Depot, Race, Help, Quest, Shop };
    }

    internal static CareerBook Book { get; private set; }
    internal static ICareerDriver Driver { get; set; }

    /// <summary>A career is loaded for this game's player.</summary>
    public static bool Active => Book != null;

    /// <summary>A career is loaded and the session's rules let it earn and spend (<see cref="CareerRuleSet.CareerOn"/>).</summary>
    public static bool Playing => Book != null && Rules.CareerOn && Rules.GuestProgress;

    /// <summary>The loaded career is on Hardcore: a wreck stays with it, nothing gets it out of trouble for free, and the host tools stay off.</summary>
    public static bool Hardcore => Book != null && Book.Difficulty == 1;

    /// <summary>The loaded career's slot id ("" when none).</summary>
    public static string SlotId { get; private set; } = "";

    /// <summary>The loaded career's name ("" when none).</summary>
    public static string SlotName { get; private set; } = "";

    /// <summary>A career was loaded (its book is ready).</summary>
    public static event Action Started;

    /// <summary>The career was closed: back to free play.</summary>
    public static event Action Stopped;

    /// <summary>Where the API reports a misuse or a subscriber that threw (core's log; null in tests).</summary>
    internal static Action<string> Log { get; set; }

    // ---------------------------------------------------------------- the career mod

    /// <summary>The career mod: <paramref name="book"/> is now the loaded career's. A career still open is closed first.</summary>
    internal static void Open(string slotId, string name, CareerBook book)
    {
        if (book == null) return;
        if (Book != null) Close();
        Book = book;
        SlotId = Clean(slotId, 40);
        SlotName = Clean(name, 40);
        Raise("Started", Started, h => h());
    }

    /// <summary>The career mod: no career is loaded any more.</summary>
    internal static void Close()
    {
        if (Book == null) return;
        Book = null;
        SlotId = "";
        SlotName = "";
        Raise("Stopped", Stopped, h => h());
    }

    /// <summary>The book changed: the career mod saves it soon.</summary>
    internal static void Touch()
    {
        try { Driver?.Changed(); }
        catch (Exception e) { DebugLog.Error("core", e, "Career.Touch"); }
    }

    /// <summary>Forgets every registration and the loaded career (tests).</summary>
    internal static void ResetForTests()
    {
        Book = null;
        SlotId = SlotName = "";
        Driver = null;
        Started = null;
        Stopped = null;
        Wallet.Reset();
        Garage.Reset();
        Dealer.Reset();
        Upgrades.Reset();
        Jobs.Reset();
        Unlocks.Reset();
        Goods.Reset();
        Items.Reset();
        Progress.Reset();
        Rules.Reset();
        Logged.Clear();
    }

    // ---------------------------------------------------------------- shared helpers

    private static readonly HashSet<string> Logged = new();

    internal static void Warn(string key, string text)
    {
        if (!Logged.Add(key)) return;
        try { Log?.Invoke("[Career] " + text); }
        catch (Exception e) { DebugLog.Error("core", e, "Career.Warn"); }
    }

    /// <summary>Calls every subscriber of an event; one that throws is logged once and the rest still run.</summary>
    internal static void Raise<T>(string what, T handlers, Action<T> call) where T : Delegate
    {
        if (handlers == null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try { call((T)(object)d); }
            catch (Exception e)
            {
                DebugLog.Error("core", e, "Career." + what);
                Warn("event " + what, $"a {what} subscriber threw (further errors of it are not logged): {e.Message}");
            }
        }
    }

    /// <summary>2 to <paramref name="max"/> characters, each a-z, 0-9, '_', '.' or '-'.</summary>
    internal static bool IsId(string id, int max = 40)
    {
        if (id == null || id.Length < 2 || id.Length > max) return false;
        foreach (char c in id)
            if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '.' || c == '-')) return false;
        return true;
    }

    /// <summary>A car key: 1 to 48 letters, digits, '_', '-', '.' or spaces (the game's car names: "Brick", "GrandWoodman", "Hilo22").</summary>
    internal static bool IsCar(string car)
    {
        if (string.IsNullOrWhiteSpace(car) || car.Length > 48) return false;
        foreach (char c in car)
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ' ')) return false;
        return true;
    }

    /// <summary>Plain text: control characters and markup brackets out, whitespace runs folded, cut to <paramref name="max"/>.</summary>
    internal static string Clean(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max));
        bool space = false;
        foreach (char c in text)
        {
            if (sb.Length >= max) break;
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) space = true;
                continue;
            }
            char ch = c == '<' ? '(' : c == '>' ? ')' : c;
            if (space)
            {
                if (sb.Length + 1 >= max) break;
                sb.Append(' ');
                space = false;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Seconds since 1970 (UTC), for the ledger.</summary>
    internal static Func<long> Now = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>What the career mod does for the API: the jobs it runs and the saving of the loaded career.</summary>
internal interface ICareerDriver
{
    /// <summary>The job this game's player is doing, or null.</summary>
    JobView CurrentJob { get; }

    /// <summary>The offers on a board this game's player can see now (empty: none).</summary>
    IReadOnlyList<JobView> Offers(string board);

    /// <summary>Takes an offer (on a guest: asks the host). False with why.</summary>
    bool Accept(string offerId, out string why);

    /// <summary>Gives up the current job. False with why.</summary>
    bool Abandon(out string why);

    /// <summary>The loaded career's book changed: save it soon.</summary>
    void Changed();
}
