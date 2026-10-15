using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Configuration;
using Fusion;
using Fusion.Sockets;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.Names;

/// <summary>Registers the overflow-name side channel (see <see cref="OverflowNames"/>).</summary>
internal sealed class OverflowNamesFeature : IFeature
{
    public void Register()
    {
        OverflowNames.Bind();
        OverflowNames.Install();
        Registry.OnTick(OverflowNames.Tick);
        Registry.Section(Registry.PlusMode, "Lobby", OverflowNames.DrawCard, 40);
    }
}

/// <summary>
/// Names for players past the game's name table.
///
/// The game keeps player names in a networked dictionary with room for 7 entries (OverlandSessionPlayers.
/// PlayerRefToNameDictionary, keyed PlayerId); in a big lobby the 8th player onwards has no name there and
/// GetPlayerName returns "" (decoded with Cpp2IL), which the lobby guards turn into "Player N".
///
/// Side channel: Fusion reliable data, the same path the game uses for the room code. The game's handler
/// (OverlandNetworkRunner.OnReliableDataReceived) switches on the first int of the ReliableKey (1 = RuntimePlayer,
/// 2 = room code) and throws ArgumentOutOfRange for anything else, so messages with our magic first word are
/// handled and swallowed by a prefix before the game sees them.
///
/// Handshake (so vanilla players never receive anything they can't read):
/// - A modded client says HELLO to the host, but only in a lobby bigger than the stock 4 players: such a host must
///   be modded, so a vanilla host never gets an unknown message.
/// - The host records every name passed to AddPlayer (including players with no slot in the name table) and sends
///   the table only to clients that said hello, again whenever it changes.
/// - Clients answer GetPlayerName from the received table when the game's own table has nothing.
/// Vanilla clients keep the game's behaviour ("Player N" for overflow players).
/// </summary>
internal static class OverflowNames
{
    public static ConfigEntry<bool> Enabled;

    // ReliableKey words: [Magic, message, protocol, 0]. The game only uses 1 and 2 as word 0.
    private const int Magic = 0x4F544850; // "OTHP"
    private const int MsgHello = 1, MsgTable = 2;
    private const int Protocol = 1;
    private const int StockMaxPlayers = 4;
    private const int MaxNameLength = 32, MaxEntries = 64, MaxPayload = 8192;
    private const char FieldSep = '\u001F', EntrySep = '\u001E';

    // Host: every name AddPlayer saw this session (PlayerId -> name), plus when a player left (kept briefly so
    // "left" messages and late UI refreshes still find the name).
    private static readonly Dictionary<int, string> HostNames = new();
    private static readonly Dictionary<int, float> LeftAt = new();
    private static readonly HashSet<int> ModdedClients = new();          // PlayerRef.RawEncoded
    private static readonly Dictionary<int, int> SentVersion = new();    // RawEncoded -> table version sent
    private static int _tableVersion = 1;

    // Client: the table the host sent us.
    private static readonly Dictionary<int, string> RemoteNames = new();
    private static bool _gotTable;
    private static int _helloCount;
    private static float _nextHello;

    private static IntPtr _runnerPtr;
    private static float _runnerGoneSince = -1f;
    private static float _nextTick;
    private static bool _installed, _canReceive, _receiveFaulted;
    private static string _lastError;
    private static Harmony _harmony;
    private static System.Reflection.MethodInfo _receiveTarget;

    // Keep sent buffers referenced for a moment in case Fusion reads them after the call returns.
    private static readonly Queue<(float time, Il2CppStructArray<byte> arr, Il2CppSystem.ReadOnlySpan<byte> span)> Recent = new();

    private static readonly HashSet<string> LoggedOnce = new();

    public static void Bind()
    {
        // Off by default until verified in-game: it hooks the game's network receive handler while a big lobby runs.
        Enabled = ModConfig.File.Bind("Big Lobby Names", "ShareOverflowNames", false,
            "Share the names of players 8+ (who don't fit in the game's name table) between players running this mod. " +
            "Vanilla players are never sent anything. Experimental.");
    }

    // ---------------------------------------------------------------- patches

    /// <summary>Patched by hand, each in its own try/catch, so one failing patch can't take the others down.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        var h = _harmony = new Harmony(Plugin.Guid + ".overflownames");
        // The receive hook (OverlandNetworkRunner.OnReliableDataReceived, which every join message passes through) is
        // NOT installed here: UpdateReceiveHook patches it only while a big lobby runs with the option on, and removes
        // it again on leaving or after its first error. Without it nothing is sent, so the game's handler (which
        // throws for unknown keys) never sees our messages from this machine.
        TryPatch(h, typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.AddPlayer), postfix: nameof(AddPlayerPostfix));
        TryPatch(h, typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.RemovePlayer), postfix: nameof(RemovePlayerPostfix));
        TryPatch(h, typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.GetPlayerName), postfix: nameof(GetPlayerNamePostfix));
    }

    private static bool TryPatch(Harmony h, Type type, string method, string prefix = null, string postfix = null)
    {
        try
        {
            var target = AccessTools.Method(type, method);
            if (target == null) { Plugin.Logger.LogWarning($"[Names] {type.Name}.{method} not found; not patched"); return false; }
            var pre = prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(OverflowNames), prefix));
            var post = postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(OverflowNames), postfix));
            h.Patch(target, prefix: pre, postfix: post);
            return true;
        }
        catch (Exception e)
        {
            _lastError = $"patch {method} failed";
            Plugin.Logger.LogWarning($"[Names] Could not patch {type.Name}.{method}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Installs the receive hook only while it is needed (option on, a running multiplayer session bigger than the stock
    /// 4 players) and removes it otherwise. After an error it stays off until the game restarts.
    /// </summary>
    private static void UpdateReceiveHook(NetworkRunner r)
    {
        bool want = false;
        if (!_receiveFaulted && Enabled != null && Enabled.Value && r != null)
        {
            try
            {
                if (r.IsRunning && !r.IsSinglePlayer)
                {
                    var info = r.SessionInfo;
                    want = info != null && info.IsValid && info.MaxPlayers > StockMaxPlayers;
                }
            }
            catch { want = false; }
        }
        if (want == _canReceive) return;
        if (want) _canReceive = PatchReceive();
        else UnpatchReceive();
    }

    private static bool PatchReceive()
    {
        try
        {
            _receiveTarget ??= AccessTools.Method(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.OnReliableDataReceived));
            if (_receiveTarget == null || _harmony == null)
            {
                _receiveFaulted = true;
                Plugin.Logger.LogWarning("[Names] OverlandNetworkRunner.OnReliableDataReceived not found; names are not shared");
                return false;
            }
            _harmony.Patch(_receiveTarget, prefix: new HarmonyMethod(AccessTools.Method(typeof(OverflowNames), nameof(ReceivePrefix))));
            Plugin.Logger.LogInfo("[Names] Big lobby: receive hook on");
            return true;
        }
        catch (Exception e)
        {
            _receiveFaulted = true;
            _lastError = "receive hook failed";
            Plugin.Logger.LogWarning("[Names] Could not hook OnReliableDataReceived: " + e.Message);
            return false;
        }
    }

    private static void UnpatchReceive()
    {
        _canReceive = false;
        if (_receiveTarget == null || _harmony == null) return;
        try
        {
            _harmony.Unpatch(_receiveTarget, AccessTools.Method(typeof(OverflowNames), nameof(ReceivePrefix)));
            Plugin.Logger.LogInfo("[Names] Receive hook off");
        }
        catch (Exception e)
        {
            _receiveFaulted = true;
            Plugin.Logger.LogWarning("[Names] Could not remove the receive hook: " + e.Message);
        }
    }

    /// <summary>Takes our messages out before the game's handler (which throws for unknown keys).</summary>
    private static bool ReceivePrefix(NetworkRunner runner, PlayerRef player, ReliableKey key, Il2CppSystem.ReadOnlySpan<byte> data)
    {
        if (_receiveFaulted) return true; // removed on the next tick; meanwhile the game handles everything itself
        int k0, k1, k2;
        try { key.GetInts(out k0, out k1, out k2, out _); }
        catch (Exception e)
        {
            _receiveFaulted = true; // Tick removes the hook
            LogOnce("key", "[Names] Could not read a reliable key, turning the receive hook off: " + e.Message);
            return true;
        }
        if (k0 != Magic) return true; // the game's own messages
        try
        {
            if (k2 != Protocol) { LogOnce("proto" + k2, $"[Names] Ignoring message with protocol {k2} (this mod speaks {Protocol})"); return false; }
            if (!Enabled.Value) return false;
            byte[] bytes = ReadSpan(data);
            Handle(runner, player, k1, bytes);
        }
        catch (Exception e)
        {
            _receiveFaulted = true; // Tick removes the hook
            LogOnce("recv", "[Names] Receive failed, turning the receive hook off: " + e);
        }
        return false; // never let the game see it: its handler throws for unknown keys
    }

    private static void AddPlayerPostfix(OverlandSessionPlayers __instance, PlayerRef playerRef, string runtimePlayerPlayerName)
    {
        try
        {
            try { SyncSession(__instance?.Runner); } catch { }
            if (!playerRef.IsRealPlayer) return;
            int id = playerRef.PlayerId;
            string name = Clean(runtimePlayerPlayerName);
            LeftAt.Remove(id);
            if (HostNames.TryGetValue(id, out var old) && old == name) return;
            HostNames[id] = name;
            _tableVersion++;
            _nextTick = 0f; // push the new name out on the next frame
        }
        catch (Exception e) { LogOnce("add", "[Names] AddPlayer hook: " + e.Message); }
    }

    private static void RemovePlayerPostfix(PlayerRef playerRef)
    {
        try
        {
            ModdedClients.Remove(playerRef.RawEncoded);
            SentVersion.Remove(playerRef.RawEncoded);
            if (HostNames.ContainsKey(playerRef.PlayerId)) LeftAt[playerRef.PlayerId] = Time.unscaledTime;
        }
        catch (Exception e) { LogOnce("remove", "[Names] RemovePlayer hook: " + e.Message); }
    }

    /// <summary>Fills in a name only when the game's own table has none (runs before the lobby guards' "Player N").</summary>
    private static void GetPlayerNamePostfix(PlayerRef playerRef, ref string __result)
    {
        if (!string.IsNullOrEmpty(__result) || Enabled == null || !Enabled.Value) return;
        try
        {
            int id = playerRef.PlayerId;
            if (RemoteNames.TryGetValue(id, out var n) && n.Length > 0) { __result = n; return; }
            if (HostNames.TryGetValue(id, out n) && n.Length > 0) __result = n;
        }
        catch { }
    }

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 0.5f;
        try
        {
            var r = GameState.Runner;
            if (_receiveFaulted && _canReceive) UnpatchReceive();
            if (r != null) { SyncSession(r); _runnerGoneSince = -1f; UpdateReceiveHook(r); }
            else if (_runnerPtr != IntPtr.Zero || _canReceive)
            {
                // GameState refreshes once a second, so only forget the session once it has really been gone a while.
                if (_runnerGoneSince < 0f) _runnerGoneSince = Time.unscaledTime;
                else if (Time.unscaledTime - _runnerGoneSince > 3f)
                {
                    ResetSession();
                    _runnerPtr = IntPtr.Zero;
                    _runnerGoneSince = -1f;
                    UpdateReceiveHook(null); // left the session: take the hook off the game's receive handler
                }
            }
            if (r == null || !_canReceive || !Enabled.Value || !r.IsRunning || r.IsSinglePlayer) return;
            if (r.IsServer) HostTick(r);
            else if (r.IsClient) ClientTick(r);
        }
        catch (Exception e) { LogOnce("tick", "[Names] Tick: " + e.Message); }
    }

    /// <summary>Starts a fresh table when a different session (runner) shows up.</summary>
    private static void SyncSession(NetworkRunner r)
    {
        if (r == null) return;
        IntPtr ptr = r.Pointer;
        if (ptr == IntPtr.Zero || ptr == _runnerPtr) return;
        if (_runnerPtr != IntPtr.Zero) ResetSession();
        _runnerPtr = ptr;
    }

    private static void ResetSession()
    {
        HostNames.Clear();
        LeftAt.Clear();
        ModdedClients.Clear();
        SentVersion.Clear();
        RemoteNames.Clear();
        _tableVersion++;
        _gotTable = false;
        _helloCount = 0;
        _nextHello = 0f;
        Recent.Clear();
    }

    private static void HostTick(NetworkRunner r)
    {
        // Forget departed players after a while.
        if (LeftAt.Count > 0)
        {
            var expired = new List<int>();
            foreach (var kv in LeftAt) if (Time.unscaledTime - kv.Value > 30f) expired.Add(kv.Key);
            foreach (int id in expired) { LeftAt.Remove(id); HostNames.Remove(id); }
            if (expired.Count > 0) _tableVersion++;
        }

        if (ModdedClients.Count == 0) return;
        var gone = new List<int>();
        foreach (int raw in ModdedClients)
        {
            var p = FromRaw(raw);
            bool valid;
            try { valid = r.IsPlayerValid(p); } catch { valid = true; }
            if (!valid) { gone.Add(raw); continue; }
            if (SentVersion.TryGetValue(raw, out int v) && v == _tableVersion) continue;
            if (SendTable(r, p)) SentVersion[raw] = _tableVersion;
        }
        foreach (int raw in gone) { ModdedClients.Remove(raw); SentVersion.Remove(raw); }
        PruneRecent();
    }

    private static void ClientTick(NetworkRunner r)
    {
        PruneRecent();
        if (_gotTable || _helloCount >= 6 || Time.unscaledTime < _nextHello) return;
        if (!r.LocalPlayer.IsRealPlayer) return;
        // Only big lobbies: the host of a lobby above the stock size runs a lobby mod, so a vanilla host never
        // receives a message its handler would throw on.
        var info = r.SessionInfo;
        if (info == null || !info.IsValid || info.MaxPlayers <= StockMaxPlayers) return;
        _nextHello = Time.unscaledTime + 5f;
        _helloCount++;
        if (Send(r, PlayerRef.None, toServer: true, MsgHello, Encoding.UTF8.GetBytes(Plugin.Version)))
            Plugin.Logger.LogInfo($"[Names] Asked the host for big-lobby names (try {_helloCount})");
    }

    // ---------------------------------------------------------------- messages

    private static void Handle(NetworkRunner runner, PlayerRef from, int msg, byte[] bytes)
    {
        if (runner == null) runner = GameState.Runner;
        if (runner == null) return;
        SyncSession(runner);
        if (runner.IsServer)
        {
            if (msg != MsgHello || !from.IsRealPlayer) return;
            string version = bytes == null ? "?" : Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 32));
            if (ModdedClients.Add(from.RawEncoded))
                Plugin.Logger.LogInfo($"[Names] Player {from.PlayerId} runs Over The Hill Plus {version}; sharing big-lobby names with them");
            SentVersion.Remove(from.RawEncoded);
            if (SendTable(runner, from)) SentVersion[from.RawEncoded] = _tableVersion;
        }
        else
        {
            if (msg != MsgTable || bytes == null) return;
            ParseTable(bytes);
            if (!_gotTable) Plugin.Logger.LogInfo($"[Names] Host shares big-lobby names ({RemoteNames.Count} known)");
            _gotTable = true;
            try { OverlandSessionPlayers.InvokePlayerDataUpdated(); } catch { } // let name labels refresh
        }
    }

    private static bool SendTable(NetworkRunner r, PlayerRef to)
    {
        var sb = new StringBuilder(HostNames.Count * 24);
        int n = 0;
        foreach (var kv in HostNames)
        {
            if (n++ >= MaxEntries) break;
            if (sb.Length > 0) sb.Append(EntrySep);
            sb.Append(kv.Key).Append(FieldSep).Append(kv.Value);
        }
        return Send(r, to, toServer: false, MsgTable, Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void ParseTable(byte[] bytes)
    {
        RemoteNames.Clear();
        string text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxPayload));
        int count = 0;
        foreach (var entry in text.Split(EntrySep))
        {
            if (count >= MaxEntries) break;
            int cut = entry.IndexOf(FieldSep);
            if (cut <= 0 || !int.TryParse(entry.Substring(0, cut), out int id) || id < 0) continue;
            string name = Clean(entry.Substring(cut + 1));
            if (name.Length == 0) continue;
            RemoteNames[id] = name;
            count++;
        }
    }

    private static bool Send(NetworkRunner r, PlayerRef to, bool toServer, int msg, byte[] payload)
    {
        try
        {
            if (payload.Length > MaxPayload) { LogOnce("big", "[Names] Name table too big; not sent"); return false; }
            var key = ReliableKey.FromInts(Magic, msg, Protocol, 0);
            var arr = new Il2CppStructArray<byte>(payload);
            var span = MakeSpan(arr);
            if (toServer) r.SendReliableDataToServer(key, span);
            else r.SendReliableDataToPlayer(to, key, span);
            Recent.Enqueue((Time.unscaledTime, arr, span));
            return true;
        }
        catch (Exception e)
        {
            _lastError = "send failed";
            LogOnce("send" + msg, $"[Names] Sending message {msg} failed: {e.Message}");
            return false;
        }
    }

    private static void PruneRecent()
    {
        while (Recent.Count > 0 && (Time.unscaledTime - Recent.Peek().time > 10f || Recent.Count > 64)) Recent.Dequeue();
    }

    // ---------------------------------------------------------------- span helpers

    private static Il2CppSystem.ReadOnlySpan<byte> MakeSpan(Il2CppStructArray<byte> arr)
    {
        try { return new Il2CppSystem.ReadOnlySpan<byte>(arr); }
        catch (Exception e)
        {
            // Fallback: build the 16-byte span value by hand ({data pointer, length}, as the game's SendRoomCode does:
            // the data of an IL2CPP array starts 32 bytes into the object, its length at 24).
            LogOnce("spanctor", "[Names] ReadOnlySpan constructor unavailable, building it by hand: " + e.Message);
            IntPtr cls = Il2CppClassPointerStore<Il2CppSystem.ReadOnlySpan<byte>>.NativeClassPtr;
            IntPtr boxed = IL2CPP.il2cpp_object_new(cls);
            IntPtr value = IL2CPP.il2cpp_object_unbox(boxed);
            Marshal.WriteIntPtr(value, arr.Pointer + 32);
            Marshal.WriteInt32(value + IntPtr.Size, arr.Length);
            return new Il2CppSystem.ReadOnlySpan<byte>(boxed);
        }
    }

    /// <summary>Copies the bytes out of a span ({data pointer, int length}) without calling generic IL2CPP code.</summary>
    private static byte[] ReadSpan(Il2CppSystem.ReadOnlySpan<byte> span)
    {
        if (span == null || span.Pointer == IntPtr.Zero) return null;
        IntPtr value = IL2CPP.il2cpp_object_unbox(span.Pointer);
        if (value == IntPtr.Zero) return null;
        IntPtr data = Marshal.ReadIntPtr(value);
        int length = Marshal.ReadInt32(value + IntPtr.Size);
        if (length < 0 || length > MaxPayload) { LogOnce("len", $"[Names] Ignoring message of {length} bytes"); return null; }
        var bytes = new byte[length];
        if (length > 0)
        {
            // An access violation here could not be caught, so refuse anything that is not a plausible user-mode
            // address range (null page, kernel half, or a range that wraps).
            ulong start = (ulong)data.ToInt64();
            const ulong MinAddress = 0x10000, MaxUserAddress = 0x00007FFFFFFFFFFF;
            if (start < MinAddress || start > MaxUserAddress - (ulong)length)
            {
                LogOnce("ptr", "[Names] Ignoring a message with an implausible data pointer");
                return null;
            }
            Marshal.Copy(data, bytes, 0, length);
        }
        return bytes;
    }

    // ---------------------------------------------------------------- misc

    private static PlayerRef FromRaw(int raw) => PlayerRef.FromEncoded(raw);

    /// <summary>Plain text, at most 32 characters (the game's name slot size); no control characters or rich-text tags.</summary>
    private static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, MaxNameLength));
        foreach (char c in s)
        {
            if (sb.Length >= MaxNameLength) break;
            if (char.IsControl(c) || c == '<' || c == '>') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static void LogOnce(string id, string message)
    {
        if (LoggedOnce.Add(id)) Plugin.Logger.LogWarning(message);
    }

    // ---------------------------------------------------------------- menu

    public static void DrawCard()
    {
        Menu.Card("Big lobby names", "The game has name slots for 7 players. Players running this mod share the names of everyone past that; vanilla players see \"Player N\".", () =>
        {
            Menu.Switch(Enabled, "Share overflow names (experimental)", "Host: send names to modded players. Guest: ask a big-lobby host for them. Only active in lobbies above 4 players.", true);
            var r = GameState.Runner;
            string role;
            try
            {
                if (r == null || !r.IsRunning) role = "not in a session";
                else if (r.IsSinglePlayer) role = "single-player (nothing to share)";
                else if (r.IsServer) role = $"hosting: {ModdedClients.Count} modded guest(s), {HostNames.Count} name(s) known";
                else if (_gotTable) role = $"guest: host shares names ({RemoteNames.Count} known)";
                else if (_helloCount > 0) role = _helloCount >= 6 ? "guest: host didn't answer (not this mod)" : "guest: asking the host...";
                else role = "guest: stock-size lobby, nothing needed";
            }
            catch { role = "?"; }
            Menu.KeyValue("Status", role);
            if (_receiveFaulted) Menu.KeyValue("Receive hook", "off after an error (restart the game to retry)");
            if (_lastError != null) Menu.KeyValue("Problem", _lastError);
        });
    }
}
