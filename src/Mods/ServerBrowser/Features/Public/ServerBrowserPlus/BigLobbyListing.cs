using System;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using OverTheHillPlus.ServerBrowser;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

/// <summary>
/// The game's server list hides every server with more than 4 player slots: OverlandSessionInfo's constructor sets a
/// private flag to (SessionInfo.MaxPlayers &lt;= 4) and SessionInfoListAdapter.ApplyFilters skips every entry without it
/// (decoded with Cpp2IL; join by code never looks at it). So big lobbies, ours (Big Lobbies) and other hosts', never
/// show in the browser. This sets the flag for them again, so they are listed like any other server (the game's own
/// hide-full / hide-private / area / search filters still apply). Only this game's list changes; players without the
/// mod still can't see servers above 4 slots.
/// </summary>
internal static class BigLobbyListing
{
    /// <summary>Byte offset of the flag in the object (found by its position in the class at start; 0x38 in this build).</summary>
    private const int ExpectedOffset = 0x38;

    internal static ConfigEntry<bool> ShowBigLobbies;
    private static int _offset = -1;
    private static bool _checked, _logged;
    internal static int Unhidden { get; private set; }

    internal static void Bind(ConfigFile cfg)
    {
        ShowBigLobbies = cfg.Bind("ServerBrowserPlus", "ShowBigLobbies", true,
            "List servers with more than 4 player slots (the game hides them from its server list; join by code still works without this).");
    }

    /// <summary>The flag's offset, checked once: a one-byte field at the expected place in OverlandSessionInfo.</summary>
    private static int FlagOffset()
    {
        if (_checked) return _offset;
        _checked = true;
        try
        {
            IntPtr klass = Il2CppClassPointerStore<OverlandSessionInfo>.NativeClassPtr;
            if (klass == IntPtr.Zero) { Fail("the game's session class was not found"); return -1; }
            IntPtr iter = IntPtr.Zero, field;
            while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != IntPtr.Zero)
            {
                if ((int)IL2CPP.il2cpp_field_get_offset(field) != ExpectedOffset) continue;
                IntPtr type = IL2CPP.il2cpp_field_get_type(field);
                string typeName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(type)) ?? "";
                string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field)) ?? "?";
                if (typeName != "System.Boolean") { Fail($"the field at 0x{ExpectedOffset:X} is {typeName} {name}, not a flag (game update?)"); return -1; }
                _offset = ExpectedOffset;
                ServerBrowserPlugin.Logger.LogInfo($"[Browser+] big lobbies: the game's 4-slot list flag is {name} (0x{ExpectedOffset:X}); servers above 4 slots will be listed");
                return _offset;
            }
            Fail($"no field at 0x{ExpectedOffset:X} (game update?)");
        }
        catch (Exception e) { Fail(e.Message); }
        return -1;
    }

    private static void Fail(string why)
    {
        if (_logged) return;
        _logged = true;
        ServerBrowserPlugin.Logger.LogWarning("[Browser+] big lobbies stay hidden from the server list: " + why);
    }

    private static int _lastLoggedCount = -1;

    /// <summary>Marks every big lobby in the runner's current session list as listable.</summary>
    internal static void FixRunnerList()
    {
        try
        {
            var list = OverlandNetworkRunner.Instance?.SessionList;
            if (list == null) return;
            int before = Unhidden;
            for (int i = 0; i < list.Count; i++)
            {
                var info = list[i];
                if (info == null) continue;
                AfterCreated(info, info.FusionSessionInfo);
            }
            int now = Unhidden - before;
            if (now > 0 && now != _lastLoggedCount)
            {
                _lastLoggedCount = now;
                ServerBrowserPlugin.Logger.LogInfo($"[Browser+] big lobbies: {now} server(s) above 4 slots made listable this refresh");
            }
        }
        catch (Exception e) { DebugLog.Error(ServerBrowserPlugin.ModId, e, "BigLobbyListing.FixRunnerList"); }
    }

    /// <summary>Lists one big lobby the game marked as hidden.</summary>
    internal static void AfterCreated(OverlandSessionInfo info, Fusion.SessionInfo sessionInfo)
    {
        if (ShowBigLobbies == null || !ShowBigLobbies.Value || info == null || sessionInfo == null) return;
        int max;
        try { max = sessionInfo.MaxPlayers; } catch { return; }
        if (max <= 4) return;
        int off = FlagOffset();
        if (off < 0) return;
        try
        {
            if (!OverlandSessionInfo.IsSessionValid(sessionInfo)) return; // missing properties / other version: stays hidden
            if (Marshal.ReadByte(info.Pointer + off) != 0) return;
            Marshal.WriteByte(info.Pointer + off, 1);
            Unhidden++;
        }
        catch (Exception e) { DebugLog.Error(ServerBrowserPlugin.ModId, e, "BigLobbyListing.AfterCreated"); }
    }
}

/// <summary>Right before the list view filters (SessionInfoListAdapter.UpdateSessionList runs ApplyFilters): marks the big
/// lobbies in the runner's session list (OverlandNetworkRunner.SessionList, built in OnSessionListUpdated) as listable.
/// (A postfix on OverlandSessionInfo's constructor never ran in game, so the list is fixed up here instead.)</summary>
[HarmonyPatch(typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.UpdateSessionList))]
internal static class BigLobbyListingPatch
{
    private static void Prefix() => BigLobbyListing.FixRunnerList();
}
