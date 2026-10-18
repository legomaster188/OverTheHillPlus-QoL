using System;

namespace OverTheHillPlus.Features.Public.ModNet;

/// <summary>A token bucket on an explicit clock (pure: the mod network passes Time.unscaledTime).</summary>
internal sealed class TokenBucket
{
    /// <summary>Tokens now; negative until first used (it then starts full).</summary>
    public float Tokens = -1f;
    public float Last;

    public void Refill(float now, float rate, float burst)
    {
        if (Tokens < 0f) Tokens = burst;
        else if (now > Last) Tokens = Math.Min(burst, Tokens + (now - Last) * rate);
        else Tokens = Math.Min(burst, Tokens);
        Last = Math.Max(Last, now);
    }

    public bool Take(float now, float rate, float burst, float cost)
    {
        Refill(now, rate, burst);
        if (Tokens < cost) return false;
        Tokens -= cost;
        return true;
    }
}

/// <summary>
/// The mod network's send budget per receiver (pure logic, unit-tested). Every receiver drops whatever one sender
/// sends over <see cref="InRate"/> messages a second (burst <see cref="InBurst"/>) or <see cref="InBytesRate"/> bytes a
/// second (burst <see cref="InBytesBurst"/>), all channels together. The host is one sender for every channel it
/// broadcasts, and the channels' own caps add up to far more than that, so the sender keeps a total per receiver a
/// little under what the receiver accepts (its bucket and ours start full and tick on different clocks): overflow is
/// then <c>RateLimited</c> here instead of a silent drop over there. Each channel may use at most
/// <see cref="MaxShare"/> of that total (rate and burst), so one busy channel never starves the others; the control
/// channel (hello / welcome) only counts toward the total.
/// </summary>
internal static class ModNetBudget
{
    public const float InRate = 20f, InBurst = 40f, InBytesRate = 16384f, InBytesBurst = 32768f;
    public const float OutRate = 18f, OutBurst = 36f, OutBytesRate = 14336f, OutBytesBurst = 28672f;
    public const float MaxShare = 0.75f;

    /// <summary>A channel's own rate, capped at its share of the total.</summary>
    public static float ShareRate(float channelRate) => Math.Min(channelRate, OutRate * MaxShare);

    /// <summary>A channel's own burst, capped at its share of the total (at least one message).</summary>
    public static float ShareBurst(float channelBurst) => Math.Max(1f, Math.Min(channelBurst, (float)Math.Floor(OutBurst * MaxShare)));

    /// <summary>
    /// Spends one message of <paramref name="payloadBytes"/> to one receiver, from its channel bucket and the receiver's
    /// totals, all or nothing. False: over the budget (nothing spent).
    /// </summary>
    public static bool TrySpend(float now, TokenBucket channel, TokenBucket msgs, TokenBucket bytes,
        float channelRate, float channelBurst, bool control, int payloadBytes)
    {
        float rate = control ? channelRate : ShareRate(channelRate);
        float burst = control ? channelBurst : ShareBurst(channelBurst);
        channel.Refill(now, rate, burst);
        msgs.Refill(now, OutRate, OutBurst);
        bytes.Refill(now, OutBytesRate, OutBytesBurst);
        float cost = Math.Max(1, payloadBytes);
        if (channel.Tokens < 1f || msgs.Tokens < 1f || bytes.Tokens < cost) return false;
        channel.Tokens -= 1f;
        msgs.Tokens -= 1f;
        bytes.Tokens -= cost;
        return true;
    }
}
