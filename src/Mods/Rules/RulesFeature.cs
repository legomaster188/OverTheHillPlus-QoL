using OverTheHillPlus.Features;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Rules;

/// <summary>Wires Server Rules into core: its mod network channel, per-frame work, overlay and vote keys.</summary>
internal sealed class RulesFeature : IFeature
{
    // Compile-time check: the pure wire file's channel is the one core's table reserves for Server Rules.
    private const int ChannelAgrees = 1 / (RulesWire.Channel == Net.Channels.Rules ? 1 : 0);

    public void Register()
    {
        Net.Register(RulesWire.Channel, new Net.ChannelOptions
        {
            Name = "server rules",
            Version = RulesWire.Version,
            MaxPayload = RulesWire.MaxPayload,
            RatePerSecond = 2f,
            Burst = 6,
            AllowRelay = false,     // guests only ever talk to the host
            HostOnlySender = true,  // guests drop anything that did not come from the host itself
        }, msg =>
        {
            if (Net.IsHost) RulesHost.OnGuestMessage(msg);
            else RulesClient.OnHostMessage(msg);
        });
        Net.OnPeerGone(RulesHost.PeerGone);
        Net.OnSessionReset(RulesClient.Reset);
        Registry.OnTick(RulesHost.Tick);
        Registry.OnGui(RulesOverlay.Draw);
        CoreBehaviour.Hotkeys += RulesClient.Hotkeys;
    }
}
