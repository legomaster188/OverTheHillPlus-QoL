using System;
using System.Collections.Generic;
using System.Text;
#if MELON
using Il2CppFusion;
#else
using Fusion;
#endif
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
#if MELON
using FunCoreManagers = Il2CppFunCore.Managers;
#else
using FunCoreManagers = FunCore.Managers;
#endif

namespace OverTheHillPlus.Core.Shared;

/// <summary>What a mod's <see cref="NetPrefabEntry.Build"/> returns: the template, "ask again later", or "give up".</summary>
public sealed class NetPrefabBuild
{
    public NetworkObject Template { get; private set; }
    public string WaitingFor { get; private set; }
    public string Failed { get; private set; }

    /// <summary>The inactive template to register (its NetworkTypeId already set to the index it was built for).</summary>
    public static NetPrefabBuild Done(NetworkObject template) => new() { Template = template };
    /// <summary>Not possible yet (the game is still loading): asked again a second later.</summary>
    public static NetPrefabBuild Later(string waitingFor) => new() { WaitingFor = waitingFor ?? "the game" };
    /// <summary>Under way, with steps left (spread over frames so the game keeps drawing): asked again next frame, for
    /// the same index; nothing after it registers in between.</summary>
    public static NetPrefabBuild Working(string step) => new() { Step = step ?? "building" };
    /// <summary>The step a <see cref="Working"/> build is at.</summary>
    public string Step { get; private set; }
    /// <summary>Never: the entry is dropped (later entries still register, at the indices that follow).</summary>
    public static NetPrefabBuild Fail(string reason) => new() { Failed = reason ?? "failed" };
}

/// <summary>One runtime network prefab a mod adds to Fusion's prefab table.</summary>
public sealed class NetPrefabEntry
{
    /// <summary>A key from <see cref="NetPrefabPlan.Order"/> (its position there decides the prefab index).</summary>
    public string Key;
    /// <summary>NetworkObjectGuid, 32 hex digits, fixed forever.</summary>
    public string Guid;
    /// <summary>Fusion's path for the entry (never a real asset; its file name is the table's name for it).</summary>
    public string AssetPath;
    /// <summary>Everything else peers must agree on for the template to match (protocol, donor, ...): in the hash.</summary>
    public string Layout;
    /// <summary>Builds the template for the given prefab index (main thread). It may take several frames
    /// (<see cref="NetPrefabBuild.Working"/>); while <see cref="NetPrefabRegistry.Urgent"/> is set (a session is starting)
    /// it must finish in that call.</summary>
    public Func<int, NetPrefabBuild> Build;
    /// <summary>The entry is in the table at this index.</summary>
    public Action<int> Registered;
    /// <summary>The entry can't be used any more (the table changed under it): the mod must stop spawning it.</summary>
    public Action<string> Lost;
    /// <summary>Core gave the entry up before it was registered, for a reason of its own (guid taken, Fusion refused
    /// the source, the build threw or gave no template, the registry broke): the mod should switch itself off and
    /// free its template (Fusion's table never held it; an entry the table holds is reported through
    /// <see cref="Lost"/> instead). Not called when the mod's own <see cref="Build"/> returned <see cref="NetPrefabBuild.Fail"/>.
    /// Optional.</summary>
    public Action<string> Failed;
}

/// <summary>
/// Core's registry of runtime network prefabs (Fusion replicates the prefab <i>index</i>, so every peer must append
/// the same entries in the same order). Mods <see cref="Declare"/> entries by key from their plugin's Load; core
/// registers them in <see cref="NetPrefabPlan.Order"/>'s order (never in plugin load order), one at a time, each
/// only after every earlier declared one is in or given up, and only outside sessions (a session never sees the set
/// change). Before every StartGame the entries are checked (and re-added when Fusion reloaded its table).
/// <para>Join gate: a game with registered entries appends "|othp:np1:&lt;hash&gt;" (stock count + every entry's
/// index, key, guid and layout, see <see cref="ConnectionToken"/>) to its connection token, and a host with registered
/// entries refuses joiners whose hash differs: they could not load what it may spawn. A host with none admits
/// everyone, so nothing changes for players who don't run such mods.</para>
/// <para>Spawned copies of a registered template are woken the moment Fusion instantiates them (see
/// <see cref="InstantiatePostfix"/>), so mods can touch their bodies right after Spawn.</para>
/// </summary>
internal static class NetPrefabRegistry
{
    private enum State { Pending, Registered, Failed }

    private sealed class Slot
    {
        public NetPrefabEntry Entry;
        public State State;
        public int Index = -1;
        public NetworkObjectGuid Guid;
        public NetworkPrefabSourceStatic Source;
        public string WaitingFor;
        /// <summary>The registered template's GameObject (its copies are woken on instantiate).</summary>
        public IntPtr TemplateGo;
        public bool WakeLogged;
        /// <summary>Fusion's table holds the template (the mod must keep it, even if the entry is lost).</summary>
        public bool InTable;
        /// <summary>Main-thread time the build took, over how many frames, and its longest frame (for the log).</summary>
        public double BuildMs, LongestMs;
        public int BuildFrames;
        public string LongestStep;
    }

    private static readonly List<Slot> Slots = new();
    private static int _stock = -1;
    private static bool _patchesOk, _broken, _wakeErrorLogged;
    private static string _marker;
    /// <summary>The runner of the last StartGame: while it exists and isn't shut down (starting, running, or between
    /// the two), nothing registers, so a session's set never changes after its advert was built.</summary>
    private static NetworkRunner _session;
    /// <summary>The first registration attempt has begun: later declarations are refused (they could take an index
    /// another peer gave to someone else).</summary>
    private static bool _begun;
    private static float _next;
    private static readonly HashSet<string> RefusedLogged = new();

    /// <summary>The marker this game sends and expects (null: nothing registered).</summary>
    public static string MarkerValue => _marker;

    /// <summary>Game build's stock prefab count (-1 until the first entry registers).</summary>
    public static int StockCount => _stock;

    /// <summary>Declares an entry. Null when accepted, otherwise why not (the mod should then stay off).</summary>
    public static string Declare(NetPrefabEntry e)
    {
        if (!_patchesOk) return "core's join gate for runtime network prefabs is not installed";
        if (!NetPrefabPlan.MayDeclare(_begun))
        {
            string late = $"'{e?.Key}' was declared after registration began (declare from the plugin's Load)";
            Core.CorePlugin.Logger?.LogError("[NetPrefabs] " + late + ".");
            return late;
        }
        if (e == null || e.Build == null) return "no entry";
        if (NetPrefabPlan.OrderOf(e.Key) < 0) return $"'{e.Key}' is not in core's network prefab order (NetPrefabPlan.Order)";
        foreach (var s in Slots)
            if (s.Entry.Key == e.Key) return $"'{e.Key}' is declared twice";
        if (string.IsNullOrEmpty(e.Guid) || e.Guid.Length != 32) return "the entry's guid must be 32 hex digits";
        Slots.Add(new Slot { Entry = e });
        DebugLog.Info("core", $"network prefab '{e.Key}' declared by {NetPrefabPlan.OwnerOf(e.Key)} ({Slots.Count} declared)");
        Slots.Sort((a, b) => NetPrefabPlan.OrderOf(a.Entry.Key).CompareTo(NetPrefabPlan.OrderOf(b.Entry.Key)));
        return null;
    }

    /// <summary>
    /// Takes back every entry <paramref name="owner"/> declared (its Load failed after declaring them), so they never
    /// register or enter the join hash. Null when done (or there was nothing to take back), otherwise why not: once
    /// registration has begun the entries' indices are fixed, and the caller must say so (the mod's failure reason).
    /// </summary>
    public static string Withdraw(string owner)
    {
        if (string.IsNullOrEmpty(owner)) return null;
        int found = 0;
        foreach (var s in Slots)
            if (string.Equals(NetPrefabPlan.OwnerOf(s.Entry.Key), owner, StringComparison.OrdinalIgnoreCase)) found++;
        if (found == 0) return null;
        if (!NetPrefabPlan.MayWithdraw(_begun))
        {
            string why = $"its {found} network prefab(s) were registered already, so they stay in the join check until the game restarts";
            Core.CorePlugin.Logger?.LogError($"[NetPrefabs] {owner}: {why}.");
            return why;
        }
        Slots.RemoveAll(s => string.Equals(NetPrefabPlan.OwnerOf(s.Entry.Key), owner, StringComparison.OrdinalIgnoreCase));
        Core.CorePlugin.Logger?.LogInfo($"[NetPrefabs] {owner}: {found} declared entr{(found == 1 ? "y" : "ies")} taken back.");
        return null;
    }

    /// <summary>The mods whose entries are registered here (each once, in <see cref="NetPrefabPlan.OwnerMods"/> order):
    /// the set the join marker covers.</summary>
    public static List<string> RegisteredOwners()
    {
        var mods = new List<string>();
        foreach (var s in Slots)
        {
            if (s.State != State.Registered) continue;
            string o = NetPrefabPlan.OwnerOf(s.Entry.Key);
            if (o != null && !mods.Contains(o)) mods.Add(o);
        }
        return NetPrefabPlan.ModsOf(NetPrefabPlan.MaskOf(mods));
    }

    /// <summary>Some declared entry is still waiting to register: this game's set (and marker) isn't final yet.</summary>
    public static bool AnyPending
    {
        get
        {
            foreach (var s in Slots) if (s.State == State.Pending && !_broken) return true;
            return false;
        }
    }

    /// <summary>The prefab index of a registered entry, -1 otherwise.</summary>
    public static int IndexOf(string key)
    {
        foreach (var s in Slots)
            if (s.Entry.Key == key && s.State == State.Registered) return s.Index;
        return -1;
    }

    // ---------------------------------------------------------------- registering

    private static bool _tableSeen;

    /// <summary>Set while a session is starting (<see cref="BeforeStartGame"/>): every build must finish in the call
    /// that asks for it, background loads included.</summary>
    public static bool Urgent { get; private set; }

    private static readonly System.Diagnostics.Stopwatch FrameClock = new();

    /// <summary>The current frame still has main-thread time for more build steps (<see cref="NetPrefabSchedule.FrameBudgetMs"/>).</summary>
    public static bool BudgetLeft() => FrameClock.Elapsed.TotalMilliseconds < NetPrefabSchedule.FrameBudgetMs;

    private static readonly StartGate Gate = new();

    // Fusion's config is a Resources asset whose prefab table holds 58 "static lazy" sources (spawners, rocks, three
    // cars), and Unity's player loads those with it (lazy only in the editor): its first access pulls some 35,000
    // objects, 330 MB on disk, in one frame. It is loaded in the background first, so that first access finds it loaded.
    private const string ConfigResource = "NetworkProjectConfig";
    private const float ConfigLoadLimit = 120f;
    private static ResourceRequest _configLoad;
    private static UnityEngine.Object _configAsset;
    private static float _configLoadSince;
    private static bool _configLoadDone;

    internal static void Tick()
    {
        if (Slots.Count == 0) return;
        // Asked for from the first frame, not after the menu has settled: its load then runs next to the game's own first
        // stall (several seconds with nothing to draw anyway), instead of freezing the settled menu for as long again.
        if (!_configLoadDone && !_tableSeen && Logging.DebugConfig.Get(Logging.DebugConfig.EarlyFusionConfig, true)) ConfigLoaded();
        if (!Gate.Open)
        {
            if (!Gate.Update(Time.unscaledDeltaTime, Time.realtimeSinceStartup, MenuUp())) return;
            Logging.BootReport.Mark("menu settled");
            Core.CorePlugin.Logger?.LogInfo($"[NetPrefabs] the game is up ({(Gate.OpenedBy == "quiet" ? "the frames are steady" : "time limit")}, " +
                                            $"{Gate.WaitedSeconds:0.0} s after the first frame): {Slots.Count} network prefab(s) register now, a step per frame.");
        }
        if (Time.unscaledTime < _next) return;
        // Once registration has begun, a build step per frame (a car's whole build took a second or more, and all of
        // them in one frame froze the game for half a minute at start); before that, a check once a second.
        _next = Time.unscaledTime + (_begun && AnyPending ? 0f : 1f);
        if (SessionHolds()) return;
        if (!ConfigLoaded()) { _next = 0f; return; }
        FrameClock.Restart();
        TryRegister();
    }

    /// <summary>The game's main menu is on top of its state stack.</summary>
    private static bool MenuUp()
    {
        try
        {
            var top = GameState.TopState();
            return top != null && top.TryCast<StateMainMenu>() != null;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "NetPrefabRegistry.MenuUp"); return true; }
    }

    /// <summary>Fusion's config is loaded (or will be loaded by the first access, the old way): registration may go on.</summary>
    private static bool ConfigLoaded()
    {
        if (_configLoadDone || _tableSeen) return true;
        float now = Time.realtimeSinceStartup;
        if (_configLoad == null)
        {
            try
            {
                CrashTrail.Mark("netprefabs: Fusion's config in the background");
                _configLoad = Resources.LoadAsync(ConfigResource, Il2CppInterop.Runtime.Il2CppType.Of<ScriptableObject>());
                _configLoadSince = now;
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "NetPrefabRegistry.ConfigLoaded"); _configLoad = null; }
            if (_configLoad == null) { _configLoadDone = true; return true; }
            Core.CorePlugin.Logger?.LogInfo("[NetPrefabs] loading Fusion's config in the background.");
        }
        bool done;
        try { done = _configLoad.isDone; } catch (Exception ex) { DebugLog.Error("core", ex, "NetPrefabRegistry.ConfigLoaded"); done = true; }
        float took = now - _configLoadSince;
        if (!done && took < ConfigLoadLimit) return false;
        try { _configAsset = done ? _configLoad.asset : null; } catch (Exception ex) { DebugLog.Error("core", ex, "NetPrefabRegistry.ConfigLoaded"); _configAsset = null; }
        _configLoadDone = true;
        Logging.BootReport.Mark("Fusion's config in");
        Core.CorePlugin.Logger?.LogInfo(done
            ? $"[NetPrefabs] Fusion's config loaded in the background in {took:0.0} s" + (_configAsset == null ? " (not found under Resources: the first access loads it)." : ".")
            : $"[NetPrefabs] Fusion's config still loading after {took:0} s: the first access finishes it.");
        return true;
    }

    private static bool SessionHolds()
    {
        if (_session == null) return false;
        bool exists, shut;
        try { exists = _session != null && _session.Pointer != IntPtr.Zero && _session.gameObject != null; } catch (Exception ex) { DebugLog.Error("core", ex, "NetPrefabRegistry.SessionHolds"); exists = false; }
        try { shut = !exists || _session.IsShutdown; } catch (Exception ex) { DebugLog.Error("core", ex, "NetPrefabRegistry.SessionHolds"); shut = true; }
        if (NetPrefabPlan.SessionHolds(exists, shut)) return true;
        _session = null;
        return false;
    }

    private static void GiveUp(Slot s, string why, bool tellMod)
    {
        s.State = State.Failed;
        s.WaitingFor = null;
        s.TemplateGo = IntPtr.Zero;   // never in the table: nothing of it is ever instantiated by Fusion
        if (!tellMod) return;
        try { s.Entry.Failed?.Invoke(why); }
        catch (Exception e) { Core.CorePlugin.Logger?.LogError($"[NetPrefabs] {s.Entry.Key} failed callback: {e}"); }
    }

    private static RegisterStep TryRegister()
    {
        if (_broken) return RegisterStep.Broken;
        NetworkPrefabTable table;
        // The first touch of Fusion's config loads it (and whatever it references) right here, in one frame: marked and
        // timed, so a long first frame at start names it instead of just "tick NetPrefabFeature".
        bool first = !_tableSeen;
        long t0 = first ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (first) CrashTrail.Mark("netprefabs: Fusion's prefab table");
        try { table = NetworkProjectConfig.Global?.PrefabTable; } catch (Exception ex) { DebugLog.Error("core", ex, "NetPrefabRegistry.TryRegister"); table = null; }
        if (table == null) return RegisterStep.Waiting;
        if (first)
        {
            _tableSeen = true;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Core.CorePlugin.Logger?.LogInfo($"[NetPrefabs] Fusion's prefab table: {table._sources.Count} entries, first access took {ms:0} ms.");
        }
        foreach (var s in Slots)
        {
            if (s.State != State.Pending) continue;
            int count = table._sources.Count;
            int expected = _stock < 0 ? count : _stock + RegisteredCount();
            if (count != expected)
            {
                Break($"Fusion's prefab table has {count} entries where {expected} were expected (something else added prefabs)");
                return RegisterStep.Broken;
            }
            _begun = true;
            NetPrefabBuild b;
            bool modSaidNo = false;
            long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
            CrashTrail.Mark("netprefabs: build " + s.Entry.Key);
            try
            {
                b = s.Entry.Build(count);
                modSaidNo = b != null && b.Failed != null;
            }
            catch (Exception e) { DebugLog.Error("core", e, "NetPrefabRegistry.TryRegister"); b = NetPrefabBuild.Fail("build failed: " + e.Message); }
            if (b == null) b = NetPrefabBuild.Fail("no template");
            double buildMs = (System.Diagnostics.Stopwatch.GetTimestamp() - b0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            s.BuildMs += buildMs;
            s.BuildFrames++;
            if (buildMs > s.LongestMs) { s.LongestMs = buildMs; s.LongestStep = b.Step ?? (b.Template != null ? "last step" : "-"); }
            if (b.Step != null)
            {
                s.WaitingFor = null;
                return RegisterStep.Working; // same entry next frame: nothing after it may take an index first
            }
            if (b.WaitingFor != null)
            {
                s.WaitingFor = b.WaitingFor;
                return RegisterStep.Waiting; // later entries wait for this one: their indices depend on it
            }
            if (b.Failed != null || b.Template == null)
            {
                string why = b.Failed ?? "no template";
                Core.CorePlugin.Logger?.LogWarning($"[NetPrefabs] {s.Entry.Key} is not registered: {why}.");
                GiveUp(s, why, tellMod: !modSaidNo);
                return RegisterStep.GaveUp;
            }
            try
            {
                s.Guid = new NetworkObjectGuid(s.Entry.Guid);
                if (table.GetId(s.Guid).IsValid)
                {
                    Core.CorePlugin.Logger?.LogError($"[NetPrefabs] {s.Entry.Key}: its guid is taken.");
                    GiveUp(s, "its network prefab guid is already in Fusion's table", tellMod: true);
                    return RegisterStep.GaveUp;
                }
                s.Source = new NetworkPrefabSourceStatic { AssetGuid = s.Guid, AssetPath = s.Entry.AssetPath };
                s.Source.Object = b.Template;
                if (!table.TryAddSource(new INetworkPrefabSource(s.Source.Pointer), out var id))
                {
                    Core.CorePlugin.Logger?.LogError($"[NetPrefabs] Fusion refused {s.Entry.Key}.");
                    GiveUp(s, "Fusion refused its network prefab", tellMod: true);
                    return RegisterStep.GaveUp;
                }
                // in Fusion's table from here on: its copies are woken, and it is lost (never given up) if anything breaks
                s.InTable = true;
                s.TemplateGo = b.Template.gameObject.Pointer;
                if (id.AsIndex != count) { Break($"{s.Entry.Key} landed at index {id.AsIndex}, not {count}"); return RegisterStep.Broken; }
                if (_stock < 0) _stock = count;
                s.Index = count;
                s.State = State.Registered;
                s.WaitingFor = null;
                UpdateMarker();
                DebugLog.Info("core", $"network prefab '{s.Entry.Key}' registered at index {count} (join marker {_marker}, {_stock} stock prefabs)");
                Core.CorePlugin.Logger?.LogInfo($"[NetPrefabs] {s.Entry.Key} registered at index {count} (join marker {_marker}; " +
                                                $"built over {s.BuildFrames} frame(s), {s.BuildMs:0} ms in all, longest {s.LongestMs:0} ms at '{s.LongestStep}'" +
                                                (Urgent ? ", finished as a session started" : "") + ").");
            }
            catch (Exception e)
            {
                DebugLog.Error("core", e, "NetPrefabRegistry.TryRegister");
                Break($"{s.Entry.Key} could not be added: {e.Message}");
                return RegisterStep.Broken;
            }
            try { s.Entry.Registered?.Invoke(s.Index); }
            catch (Exception e) { Core.CorePlugin.Logger?.LogError($"[NetPrefabs] {s.Entry.Key} registered callback: {e}"); }
            if (!AnyPending)
            {
                Core.CorePlugin.Logger?.LogInfo($"[NetPrefabs] all done: {RegisteredCount()} of {Slots.Count} registered.");
                // nothing more will ask for a texture decoded ahead
                Cars.AssetPrefetch.Close();
                Logging.BootReport.Write();
            }
            return RegisterStep.Registered; // the next one on the next frame, so the game keeps drawing between cars
        }
        return RegisterStep.Idle;
    }

    private static int RegisteredCount()
    {
        int n = 0;
        foreach (var s in Slots) if (s.State == State.Registered) n++;
        return n;
    }

    private static void UpdateMarker()
    {
        var list = new List<(string, string, string, int)>();
        foreach (var s in Slots)
            if (s.State == State.Registered) list.Add((s.Entry.Key, s.Entry.Guid, s.Entry.Layout, s.Index));
        _marker = NetPrefabPlan.Hash(_stock, list);
    }

    /// <summary>The table no longer matches what was registered: every entry is lost and nothing more registers
    /// (this game then sends no marker, and spawns none of them).</summary>
    private static void Break(string why)
    {
        _broken = true;
        _marker = null;
        DebugLog.Error("core", "network prefabs broken: " + why);
        Core.CorePlugin.Logger?.LogError($"[NetPrefabs] {why}. Runtime network prefabs are off this session.");
        foreach (var s in Slots)
        {
            switch (s.State)
            {
                case State.Registered:
                    s.State = State.Failed;
                    try { s.Entry.Lost?.Invoke(why); } catch (Exception ex) { DebugLog.Error("core", ex, "NetPrefabRegistry.Break"); }
                    break;
                case State.Pending when s.InTable:
                    // added but never confirmed (it landed elsewhere): Fusion holds the template, so it is lost, not given up
                    s.State = State.Failed;
                    try { s.Entry.Lost?.Invoke(why); } catch (Exception ex) { DebugLog.Error("core", ex, "NetPrefabRegistry.Break"); }
                    break;
                case State.Pending:
                    GiveUp(s, why, tellMod: true);
                    break;
            }
        }
    }

    /// <summary>Before every StartGame: register what can be, check the registered entries (re-adding them in order
    /// if Fusion reloaded its table), and keep the set fixed for the session when something was still pending.</summary>
    internal static void BeforeStartGame(NetworkRunner runner)
    {
        if (Slots.Count == 0) return;
        FlushPending();
        if (!_broken && RegisteredCount() > 0) Verify();
        foreach (var s in Slots)
            if (s.State == State.Pending)
            {
                Core.CorePlugin.Logger?.LogWarning($"[NetPrefabs] a session starts while {s.Entry.Key} is not ready (waiting for " +
                                                   $"{s.WaitingFor ?? "the game"}): it stays out until that session is shut down.");
                break;
            }
        // From here until this runner is shut down, the set stays as it is (the advert is built right after this).
        _session = runner;
    }

    /// <summary>The game builds its connection token inside OverlandNetworkRunner.StartGame, before NetworkRunner.StartGame
    /// runs, so the flush has to come first or the token carries a marker for a half-registered set.</summary>
    private static void FlushPending()
    {
        if (!SessionHolds() && AnyPending)
        {
            // The session's set is fixed from here on, so everything that can register does, now, in this call: its
            // builds finish at once (background loads included). The start of the session waits for it, rather than the
            // cars staying out of it.
            Gate.Force(Time.realtimeSinceStartup);
            int before = RegisteredCount();
            long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Urgent = true;
            (int attempts, RegisterStep last) r;
            try { r = NetPrefabSchedule.Flush(TryRegister, NetPrefabSchedule.FlushAttempts(Slots.Count)); }
            finally { Urgent = false; }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - f0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int added = RegisteredCount() - before;
            if (added > 0 || r.last != RegisterStep.Idle)
                Core.CorePlugin.Logger?.LogInfo($"[NetPrefabs] a session is starting: {added} more registered first, in {ms:0} ms ({r.attempts} step(s), last: {r.last}).");
        }
    }

    private static void Verify()
    {
        try
        {
            var table = NetworkProjectConfig.Global?.PrefabTable;
            if (table == null) { Break("Fusion's prefab table is gone"); return; }
            bool allThere = true, noneThere = true;
            foreach (var s in Slots)
            {
                if (s.State != State.Registered) continue;
                var id = table.GetId(s.Guid);
                if (id.IsValid) noneThere = false;
                if (!id.IsValid || id.AsIndex != s.Index) allThere = false;
            }
            if (allThere) return;
            if (!noneThere || table._sources.Count != _stock) { Break("the registered prefabs moved in Fusion's table"); return; }
            // Fusion reloaded its config asset (it clears the table): add the same sources again, in order.
            foreach (var s in Slots)
            {
                if (s.State != State.Registered) continue;
                if (!table.TryAddSource(new INetworkPrefabSource(s.Source.Pointer), out var id) || id.AsIndex != s.Index)
                { Break($"{s.Entry.Key} could not be added again"); return; }
            }
            Core.CorePlugin.Logger?.LogInfo("[NetPrefabs] entries re-added (Fusion reloaded its prefab table).");
        }
        catch (Exception e) { DebugLog.Error("core", e, "NetPrefabRegistry.Verify"); Break("prefab check failed: " + e.Message); }
    }

    // ---------------------------------------------------------------- join gate

    private static string Text(Il2CppStructArray<byte> a)
    {
        if (a == null) return null;
        var b = new byte[a.Length];
        for (int i = 0; i < b.Length; i++) b[i] = a[i];
        return Encoding.UTF8.GetString(b);
    }

    private static void TokenPostfix(ref Il2CppStructArray<byte> __result)
    {
        try
        {
            if (_marker == null) return;
            string t = ConnectionToken.WithMarker(Text(__result) ?? "", NetPrefabPlan.MarkerName, _marker);
            __result = new Il2CppStructArray<byte>(Encoding.UTF8.GetBytes(t));
        }
        catch (Exception e) { DebugLog.Error("core", e, "NetPrefabRegistry.TokenPostfix"); Core.CorePlugin.Logger?.LogWarning("[NetPrefabs] connection token: " + e.Message); }
    }

    // Runs before the game's own checks (bans, missing token).
    private static bool ConnectPrefix(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, Il2CppStructArray<byte> token)
    {
        try
        {
            if (_marker == null || runner == null || !runner.IsServer) return true;
            string t = Text(token);
            var check = NetPrefabPlan.Check(ConnectionToken.Marker(t, NetPrefabPlan.MarkerName), _marker);
            if (check == NetPrefabCheck.Ok) return true;
            request.Refuse();
            string who = ConnectionToken.PlatformPart(t) ?? "?";
            DebugLog.Info("core", $"join refused for a player ({check}): their added cars/prefabs do not match this server's");
            if (RefusedLogged.Count < 64 && RefusedLogged.Add(who + check))
                Core.CorePlugin.Logger?.LogInfo(check == NetPrefabCheck.Missing
                    ? "[NetPrefabs] refused a player without this server's added cars/prefabs (their game could not load them)."
                    : "[NetPrefabs] refused a player whose added cars/prefabs are another version, set or game build.");
            return false;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "NetPrefabRegistry.ConnectPrefix");
            // Can't tell: refuse rather than let in someone who might not load what this server spawns.
            Core.CorePlugin.Logger?.LogWarning("[NetPrefabs] join check failed, refusing: " + e.Message);
            try { request.Refuse(); } catch (Exception ex) { DebugLog.Error("core", ex, "NetPrefabRegistry.ConnectPrefix"); }
            return false;
        }
    }

    /// <summary>
    /// Wakes each copy of a registered template the moment Fusion instantiates it, as a stock prefab copy is.
    /// <para>Stock prefabs are active assets, so <c>Object.Instantiate</c> runs every Awake at once (NetworkRigidbody3D.Awake
    /// finds its Rigidbody, NetworkObject.Awake marks it awoken) and Fusion's UnityPreInitialize leaves it alone. Runtime
    /// templates are inactive (so they never wake themselves): their copies come out inactive and Fusion takes its
    /// "Delaying activation" branch, activating them only later, so anything that touches the copy right after Spawn
    /// (a Rigidbody reset, a joint, a velocity) finds it asleep. Activating the copy here
    /// (NetworkObjectProviderDefault.InstantiatePrefab, on every peer that creates one: host, solo and guests) gives
    /// it exactly the stock path. Only copies of registered templates are touched; idempotent.</para>
    /// </summary>
    private static void InstantiatePostfix(NetworkObject prefab, NetworkObject __result)
    {
        try
        {
            if (Slots.Count == 0 || prefab == null || __result == null) return;
            IntPtr p = prefab.gameObject.Pointer;
            foreach (var s in Slots)
            {
                if (s.TemplateGo == IntPtr.Zero || s.TemplateGo != p) continue;
                var go = __result.gameObject;
                if (go.activeSelf) return;
                CrashTrail.Mark("netprefabs: wake " + s.Entry.Key);
                go.SetActive(true);
                CrashTrail.Mark("netprefabs: " + s.Entry.Key + " awake");
                if (!s.WakeLogged)
                {
                    s.WakeLogged = true;
                    // the reason for waking: NetworkRigidbody3D.Awake must have found its Rigidbody by now
                    var nrb = __result.GetComponent<Fusion.Addons.Physics.NetworkRigidbody3D>();
                    string rb = nrb == null ? "no NetworkRigidbody3D" : nrb.Rigidbody != null ? "rigidbody found" : "rigidbody MISSING";
                    Core.CorePlugin.Logger?.LogInfo($"[NetPrefabs] {s.Entry.Key} copy woken ({rb})");
                }
                return;
            }
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "NetPrefabRegistry.InstantiatePostfix");
            if (!_wakeErrorLogged) Core.CorePlugin.Logger?.LogWarning("[NetPrefabs] waking a spawned copy: " + e.Message);
            _wakeErrorLogged = true;
        }
    }

    private static void StartGamePrefix(NetworkRunner __instance)
    {
        try { BeforeStartGame(__instance); }
        catch (Exception e) { DebugLog.Error("core", e, "NetPrefabRegistry.StartGamePrefix"); Core.CorePlugin.Logger?.LogWarning("[NetPrefabs] start check: " + e.Message); }
    }

    private static void OverlandStartPrefix()
    {
        try { if (Slots.Count > 0) FlushPending(); }
        catch (Exception e) { DebugLog.Error("core", e, "NetPrefabRegistry.OverlandStartPrefix"); Core.CorePlugin.Logger?.LogWarning("[NetPrefabs] start flush: " + e.Message); }
    }

    /// <summary>Installs the token, join, start (both) and instantiate hooks (core's Load). Without all five, nothing can be
    /// declared.</summary>
    internal static void InstallPatches(Harmony h)
    {
        var done = new List<(System.Reflection.MethodBase original, System.Reflection.MethodInfo patch)>();
        try
        {
            var token = AccessTools.Method(typeof(OverlandNetworkUtils), nameof(OverlandNetworkUtils.GetOverlandConnectionToken));
            var connect = AccessTools.Method(typeof(OverlandNetworkRunner), "Fusion_INetworkRunnerCallbacks_OnConnectRequest");
            var start = AccessTools.Method(typeof(NetworkRunner), nameof(NetworkRunner.StartGame));
            var instantiate = AccessTools.Method(typeof(NetworkObjectProviderDefault), nameof(NetworkObjectProviderDefault.InstantiatePrefab));
            var overlandStart = AccessTools.Method(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.StartGame), new[] { typeof(GameSetupContext) });
            if (token == null || connect == null || start == null || instantiate == null || overlandStart == null) throw new MissingMethodException("a game method was not found");
            var tokenPatch = AccessTools.Method(typeof(NetPrefabRegistry), nameof(TokenPostfix));
            var connectPatch = AccessTools.Method(typeof(NetPrefabRegistry), nameof(ConnectPrefix));
            var startPatch = AccessTools.Method(typeof(NetPrefabRegistry), nameof(StartGamePrefix));
            var overlandStartPatch = AccessTools.Method(typeof(NetPrefabRegistry), nameof(OverlandStartPrefix));
            var instantiatePatch = AccessTools.Method(typeof(NetPrefabRegistry), nameof(InstantiatePostfix));
            h.Patch(token, postfix: new HarmonyMethod(tokenPatch)); done.Add((token, tokenPatch));
            h.Patch(connect, prefix: new HarmonyMethod(connectPatch) { priority = Priority.First }); done.Add((connect, connectPatch));
            h.Patch(start, prefix: new HarmonyMethod(startPatch) { priority = Priority.First }); done.Add((start, startPatch));
            h.Patch(overlandStart, prefix: new HarmonyMethod(overlandStartPatch) { priority = Priority.First }); done.Add((overlandStart, overlandStartPatch));
            h.Patch(instantiate, postfix: new HarmonyMethod(instantiatePatch) { priority = Priority.First }); done.Add((instantiate, instantiatePatch));
            _patchesOk = true;
        }
        catch (Exception e)
        {
            // Only our own patch methods: core's other patches on these methods stay.
            foreach (var (original, patch) in done)
                try { h.Unpatch(original, patch); } catch (Exception ex) { DebugLog.Error("core", ex, "NetPrefabRegistry.InstallPatches"); }
            Core.CorePlugin.Logger?.LogError("[NetPrefabs] hooks not installed (mods that add network prefabs stay off): " + e.GetBaseException().Message);
        }
    }
}

/// <summary>Runs the registry's once-a-second registration.</summary>
internal sealed class NetPrefabFeature : Features.IFeature
{
    public void Register() => Features.Registry.OnTick(NetPrefabRegistry.Tick);
}
