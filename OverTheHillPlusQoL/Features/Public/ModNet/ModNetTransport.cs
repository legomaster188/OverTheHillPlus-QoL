using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Fusion;
using Fusion.Sockets;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ModNet;

/// <summary>
/// The wire under <see cref="ModNet"/>: Fusion reliable data (NetworkRunner.SendReliableDataToServer / ToPlayer), the
/// same path the game uses for its room code. The game's receiver (OverlandNetworkRunner.OnReliableDataReceived,
/// decoded with Cpp2IL) switches on the first int of the ReliableKey (1 = RuntimePlayer, 2 = room code) and throws
/// for anything else, so a Harmony prefix takes every message whose first word is <see cref="Magic"/> before the
/// game sees it, and leaves every other message alone.
///
/// ReliableKey layout (four ints):
///   [0] Magic "OTHP"
///   [1] channel (bits 0-15) | message type (bits 16-23) | message version (bits 24-31)
///   [2] wire protocol (bits 0-7) | flags (bits 8-15): 1 = relay request (client asks the host to forward),
///       2 = relayed (forwarded by the host)
///   [3] relay request: target player (RawEncoded, 0 = every modded player); relayed: the original sender
/// Wire protocol 1 was the first big-lobby names exchange; peers on it swallow protocol 2 messages without
/// handling them (and this version does the same with theirs), so mixed versions stay quiet rather than break.
///
/// The prefix is installed only while a session can actually carry mod messages (see <see cref="ModNet"/>) and is
/// removed when leaving. If reading a key ever fails it is removed for the rest of the game run; if handling fails
/// it keeps swallowing our messages but stops handling them, so the game's receiver never sees one.
/// </summary>
internal static class ModNetTransport
{
    internal const int Magic = 0x4F544850; // "OTHP"
    internal const int FlagRelayRequest = 1, FlagRelayed = 2;

    private static Harmony _harmony;
    private static System.Reflection.MethodInfo _target;
    private static bool _installed;

    /// <summary>The prefix is patched in.</summary>
    internal static bool Installed => _installed;
    /// <summary>Key reading failed once: the hook is off until the game restarts.</summary>
    internal static bool Faulted { get; private set; }
    /// <summary>Handling failed once: our messages are still swallowed, but no longer handled.</summary>
    internal static bool SwallowOnly { get; private set; }
    internal static string LastError { get; private set; }

    // Keep sent buffers referenced for a moment in case Fusion reads them after the call returns.
    private static readonly Queue<(float time, Il2CppStructArray<byte> arr, Il2CppSystem.ReadOnlySpan<byte> span)> Recent = new();

    // ---------------------------------------------------------------- hook

    internal static void SetInstalled(bool want)
    {
        if (Faulted) want = false;
        if (want == _installed) return;
        if (want) Patch();
        else Unpatch();
    }

    private static void Patch()
    {
        try
        {
            _harmony ??= new Harmony(Plugin.Guid + ".modnet");
            _target ??= AccessTools.Method(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.OnReliableDataReceived));
            if (_target == null)
            {
                Fault("OverlandNetworkRunner.OnReliableDataReceived not found; the mod network is off");
                return;
            }
            _harmony.Patch(_target, prefix: new HarmonyMethod(AccessTools.Method(typeof(ModNetTransport), nameof(ReceivePrefix))));
            _installed = true;
            Plugin.Logger.LogInfo("[ModNet] Receive hook on");
        }
        catch (Exception e) { Fault("could not hook OnReliableDataReceived: " + e.Message); }
    }

    private static void Unpatch()
    {
        _installed = false;
        if (_target == null || _harmony == null) return;
        try
        {
            _harmony.Unpatch(_target, AccessTools.Method(typeof(ModNetTransport), nameof(ReceivePrefix)));
            Plugin.Logger.LogInfo("[ModNet] Receive hook off");
        }
        catch (Exception e)
        {
            Faulted = true;
            LastError = "could not remove the receive hook";
            Plugin.Logger.LogWarning("[ModNet] Could not remove the receive hook: " + e.Message);
        }
    }

    private static void Fault(string why)
    {
        Faulted = true;
        LastError = why;
        Plugin.Logger.LogWarning("[ModNet] " + why);
    }

    /// <summary>Takes our messages out before the game's handler (which throws for unknown keys).</summary>
    private static bool ReceivePrefix(NetworkRunner runner, PlayerRef player, ReliableKey key, Il2CppSystem.ReadOnlySpan<byte> data)
    {
        if (Faulted) return true; // removed on the next tick; meanwhile the game handles everything itself
        int k0, k1, k2, k3;
        try { key.GetInts(out k0, out k1, out k2, out k3); }
        catch (Exception e)
        {
            Faulted = true; // ModNet.Tick removes the hook
            LastError = "could not read a message key";
            ModNet.LogOnce("key", "[ModNet] Could not read a reliable key, turning the receive hook off: " + e.Message);
            return true;
        }
        if (k0 != Magic) return true; // the game's own messages
        if (SwallowOnly) return false;
        try
        {
            int proto = k2 & 0xFF;
            if (proto != ModNet.WireProtocol)
            {
                ModNet.LogOnce("proto" + proto, $"[ModNet] Ignoring a message on wire protocol {proto} (this version speaks {ModNet.WireProtocol}): the other player runs a different version of the mod");
                return false;
            }
            byte[] bytes = ReadSpan(data);
            if (bytes != null) ModNet.OnWire(runner, player, k1, k2, k3, bytes);
        }
        catch (Exception e)
        {
            SwallowOnly = true;
            LastError = "receive failed (messages are now ignored)";
            ModNet.LogOnce("recv", "[ModNet] Receive failed; mod messages are ignored from now on: " + e);
        }
        return false; // never let the game see it: its handler throws for unknown keys
    }

    // ---------------------------------------------------------------- send

    internal static bool Send(NetworkRunner r, PlayerRef to, bool toServer, int k1, int k2, int k3, byte[] payload)
    {
        try
        {
            var key = ReliableKey.FromInts(Magic, k1, k2, k3);
            var arr = new Il2CppStructArray<byte>(payload ?? Array.Empty<byte>());
            var span = MakeSpan(arr);
            if (toServer) r.SendReliableDataToServer(key, span);
            else r.SendReliableDataToPlayer(to, key, span);
            Recent.Enqueue((Time.unscaledTime, arr, span));
            return true;
        }
        catch (Exception e)
        {
            LastError = "send failed";
            ModNet.LogOnce("send" + (k1 & 0xFFFF), $"[ModNet] Sending on channel {k1 & 0xFFFF} failed: {e.Message}");
            return false;
        }
    }

    internal static void PruneRecent()
    {
        while (Recent.Count > 0 && (Time.unscaledTime - Recent.Peek().time > 10f || Recent.Count > 128)) Recent.Dequeue();
    }

    internal static void ClearRecent() => Recent.Clear();

    // ---------------------------------------------------------------- span helpers

    private static Il2CppSystem.ReadOnlySpan<byte> MakeSpan(Il2CppStructArray<byte> arr)
    {
        try { return new Il2CppSystem.ReadOnlySpan<byte>(arr); }
        catch (Exception e)
        {
            // Fallback: build the 16-byte span value by hand ({data pointer, length}, as the game's SendRoomCode does:
            // the data of an IL2CPP array starts 32 bytes into the object, its length at 24).
            ModNet.LogOnce("spanctor", "[ModNet] ReadOnlySpan constructor unavailable, building it by hand: " + e.Message);
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
        if (length < 0 || length > ModNet.HardMaxPayload)
        {
            ModNet.CountDrop(ModNet.DropReason.Size);
            ModNet.LogOnce("len", $"[ModNet] Ignoring a message of {length} bytes");
            return null;
        }
        var bytes = new byte[length];
        if (length > 0)
        {
            // An access violation here could not be caught, so refuse anything that is not a plausible user-mode
            // address range (null page, kernel half, or a range that wraps).
            ulong start = (ulong)data.ToInt64();
            const ulong MinAddress = 0x10000, MaxUserAddress = 0x00007FFFFFFFFFFF;
            if (start < MinAddress || start > MaxUserAddress - (ulong)length)
            {
                ModNet.LogOnce("ptr", "[ModNet] Ignoring a message with an implausible data pointer");
                return null;
            }
            Marshal.Copy(data, bytes, 0, length);
        }
        return bytes;
    }
}
