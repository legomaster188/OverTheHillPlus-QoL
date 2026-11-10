using System;
using System.Collections.Generic;
using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public.ModNet;
using UnityEngine;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// Tells players whose custom parts are another set than the host's (mod-net channel 51, <see cref="PartsSignature"/>):
/// the host sends its set to each player who joins with the mod, a guest sends its own to the host once the host welcomed
/// it, and whoever finds the other's set different gets a plain notice (the guest about the host, the host about the
/// guest), once per player per session. Players without the mod get nothing and see no custom parts at all, which is
/// harmless; a different set is the case that shows wrong parts.
/// </summary>
internal sealed class PartsHandshake : IFeature
{
    /// <summary>ModNet.Channels.CustomParts (kept equal).</summary>
    internal const ushort Channel = 51;
    private const byte MsgSet = 1;

    private static bool _channel;
    private static PartsSignature.Set _mine;
    private static readonly HashSet<int> Told = new();
    private static bool _toldAboutHost;
    private static string _pending;
    private static float _pendingSince;

    public void Register()
    {
        _channel = ModNet.Register(Channel, new ModNet.ChannelOptions
        {
            Name = "custom parts", Version = 1, MinVersion = 1, MaxPayload = PartsSignature.MaxPayload,
            RatePerSecond = 1f, Burst = 4,
            Active = () => CustomCosmeticsPlugin.Problem == null && !PartObjects.Off,
        }, OnMessage);
        if (!_channel)
        {
            CustomCosmeticsPlugin.Logger?.LogWarning($"Custom Cosmetics: mod-net channel {Channel} is taken: players with another set of parts won't be told");
            return;
        }
        ModNet.OnPeerReady(p =>
        {
            if (!ModNet.IsHost || !ModNet.PeerHas(p, Channel)) return;
            var mine = Mine();
            if (mine != null) ModNet.SendTo(p, Channel, MsgSet, PartsSignature.Write(mine));
        });
        ModNet.OnHostReady(() =>
        {
            if (!ModNet.HostHas(Channel)) return;
            var mine = Mine();
            if (mine != null) ModNet.SendToHost(Channel, MsgSet, PartsSignature.Write(mine));
        });
        ModNet.OnPeerGone(p => Told.Remove(p.RawEncoded));
        ModNet.OnSessionReset(() =>
        {
            Told.Clear();
            _toldAboutHost = false;
            _pending = null;
        });
        Registry.OnTick(Tick);
    }

    /// <summary>This game's set, worked out once (the library is fixed for the session).</summary>
    private static PartsSignature.Set Mine()
    {
        if (_mine != null) return _mine;
        try
        {
            var parts = new List<PartsSignature.Part>();
            foreach (var p in PartLibrary.Parts)
                parts.Add(new PartsSignature.Part(p.Id, p.ItemId, p.Manifest.Fits.Keys, p.Manifest.Replaces));
            _mine = new PartsSignature.Set { ModVersion = CustomCosmeticsPlugin.Version, Count = parts.Count, Hash = PartsSignature.Of(parts) };
            DebugLog.Info(CustomCosmeticsPlugin.ModId, $"part set for other players: {_mine.Describe()}, {_mine.Hash}");
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "PartsHandshake.Mine"); }
        return _mine;
    }

    private static void OnMessage(ModNet.Message m)
    {
        try
        {
            if (m.Type != MsgSet) return;
            var theirs = PartsSignature.Read(m.Payload);
            var mine = Mine();
            if (theirs == null || mine == null) return;
            bool same = theirs.SameParts(mine);
            if (ModNet.IsHost)
            {
                DebugLog.Info(CustomCosmeticsPlugin.ModId, $"player {m.From.PlayerId}'s part set: {theirs.Describe()}, {theirs.Hash}{(same ? " (same)" : " (differs)")}");
                if (same || !Told.Add(m.From.RawEncoded)) return;
                Notify(PartsSignature.HostNotice(theirs, mine));
            }
            else if (m.FromHost)
            {
                DebugLog.Info(CustomCosmeticsPlugin.ModId, $"the host's part set: {theirs.Describe()}, {theirs.Hash}{(same ? " (same)" : " (differs)")}");
                if (same || _toldAboutHost) return;
                _toldAboutHost = true;
                Notify(PartsSignature.GuestNotice(theirs, mine));
            }
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "PartsHandshake.OnMessage"); }
    }

    private static void Notify(string text)
    {
        CustomCosmeticsPlugin.Logger?.LogWarning("Custom Cosmetics: " + text);
        _pending = text;
        _pendingSince = Time.unscaledTime;
        Tick();
    }

    /// <summary>The notice goes out as the game's own notification once its HUD is up (in the lobby or the world).</summary>
    private static void Tick()
    {
        if (_pending == null) return;
        if (UI.GameMenu.GameToast.Show(_pending, UI.GameMenu.GameToast.Warning, 10f) || Time.unscaledTime - _pendingSince > 90f)
            _pending = null;
    }
}
