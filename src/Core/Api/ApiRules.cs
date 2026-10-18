using System;
using System.Collections.Generic;
using OverTheHillPlus.Core.Manager;

namespace OverTheHillPlus.Api;

/// <summary>
/// The checks behind the public API: mod ids, network channels and their limits. Pure logic with no BepInEx or Unity
/// types, so it can be unit tested.
/// </summary>
internal static partial class ApiRules
{
    /// <summary>Network channels other creators may use (ours are below <see cref="MinChannel"/>).</summary>
    public const int MinChannel = 1000, MaxChannel = 65000;

    /// <summary>Caps on a channel of another creator's mod (the mod network's own hard payload cap is the same).</summary>
    public const int MaxPayload = 8192, MaxBurst = 40;
    public const float MaxRatePerSecond = 20f, MinRatePerSecond = 0.01f;

    /// <summary>Channels one mod of another creator may register, and all of them together: the mod network
    /// advertises at most 64 channels to the other side, and ours always come first.</summary>
    public const int MaxChannelsPerMod = 8, MaxChannelsTotal = 32;

    /// <summary>Where the checks report a problem of their own (set to the log by the API); null: nowhere.</summary>
    internal static Action<string> Warn { get; set; }

    /// <summary>2 to 40 characters, each a-z, 0-9, '_', '.' or '-': the id names the mod's config file (oth.&lt;id&gt;.cfg).</summary>
    public static bool IsValidModId(string id)
    {
        if (id == null || id.Length < 2 || id.Length > 40) return false;
        foreach (char c in id)
            if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '.' || c == '-')) return false;
        return true;
    }

    /// <summary>True for a channel id in <see cref="MinChannel"/>..<see cref="MaxChannel"/>.</summary>
    public static bool IsThirdPartyChannel(int channel) => channel >= MinChannel && channel <= MaxChannel;

    // ---------------------------------------------------------------- our ids

    private static HashSet<string> _reserved;

    /// <summary>
    /// The ids of the Mod Manager and every mod of ours, installed or not (also the planned ones): other creators
    /// can't take them, so a later install of ours never shares a config file or switch with theirs.
    /// </summary>
    public static IReadOnlyCollection<string> ReservedIds => Reserved;

    private static HashSet<string> Reserved
    {
        get
        {
            if (_reserved != null) return _reserved;
            var ids = new List<string>
            {
                "core",
                // Released mods.
                "biglobbies", "chat", "drivingqol", "graphics", "maphud", "photo", "serverbrowser", "servercreator",
                "serverutils",
                // Round 5.
                "vera", "waypoints", "race", "tag", "ghosts", "stats", "camera", "radio", "horns", "rules", "discord",
                "minimap", "hudplus", "autoshot", "cargo", "liverypacks", "tercel", "trailers", "stacking", "aro", "hilo",
                "bastion",
            };
            AddPrivate(ids);
            return _reserved = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The private mods' ids (in a folder the public export leaves out).</summary>
    static partial void AddPrivate(List<string> ids);

    /// <summary>True when the id is "core" or one of <see cref="ReservedIds"/>.</summary>
    public static bool IsReserved(string id) => id != null && Reserved.Contains(id);

    /// <summary>
    /// Why another creator's mod can't use <paramref name="id"/> (register it, or name it in any other call), or null
    /// when it can: an invalid id, a reserved one, or one <paramref name="isOurs"/> says is ours (an installed mod of
    /// ours, or one in the release's list). A check that throws is reported to <see cref="Warn"/> and doesn't block.
    /// </summary>
    public static string IdProblem(string id, Func<string, bool> isOurs)
    {
        if (!IsValidModId(id)) return $"\"{id}\" is not a valid mod id: use 2 to 40 characters from a-z, 0-9, '_', '.' and '-'";
        if (id == "core") return "\"core\" is the Mod Manager's own id";
        if (IsReserved(id)) return $"\"{id}\" is the id of one of the Mod Manager's own mods";
        try
        {
            if (isOurs != null && isOurs(id)) return $"\"{id}\" is the id of one of the Mod Manager's own mods";
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "ApiRules.IdProblem");
            // Our own check broke: say so, and don't block another creator's mod over it.
            try { Warn?.Invoke($"could not check whether \"{id}\" is one of our mod ids ({e.Message}); allowed"); }
            catch (Exception ex) { DebugLog.Error("core", ex, "ApiRules.IdProblem"); /* the log itself failed */ }
        }
        return null;
    }

    /// <summary>Why Mods.Register refuses <paramref name="id"/>, or null: see <see cref="IdProblem"/>.</summary>
    public static string RegisterProblem(string id, Func<string, bool> isOurs) => IdProblem(id, isOurs);

    /// <summary>
    /// Mods.IsEnabled: false for an id <see cref="IdProblem"/> refuses (so another creator's mod never reads the state
    /// of the Mod Manager or of one of our mods), else what <paramref name="switchedOn"/> (the MODS page's switch) says.
    /// </summary>
    public static bool IsEnabled(string id, Func<string, bool> isOurs, Func<string, bool> switchedOn) =>
        IdProblem(id, isOurs) == null && switchedOn != null && switchedOn(id);

    // ---------------------------------------------------------------- registration

    /// <summary>
    /// The copy of a mod's ModInfo the Mod Manager keeps: the caller's object can change after registering (its id, or
    /// Private, which would hold back core's updates) without affecting the registered mod. Private is always false.
    /// </summary>
    public static ModInfo CopyForRegister(ModInfo info)
    {
        if (info == null) return null;
        return new ModInfo
        {
            Id = info.Id,
            Name = info.Name,
            Description = info.Description,
            Version = info.Version,
            Private = false,
            DrawSimple = info.DrawSimple,
            DrawAdvanced = info.DrawAdvanced,
            Side = info.Side,
        };
    }

    // ---------------------------------------------------------------- network

    /// <summary>A channel's payload, rate and burst within the caps above.</summary>
    public static (int payload, float rate, int burst) ClampLimits(int payload, float rate, int burst)
    {
        int p = Math.Clamp(payload, 0, MaxPayload);
        float r = float.IsNaN(rate) ? MinRatePerSecond : Math.Clamp(rate, MinRatePerSecond, MaxRatePerSecond);
        int b = Math.Clamp(burst, 1, MaxBurst);
        return (p, r, b);
    }

    /// <summary>Counts the channels other creators' mods registered, per mod and in total (<see cref="MaxChannelsPerMod"/>,
    /// <see cref="MaxChannelsTotal"/>).</summary>
    internal sealed class ChannelQuota
    {
        private readonly Dictionary<string, int> _perMod = new(StringComparer.OrdinalIgnoreCase);
        private int _total;

        /// <summary>Why <paramref name="modId"/> can't register another channel, or null when it can.</summary>
        public string Problem(string modId)
        {
            if (_total >= MaxChannelsTotal) return $"other creators' mods already use {MaxChannelsTotal} channels, the most there can be";
            _perMod.TryGetValue(modId ?? "", out int n);
            return n >= MaxChannelsPerMod ? $"a mod can register at most {MaxChannelsPerMod} channels" : null;
        }

        /// <summary>Counts a channel that was registered.</summary>
        public void Take(string modId)
        {
            _perMod.TryGetValue(modId ?? "", out int n);
            _perMod[modId ?? ""] = n + 1;
            _total++;
        }
    }
}
