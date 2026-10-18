using System;
using Fusion;
using OverTheHillPlus.Features.Public.ModNet;

namespace OverTheHillPlus.Api;

/// <summary>
/// Messages between players who run your mod, over the Mod Manager's mod network. Nothing is ever sent to a player
/// who doesn't run the Mod Manager (the game would choke on it): a send to them is skipped and says why.
///
/// <para>Pick one channel id per message kind from <see cref="FirstChannel"/>..<see cref="LastChannel"/> (the lower ids
/// belong to the Mod Manager's own mods) and keep it forever; choose something unlikely to clash with another
/// creator's mod. Register it once from your Load. Messages carry a type byte of your own and your channel's version;
/// payloads are your own bytes, capped per channel, and every sender is rate limited.</para>
///
/// <para>Handlers run on the main thread. Anything a handler shows to other players, check with <see cref="TextFilter"/>
/// first; treat every payload as untrusted (another player may run a modified copy).</para>
/// </summary>
public static class Net
{
    /// <summary>The channel ids other creators' mods may use.</summary>
    public const int FirstChannel = ApiRules.MinChannel, LastChannel = ApiRules.MaxChannel;

    /// <summary>Largest payload a channel may allow, in bytes.</summary>
    public const int MaxPayload = ApiRules.MaxPayload;

    /// <summary>How a channel behaves. Rates are per sender, checked when messages arrive (and when you send, so your
    /// own sends never trip them).</summary>
    public sealed class ChannelOptions
    {
        /// <summary>Shown in logs.</summary>
        public string Name = "?";
        /// <summary>The version this build sends, 1-255. Add fields at the end when you change a message; never reorder.</summary>
        public byte Version = 1;
        /// <summary>The oldest version this build accepts; older messages are dropped.</summary>
        public byte MinVersion = 1;
        /// <summary>Largest payload in bytes (at most <see cref="Net.MaxPayload"/>).</summary>
        public int MaxPayload = 1024;
        /// <summary>Messages per second per sender (at most 20).</summary>
        public float RatePerSecond = 5f;
        /// <summary>Messages a sender may send at once before the rate applies (at most 40).</summary>
        public int Burst = 10;
        /// <summary>Guests may send to other guests through the host.</summary>
        public bool AllowRelay;
        /// <summary>Guests drop anything that didn't come from the host itself.</summary>
        public bool HostOnlySender;
        /// <summary>The channel is offered to the other side only while this returns true (null: always), e.g. while
        /// your feature is switched on.</summary>
        public Func<bool> Active;
    }

    /// <summary>A message that arrived.</summary>
    public sealed class Message
    {
        public int Channel;
        /// <summary>Your own message type.</summary>
        public byte Type;
        /// <summary>The sender's channel version.</summary>
        public byte Version;
        /// <summary>The player who wrote it (for a relayed message, the original sender).</summary>
        public PlayerRef From;
        /// <summary>On a guest: it came from the host itself, not relayed from another guest.</summary>
        public bool FromHost;
        /// <summary>It was relayed by the host from another guest.</summary>
        public bool Relayed;
        public byte[] Payload;
    }

    public enum SendResult { Sent, Off, NotConnected, NotModded, UnknownChannel, TooBig, RateLimited, NotAllowed, Failed }

    /// <summary>Channels a mod may register, and all other creators' mods together.</summary>
    public const int MaxChannelsPerMod = ApiRules.MaxChannelsPerMod, MaxChannelsTotal = ApiRules.MaxChannelsTotal;

    private static readonly ApiRules.ChannelQuota Quota = new();

    /// <summary>
    /// Registers a channel and its handler, once, from your Load. False (and logged) when the channel is outside
    /// <see cref="FirstChannel"/>..<see cref="LastChannel"/>, already taken, or over <see cref="MaxChannelsPerMod"/>
    /// for your mod or <see cref="MaxChannelsTotal"/> for every other creator's mod together. Throws
    /// ArgumentException for an invalid mod id or one of ours.
    /// </summary>
    public static bool Register(string modId, int channel, ChannelOptions options, Action<Message> handler)
    {
        ApiGuard.Require(modId, "Net.Register");
        if (!ApiRules.IsThirdPartyChannel(channel))
        {
            Mods.Log($"[{modId}] Net.Register: channel {channel} is outside {FirstChannel}-{LastChannel}");
            return false;
        }
        string quota = Quota.Problem(modId);
        if (quota != null)
        {
            Mods.Log($"[{modId}] Net.Register: channel {channel} refused: {quota}");
            return false;
        }
        options ??= new ChannelOptions();
        var (payload, rate, burst) = ApiRules.ClampLimits(options.MaxPayload, options.RatePerSecond, options.Burst);
        var active = options.Active;
        var internalOptions = new ModNet.ChannelOptions
        {
            Name = $"{modId}: {options.Name ?? "?"}",
            Version = options.Version,
            MinVersion = options.MinVersion,
            MaxPayload = payload,
            RatePerSecond = rate,
            Burst = burst,
            AllowRelay = options.AllowRelay,
            HostOnlySender = options.HostOnlySender,
            Active = active,
        };
        ModNet.Handler h = null;
        if (handler != null)
            h = m => handler(new Message
            {
                Channel = m.Channel, Type = m.Type, Version = m.Version, From = m.From, FromHost = m.FromHost, Relayed = m.Relayed,
                Payload = m.Payload,
            });
        bool ok = ModNet.Register((ushort)channel, internalOptions, h);
        if (ok) Quota.Take(modId);
        else Mods.Log($"[{modId}] Net.Register: channel {channel} is already taken");
        return ok;
    }

    /// <summary>Guest to host.</summary>
    public static SendResult SendToHost(int channel, byte type, byte[] payload) =>
        !ApiRules.IsThirdPartyChannel(channel) ? SendResult.NotAllowed : Map(ModNet.SendToHost((ushort)channel, type, payload));

    /// <summary>Host to a guest; guest to another guest through the host (when the channel allows relaying).</summary>
    public static SendResult SendTo(PlayerRef to, int channel, byte type, byte[] payload) =>
        !ApiRules.IsThirdPartyChannel(channel) ? SendResult.NotAllowed : Map(ModNet.SendTo(to, (ushort)channel, type, payload));

    /// <summary>Host to every guest that runs the channel; guest to everyone through the host. Returns how many it was sent to.</summary>
    public static int Broadcast(int channel, byte type, byte[] payload) =>
        !ApiRules.IsThirdPartyChannel(channel) ? 0 : ModNet.Broadcast((ushort)channel, type, payload);

    /// <summary>True while you host a session.</summary>
    public static bool IsHost => ModNet.IsHost;

    /// <summary>True while you are a guest in someone's session.</summary>
    public static bool IsGuest => ModNet.IsGuest;

    /// <summary>On a guest: the host runs this channel (and accepts it right now).</summary>
    public static bool HostHas(int channel) => ApiRules.IsThirdPartyChannel(channel) && ModNet.HostHas((ushort)channel);

    /// <summary>On the host: this guest runs this channel.</summary>
    public static bool PeerHas(PlayerRef player, int channel) => ApiRules.IsThirdPartyChannel(channel) && ModNet.PeerHas(player, (ushort)channel);

    /// <summary>A modded player is ready to receive messages (on the host: a guest said hello).</summary>
    public static void OnPeerReady(Action<PlayerRef> hook) => ModNet.OnPeerReady(hook);

    /// <summary>A modded player left.</summary>
    public static void OnPeerGone(Action<PlayerRef> hook) => ModNet.OnPeerGone(hook);

    /// <summary>On a guest: the host answered, so <see cref="HostHas"/> is known.</summary>
    public static void OnHostReady(Action hook) => ModNet.OnHostReady(hook);

    /// <summary>The session ended or changed: forget everything about the other players.</summary>
    public static void OnSessionReset(Action hook) => ModNet.OnSessionReset(hook);

    private static SendResult Map(ModNet.SendResult r) => r switch
    {
        ModNet.SendResult.Sent => SendResult.Sent,
        ModNet.SendResult.Off => SendResult.Off,
        ModNet.SendResult.NotConnected => SendResult.NotConnected,
        ModNet.SendResult.NotModded => SendResult.NotModded,
        ModNet.SendResult.UnknownChannel => SendResult.UnknownChannel,
        ModNet.SendResult.TooBig => SendResult.TooBig,
        ModNet.SendResult.RateLimited => SendResult.RateLimited,
        ModNet.SendResult.NotAllowed => SendResult.NotAllowed,
        _ => SendResult.Failed,
    };
}
