using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;

namespace OverTheHillPlus.Api;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The public modding API of the OTH Mod Manager, for mods by other creators: see docs/modding-api.md. A stable
/// surface over the Mod Manager's internals (which may change between versions); the Mod Manager's own mods use the
/// internals directly.
///
/// <para>Registering your mod lists it on the MODS page (INSTALLED tab, with an OTHER CREATOR badge), with its own
/// page for your settings and an ON/OFF switch. Call these from your plugin's Load, on the main thread.</para>
/// </summary>
public static class Mods
{
    /// <summary>
    /// Lists your mod on the MODS page. Call it first in your plugin's Load, before <see cref="IsEnabled"/>, so the
    /// mod stays listed (and can be switched back on) while it is off. The id is 2 to 40 characters from a-z, 0-9,
    /// '_', '.' and '-' (it names your config file, oth.&lt;id&gt;.cfg); "core" and the ids of the Mod Manager's own
    /// mods are taken (also those not installed). Returns false (and logs why) when the id is refused; never throws.
    /// The Mod Manager keeps its own copy of <paramref name="info"/>: changing your object afterwards changes nothing,
    /// and <see cref="ModInfo.Private"/> is always false for another creator's mod.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Register(ModInfo info)
    {
        var caller = Assembly.GetCallingAssembly();
        if (info == null)
        {
            Log("Mods.Register: no ModInfo given");
            return false;
        }
        // A copy: the caller's object could change its id or Private after the checks.
        var copy = ApiRules.CopyForRegister(info);
        string problem = ApiGuard.Problem(copy.Id);
        if (problem != null)
        {
            Log("Mods.Register: " + problem);
            return false;
        }
        string file = null;
        try { file = caller.Location; } catch (Exception ex) { DebugLog.Error("core", ex, "Mods.Register"); /* in-memory assembly: no file */ }
        if (ThirdParty.ApiIdTakenByOther(copy.Id, file))
        {
            Log($"Mods.Register: \"{copy.Id}\" is already the id of another mod");
            return false;
        }
        ThirdParty.NoteApiMod(copy.Id, file);
        ModManager.Register(copy, caller);
        return true;
    }

    /// <summary>
    /// True unless the player switched your mod off on the MODS page (or the Mod Manager can't run mods this
    /// session). Read it once in Load: when it is false, return without patching or hooking anything. A change of
    /// the switch takes effect after a restart. False for an id the API refuses (invalid, "core" or one of ours).
    /// </summary>
    public static bool IsEnabled(string id) => ApiRules.IsEnabled(id, ThirdPartyScan.IsOurModId, ModManager.IsEnabled);

    /// <summary>Your mod's own BepInEx config file, BepInEx/config/oth.&lt;id&gt;.cfg (created on first use). It is
    /// saved for you a moment after a setting changes. Throws ArgumentException for an invalid id or one of ours.</summary>
    public static ConfigFile ConfigFor(string id)
    {
        ApiGuard.Require(id, "Mods.ConfigFor");
        return ModManager.ConfigFor(id);
    }

    /// <summary>
    /// Marks your mod as failed to start: its MODS page shows "Failed to start: " and the first line of
    /// <paramref name="error"/>, and the other mods keep running. Call it from the catch around your Load. Never
    /// throws: an invalid id, or one of ours, is logged and ignored.
    /// </summary>
    public static void MarkFailed(string id, string error)
    {
        string problem = ApiGuard.Problem(id);
        if (problem != null)
        {
            Log("Mods.MarkFailed: " + problem);
            return;
        }
        ModManager.MarkFailed(id, error);
    }

    /// <summary>
    /// Applies every Harmony patch class in <paramref name="assembly"/> one by one: a patch whose target moved after
    /// a game update is skipped (and logged) instead of failing the rest, and your MODS page says how many features
    /// are unavailable. Returns the number of patch classes that failed. Throws ArgumentException for an invalid id or
    /// one of ours, ArgumentNullException without a Harmony or an assembly.
    /// </summary>
    public static int PatchAll(string id, Harmony harmony, Assembly assembly)
    {
        ApiGuard.Require(id, "Mods.PatchAll");
        if (harmony == null || assembly == null) throw new ArgumentNullException(harmony == null ? nameof(harmony) : nameof(assembly));
        return PatchReport.PatchAllSafely(harmony, assembly, id);
    }

    internal static void Log(string message) => ApiGuard.Log(message);
}

/// <summary>The id check every call of the API that names a mod goes through, and the API's log.</summary>
internal static class ApiGuard
{
    static ApiGuard() => ApiRules.Warn = Log;

    internal static void Log(string message) => Core.CorePlugin.Logger?.LogWarning("[Api] " + message);

    /// <summary>Why another creator's mod can't use this id (invalid, "core", or one of ours), or null.</summary>
    internal static string Problem(string id) => ApiRules.IdProblem(id, ThirdPartyScan.IsOurModId);

    /// <summary>Throws ArgumentException when <see cref="Problem"/> refuses the id.</summary>
    internal static void Require(string id, string call)
    {
        string problem = Problem(id);
        if (problem != null) throw new ArgumentException($"{call}: {problem}", "modId");
    }
}
