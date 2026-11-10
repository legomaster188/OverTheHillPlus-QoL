using System;
using System.Collections.Generic;
using System.Text;
#if MELON
using Il2CppFusion;
#else
using Fusion;
#endif
#if MELON
using Il2CppFunCore.Managers;
#else
using FunCore.Managers;
#endif
using HarmonyLib;
using OverTheHillPlus.Core.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OverTheHillPlus;

/// <summary>
/// Keeps the searches of the whole world (Object.FindObjectsOfType and friends) off the frames of someone driving. Every
/// mod that looked for the cars, the volumes or the water every second or two did so whether anything had changed or not,
/// and each search cost 20 ms or more: the stutter every few seconds. Now <see cref="Stamp"/> moves when the world
/// changes, a search asks <see cref="Due(ScanPacer, float)"/> first, and with nothing new there is no search at all:
/// <list type="bullet">
/// <item>the session, the active scene, the number of scenes and the game's SceneReferences are looked at once a second
/// (<see cref="Poll"/>);</item>
/// <item>a car coming or going says so (FusionPlayer.Spawned and Despawned: every car has one, the player's, a guest's or an
/// AI's);</item>
/// <item>so does going in and out of photo mode, which switches on volumes of its own;</item>
/// <item>a mod that changes the world in a way none of these see says so with <see cref="Changed"/>.</item>
/// </list>
/// Other network objects don't count: whatever the game's spawners make all the time must not keep every search awake, and
/// none of the searches looks for them. A hitch or a tow bar is told to the joint searches alone (<see cref="JointsChanged"/>).
/// At most one search runs in a frame: while a world loads, every mod wants one at once, and each could wait seconds for
/// the loader. Without the two car patches, or if they turn out not to run, every second counts as a change and the searches
/// run as they always did.
/// </summary>
internal static class WorldScan
{
    private static int _stamp, _carEvents;
    private static int _scanFrame = -1;
    private static bool _hooked;
    private static IntPtr _runner, _refs, _state;
    private static bool _photo;
    private static int _sceneCount = -1, _sceneHandle;
    private static readonly List<ScanPacer> Pacers = new();

    /// <summary>Moves whenever the world may hold something new.</summary>
    internal static int Stamp => _stamp;

    /// <summary>Something came or went that a search should see.</summary>
    internal static void Changed() => _stamp++;

    /// <summary>A pacer for a search of <paramref name="name"/>, listed in the report.</summary>
    internal static ScanPacer Pace(string name, float everySeconds, float settleSeconds = 0f)
    {
        var p = new ScanPacer(name, everySeconds, settleSeconds);
        lock (Pacers) Pacers.Add(p);
        return p;
    }

    private static readonly List<ScanPacer> JointPacers = new();

    /// <summary>A pacer for a search of the world's joints: told by <see cref="JointsChanged"/>, not only by the stamp.</summary>
    internal static ScanPacer JointPace(string name, float everySeconds)
    {
        var p = Pace(name, everySeconds, 10f);
        lock (JointPacers) JointPacers.Add(p);
        return p;
    }

    /// <summary>A joint was made or dropped (a trailer hitched, a tow bar): only the searches for joints look again.</summary>
    internal static void JointsChanged()
    {
        lock (JointPacers) foreach (var p in JointPacers) p.Again();
    }

    /// <summary>The search may run now: it is due, and no other search ran this frame. Main thread.</summary>
    internal static bool Due(ScanPacer pace, float now)
    {
        if (pace == null || !pace.Due(_stamp, now) || !Turn()) return false;
        pace.Ran(now);
        return true;
    }

    /// <summary>Takes this frame's one search, for a search with a clock of its own. False when another one had it: nothing
    /// pays for two in a frame.</summary>
    internal static bool Turn()
    {
        int frame = Time.frameCount;
        if (frame == _scanFrame) return false;
        _scanFrame = frame;
        return true;
    }

    /// <summary>Once a second, from <see cref="GameState.Tick"/>: what can be seen without a search.</summary>
    internal static void Poll(NetworkRunner runner)
    {
        IntPtr r = IntPtr.Zero, refs = IntPtr.Zero;
        int count = _sceneCount, handle = _sceneHandle;
        try
        {
            r = runner != null ? runner.Pointer : IntPtr.Zero;
            var sr = SceneReferences.Instance;
            refs = sr != null ? sr.Pointer : IntPtr.Zero;
            count = SceneManager.sceneCount;
            handle = SceneManager.GetActiveScene().handle;
        }
        catch (Exception e) { DebugLog.Error("core", e, "WorldScan.Poll"); }
        if (r != _runner || refs != _refs || count != _sceneCount || handle != _sceneHandle)
        {
            _runner = r;
            _refs = refs;
            _sceneCount = count;
            _sceneHandle = handle;
            _stamp++;
        }
        else if (!_hooked) _stamp++;
        CheckPhotoMode();
    }

    private static void CheckPhotoMode()
    {
        try
        {
            var top = HybridSingleton<GameStateManager>.Instance?.PeekState();
            IntPtr state = top != null ? top.Pointer : IntPtr.Zero;
            if (state == _state) return;
            _state = state;
            bool photo = top != null && top.TryCast<StatePhotoModeMenu>() != null;
            if (photo == _photo) return;
            _photo = photo;
            _stamp++;
        }
        catch (Exception e) { DebugLog.Error("core", e, "WorldScan.CheckPhotoMode"); }
    }

    /// <summary>The cars coming and going (core's Load). A failure only means more searches.</summary>
    internal static void InstallPatches(Harmony h)
    {
        try
        {
            var spawned = AccessTools.Method(typeof(FusionPlayer), nameof(FusionPlayer.Spawned));
            var despawned = AccessTools.Method(typeof(FusionPlayer), nameof(FusionPlayer.Despawned));
            if (spawned == null || despawned == null) throw new MissingMethodException("FusionPlayer.Spawned or Despawned was not found");
            var mark = new HarmonyMethod(AccessTools.Method(typeof(WorldScan), nameof(CarCameOrWent)));
            h.Patch(spawned, postfix: mark);
            h.Patch(despawned, postfix: mark);
            _hooked = true;
        }
        catch (Exception e)
        {
            Core.CorePlugin.Logger?.LogWarning("[WorldScan] the car spawn hooks are not on, so the world is searched every second as before: " + e.GetBaseException().Message);
        }
    }

    private static void CarCameOrWent()
    {
        _carEvents++;
        _stamp++;
    }

    /// <summary>This player's car is in the world, so its Spawned has run: if the hooks never saw one they don't work in this
    /// build, and the searches go back to their old pace.</summary>
    internal static void SawLocalCar()
    {
        if (!_hooked || _carEvents > 0) return;
        _hooked = false;
        Core.CorePlugin.Logger?.LogWarning("[WorldScan] a car is in the world but its spawn hook never ran: the world is searched every second as before.");
    }

    /// <summary>What was searched and how often: one line, with every kind of search that ran.</summary>
    internal static string Report()
    {
        var sb = new StringBuilder();
        sb.Append("world changes ").Append(_stamp).Append(_hooked ? "" : " (no car hooks)");
        lock (Pacers)
            foreach (var p in Pacers)
                if (p.Runs > 0) sb.Append("; ").Append(p.Name).Append(' ').Append(p.Runs);
        return sb.ToString();
    }
}
