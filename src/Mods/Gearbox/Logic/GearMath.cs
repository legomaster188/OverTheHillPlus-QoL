using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.GearboxPlus;

/// <summary>Gearbox+'s pure maths and wording (no game types, unit tested).</summary>
internal static class GearMath
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The game's cars have two forward gears.</summary>
    public const int StockGears = 2;
    public const int MaxGears = 5;
    /// <summary>A gear taller than this ratio is not added: past it the car would not pull at all.</summary>
    public const float MinRatio = 0.35f;
    /// <summary>The step from one gear to the next must lie in this range to be continued (1st to 2nd of a sane box).</summary>
    public const float MinStep = 0.3f, MaxStep = 0.98f;

    public static int ClampGears(int gears) => gears < StockGears ? StockGears : gears > MaxGears ? MaxGears : gears;

    // ---------------------------------------------------------------- the ratios

    /// <summary>
    /// <paramref name="stock"/> (forward ratios, 1st first, each lower than the one before) continued to
    /// <paramref name="gears"/> gears with the car's own step: the last stock ratio divided by the one before it, again
    /// and again, so a car with a close 1st to 2nd keeps close gears and one with a wide step keeps wide ones. A copy of
    /// <paramref name="stock"/> comes back when there is nothing to add: the car already has that many gears, fewer are
    /// asked for, or its ratios are not something a step can be taken from (under two gears, zero, negative, not
    /// falling, not a number). Gears that would be taller than <see cref="MinRatio"/> are left out, so the result can have
    /// fewer than <paramref name="gears"/>. Never throws.
    /// </summary>
    public static float[] Extend(float[] stock, int gears)
    {
        if (stock == null) return new float[0];
        var result = new List<float>(stock);
        gears = ClampGears(gears);
        int n = stock.Length;
        if (n < 2 || n >= gears) return result.ToArray();
        float last = stock[n - 1], prev = stock[n - 2];
        if (!Valid(last) || !Valid(prev)) return result.ToArray();
        float step = last / prev;
        if (float.IsNaN(step) || step < MinStep || step > MaxStep) return result.ToArray();
        for (int i = n; i < gears; i++)
        {
            float next = result[i - 1] * step;
            if (!Valid(next) || next < MinRatio) break;
            result.Add(next);
        }
        return result.ToArray();
    }

    private static bool Valid(float ratio) => ratio > 0f && !float.IsNaN(ratio) && !float.IsInfinity(ratio);

    /// <summary>How many gears a car gets: the setting, or its stock count when it is kept stock or the mod is not active.</summary>
    public static int GearsFor(int setting, bool keepStock, bool active, int stockCount) =>
        keepStock || !active ? stockCount : Math.Max(stockCount, ClampGears(setting));

    // ---------------------------------------------------------------- the cars kept stock

    private const char Separator = '|';

    public static HashSet<string> ParseCars(string list)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(list)) return set;
        foreach (var part in list.Split(Separator))
        {
            string name = part.Trim();
            if (name.Length > 0) set.Add(name);
        }
        return set;
    }

    public static string JoinCars(IEnumerable<string> cars)
    {
        var sb = new StringBuilder();
        foreach (var c in cars)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            if (sb.Length > 0) sb.Append(Separator);
            sb.Append(c.Trim().Replace(Separator, ' '));
        }
        return sb.ToString();
    }

    public static bool IsKeptStock(string list, string car) =>
        !string.IsNullOrWhiteSpace(car) && ParseCars(list).Contains(car.Trim().Replace(Separator, ' '));

    /// <summary>The list with <paramref name="car"/> put in (<paramref name="keep"/>) or taken out.</summary>
    public static string SetKeptStock(string list, string car, bool keep)
    {
        if (string.IsNullOrWhiteSpace(car)) return list ?? "";
        var set = ParseCars(list);
        string name = car.Trim().Replace(Separator, ' ');
        if (keep) set.Add(name); else set.Remove(name);
        var sorted = new List<string>(set);
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        return JoinCars(sorted);
    }

    // ---------------------------------------------------------------- shifting

    /// <summary>
    /// The game steps its manual gear 1 to R and R to 1 in one press (it has no neutral between them). With a real
    /// neutral that step stops at N: 0 comes back for a 1-to-R or R-to-1 step, anything else as it was.
    /// </summary>
    public static int NeutralBetween(int before, int after, bool neutral)
    {
        if (!neutral) return after;
        if (before == 1 && after < 0) return 0;
        if (before < 0 && after == 1) return 0;
        return after;
    }

    /// <summary>The press that brings <paramref name="selected"/> one step closer to neutral: -1 down, +1 up, 0 already there.</summary>
    public static int StepToNeutral(int selected) => selected > 0 ? -1 : selected < 0 ? 1 : 0;

    // ---------------------------------------------------------------- the indicator

    /// <summary>"R", "N" or the gear number (the gearbox's gear: negative is reverse, 0 neutral).</summary>
    public static string GearText(int gear) => gear < 0 ? "R" : gear == 0 ? "N" : gear.ToString(Inv);

    /// <summary>The slots of the indicator: R, N, then 1 to <paramref name="gears"/>.</summary>
    public static string[] Slots(int gears)
    {
        gears = Math.Max(1, Math.Min(MaxGears, gears));
        var slots = new string[gears + 2];
        slots[0] = "R";
        slots[1] = "N";
        for (int i = 1; i <= gears; i++) slots[i + 1] = i.ToString(Inv);
        return slots;
    }

    /// <summary>Which slot of <see cref="Slots"/> is lit for the gearbox's gear (a gear past the last lights the last).</summary>
    public static int ActiveSlot(int gear, int gears)
    {
        gears = Math.Max(1, Math.Min(MaxGears, gears));
        if (gear < 0) return 0;
        if (gear == 0) return 1;
        return Math.Min(gear, gears) + 1;
    }

    /// <summary>"4H" or "4L".</summary>
    public static string RangeText(bool low) => low ? "4L" : "4H";
}
