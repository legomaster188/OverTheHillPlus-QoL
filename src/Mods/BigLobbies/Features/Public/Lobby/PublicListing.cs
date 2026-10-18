using System;
using System.Reflection;
using BepInEx.Configuration;
using Fusion;
using OverTheHillPlus.BigLobbies;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.Lobby;

/// <summary>
/// Since the 2026-10-16 update the game's server browser hides every server with more than 4 slots
/// (OverlandSessionInfo.VisibleInList = MaxPlayers &lt;= 4), and its Join button refuses full servers
/// (PlayerCount &gt;= MaxPlayers). So players without the mods could never find a big lobby. While fewer than 4 players
/// are in, this advertises the room as a 4-slot room to Photon's lobby (Photon.Realtime Room.MaxPlayers), so every
/// player sees it ("2/4") and can join; once the 4th is in it advertises the real size again, so a 5th can still join
/// (from then on only players with Server Browser+ see it). Fusion's own player capacity is never touched; only the
/// number the lobby lists (and the room's join limit) changes. Host only, only for lobbies above 4 slots.
/// </summary>
internal sealed class PublicListing : IFeature
{
    internal const int VanillaMax = 4;
    internal static ConfigEntry<bool> Enabled;

    private static float _next;
    private static IntPtr _runner;
    private static int _realMax;
    private static int _lastSet = -1;
    private static bool _loggedFail, _loggedPrefabs;

    public void Register()
    {
        Enabled = BigLobbiesConfig.File.Bind("Multiplayer", "ShowToEveryoneUnder4", true,
            "Big lobbies you host show in every player's server browser (also without mods) while fewer than 4 players are in: " +
            "the lobby lists them as 4-slot rooms until the 4th joins, then the real size again. The game itself hides servers above 4 slots.");
        Registry.OnTick(Tick);
    }

    /// <summary>The slot count to advertise for <paramref name="players"/> in a lobby of <paramref name="realMax"/> slots.</summary>
    internal static int Advertised(int players, int realMax) =>
        realMax <= VanillaMax ? realMax : players < VanillaMax ? VanillaMax : realMax;

    private static void Tick()
    {
        if (Enabled == null || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 1f;
        try
        {
            var r = GameState.Runner;
            if (r == null || !r.IsRunning || !r.IsServer || r.IsSinglePlayer) { _runner = IntPtr.Zero; _realMax = 0; _lastSet = -1; return; }
            var room = Room(r);
            if (room == null) return;
            int max = Get<int>(room, "MaxPlayers"), players = Get<int>(room, "PlayerCount");
            if (r.Pointer != _runner)
            {
                _runner = r.Pointer;
                _realMax = max; // the size the room was created with (Big Lobbies' choice)
                _lastSet = -1;
                if (_realMax > VanillaMax)
                    BigLobbiesPlugin.Logger.LogInfo($"[Lobby] public listing: a {_realMax}-slot room; " +
                        (Enabled.Value ? "listed as 4 slots for players without mods while fewer than 4 are in" : "switched off: only Server Browser+ users see it"));
            }
            if (_realMax <= VanillaMax) return;
            // With added cars/prefabs (Vera, Tercel, Aro...) the host refuses every game without them, and the game
            // shows that as "banned from this session": don't list the room for players without mods at all then.
            bool addedPrefabs = Core.Shared.NetPrefabRegistry.MarkerValue != null;
            if (addedPrefabs && !_loggedPrefabs)
            {
                _loggedPrefabs = true;
                BigLobbiesPlugin.Logger.LogInfo("[Lobby] public listing: this server has added cars/prefabs, which players without the same mods can't load, " +
                    "so the room keeps its real size and only players with the mods see it.");
            }
            int want = Enabled.Value && !addedPrefabs ? Advertised(players, _realMax) : _realMax;
            if (want == max) return;
            Set(room, "MaxPlayers", want);
            if (want != _lastSet)
            {
                _lastSet = want;
                BigLobbiesPlugin.Logger.LogInfo($"[Lobby] public listing: {players} player(s) in, the lobby now lists the room as {want} slots" +
                    (want == VanillaMax ? " (every player can see and join it)" : " (only Server Browser+ users see it from now on)"));
            }
        }
        catch (Exception e)
        {
            DebugLog.Error(BigLobbiesPlugin.ModId, e, "PublicListing.Tick");
            if (!_loggedFail) { _loggedFail = true; BigLobbiesPlugin.Logger.LogWarning("[Lobby] public listing failed: " + e.Message); }
        }
    }

    /// <summary>Photon's room behind the runner, by reflection over the interop wrappers (names vary between Fusion
    /// builds): NetworkRunner._cloudServices._communicator (an ICommunicator, cast to CloudCommunicator).Client.CurrentRoom
    /// since the game's 10-16 update, NetworkRunner._cloudServices.Client.CurrentRoom before it.</summary>
    private static object Room(NetworkRunner r)
    {
        object cloud = First(r, "_cloudServices", "CloudServices");
        if (cloud == null) return null;
        object client = First(cloud, "Client", "RealtimeClient");
        if (client == null)
        {
            object communicator = First(cloud, "_communicator", "Communicator");
            if (communicator is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase wrapped)
                communicator = wrapped.TryCast<CloudCommunicator>() ?? communicator;
            client = communicator == null ? null : First(communicator, "Client", "RealtimeClient");
        }
        if (client == null) throw new MissingMemberException("CloudServices", "_communicator.Client");
        return Get<object>(client, "CurrentRoom");
    }

    /// <summary>The first of <paramref name="names"/> that <paramref name="o"/> has and that is not null.</summary>
    private static object First(object o, params string[] names)
    {
        foreach (var n in names)
        {
            try { var v = Get<object>(o, n); if (v != null) return v; }
            catch (MissingMemberException) { }
        }
        return null;
    }

    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static T Get<T>(object o, string name)
    {
        var t = o.GetType();
        var p = t.GetProperty(name, All);
        if (p != null) return (T)p.GetValue(o);
        var f = t.GetField(name, All);
        if (f != null) return (T)f.GetValue(o);
        var m = t.GetMethod("get_" + name, All, null, Type.EmptyTypes, null);
        if (m != null) return (T)m.Invoke(o, null);
        throw new MissingMemberException(t.Name, name);
    }

    private static void Set(object o, string name, object value)
    {
        var p = o.GetType().GetProperty(name, All);
        if (p == null || !p.CanWrite) throw new MissingMemberException(o.GetType().Name, name);
        p.SetValue(o, value);
    }
}
