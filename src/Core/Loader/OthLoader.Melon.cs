#if MELON
extern alias melon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using melon::MelonLoader;
using melon::MelonLoader.Utils;
using UnityEngine;

namespace OverTheHillPlus.Loader;

/// <summary>MelonLoader 0.7 (IL2CPP, net6): mods in the game's Mods folder, settings in UserData/OverTheHill, the log in
/// MelonLoader/Latest.log.</summary>
public static partial class OthLoader
{
    /// <summary>MelonLoader's version.</summary>
    public static string Version => typeof(MelonMod).Assembly.GetName().Version?.ToString();

    /// <summary>The settings folder: UserData/OverTheHill (created on first use).</summary>
    public static string ConfigDir => Folder(ref _configDir, Path.Combine(MelonEnvironment.UserDataDirectory, "OverTheHill"));
    private static string _configDir;

    /// <summary>Where the debug log, reports and crash trail go: UserData/OverTheHill too.</summary>
    public static string DataDir => ConfigDir;

    /// <summary>Where melons are loaded from: the game's Mods folder.</summary>
    public static string PluginsDir => MelonEnvironment.ModsDirectory;

    /// <summary>Our mods' folder: the game's Mods folder (OTH.Core.dll, OTH.&lt;Name&gt;.dll).</summary>
    public static string ModsDir => MelonEnvironment.ModsDirectory;

    /// <summary>The game's interop assemblies: MelonLoader/Il2CppAssemblies.</summary>
    public static string InteropDir => MelonEnvironment.Il2CppAssembliesDirectory;

    /// <summary>Our release's DLLs are BepInEx builds, so the AVAILABLE tab doesn't install or update anything here.</summary>
    public static bool CanInstall => false;

    private static string Folder(ref string cached, string path)
    {
        if (cached != null) return cached;
        try { Directory.CreateDirectory(path); } catch { }
        return cached = path;
    }

    private static readonly ConditionalWeakTable<MelonLogger.Instance, ModLog> Wrapped = new();

    /// <summary>A ModLog that writes to this melon's logger (one per logger).</summary>
    public static ModLog Wrap(MelonLogger.Instance logger, string name)
    {
        if (logger == null) return null;
        return Wrapped.GetValue(logger, (MelonLogger.Instance l) => new ModLog(name, (LogKind kind, object data) => Write(l, kind, data)));
    }

    private static void Write(MelonLogger.Instance l, LogKind kind, object data)
    {
        string text = data?.ToString() ?? "";
        switch (kind)
        {
            case LogKind.Fatal:
            case LogKind.Error: l.Error(text); break;
            case LogKind.Warning: l.Warning(text); break;
            case LogKind.Debug: if (MelonDebug.IsEnabled()) l.Msg(text); break;
            default: l.Msg(text); break;
        }
    }

    /// <summary>A new log of our own, under <paramref name="name"/>.</summary>
    public static ModLog CreateLog(string name) => Wrap(new MelonLogger.Instance(name), name);

    /// <summary>Every line any melon logs (its name, level, text). MelonLoader reports messages, warnings and errors;
    /// they are told apart by the callback that fires.</summary>
    public static void AddLogListener(Action<string, LogKind, string> listener)
    {
        if (listener == null) return;
        MelonLogger.MsgDrawingCallbackHandler += (melon::MelonLoader.Logging.ColorARGB melonColor, melon::MelonLoader.Logging.ColorARGB textColor, string section, string msg) =>
            listener(section, LogKind.Info, msg);
        MelonLogger.WarningCallbackHandler += (string section, string msg) => listener(section, LogKind.Warning, msg);
        MelonLogger.ErrorCallbackHandler += (string section, string msg) => listener(section, LogKind.Error, msg);
    }

    private static GameObject _host;

    /// <summary>Adds a component of an injected class to a hidden object of ours that survives scene loads (what
    /// BepInEx's BasePlugin.AddComponent does).</summary>
    public static T AddComponent<T>() where T : Il2CppObjectBase
    {
        RegisterType<T>();
        if (_host == null)
        {
            _host = new GameObject("OTH Mods");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.hideFlags = HideFlags.HideAndDontSave;
        }
        return _host.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<T>()).Cast<T>();
    }

    /// <summary>Registers a managed class in IL2CPP (a MonoBehaviour of ours) once.</summary>
    public static void RegisterType<T>() where T : class
    {
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<T>()) ClassInjector.RegisterTypeInIl2Cpp<T>();
    }

    private static readonly List<object> Hooks = new();

    /// <summary>Puts a native hook on <paramref name="target"/>; <paramref name="original"/> calls the game's code.
    /// The returned object keeps the hook alive (the caller keeps <paramref name="hook"/> referenced). The hook must never
    /// let an exception out: it is attached directly, without MelonLoader's exception guard (see <see cref="AttachDirect"/>).</summary>
    public static object NativeDetour<T>(IntPtr target, T hook, out T original) where T : Delegate
    {
        IntPtr detour = Marshal.GetFunctionPointerForDelegate(hook);
        var direct = AttachDirect(target, detour);
        if (direct != null)
        {
            original = Marshal.GetDelegateForFunctionPointer<T>(direct.Trampoline);
            lock (Hooks) Hooks.Add(direct);
            return direct;
        }
        var h = new melon::MelonLoader.NativeUtils.NativeHook<T>(target, detour);
        h.Attach();
        original = h.Trampoline;
        lock (Hooks) Hooks.Add(h);
        return h;
    }

    /// <summary>A hook put in by <see cref="AttachDirect"/>: the cell the trampoline came back in, kept for the game run.</summary>
    private sealed class DirectHook
    {
        public IntPtr Cell, Trampoline;
    }

    private static System.Reflection.MethodInfo _attachDirect;
    private static bool _attachDirectLooked;

    /// <summary>
    /// MelonLoader's NativeHook.Attach first runs CoreClrDelegateFixer.SanityCheckDetour, which takes a ClrMD snapshot of
    /// the whole process (DataTarget.CreateSnapshotAndAttach) to find the managed method behind the hook and wrap it in an
    /// exception guard. On this game (13-16 GB of commit with the car mods) that snapshot fails with error 1450 when Windows
    /// is short of commit, and stalls a frame for about 350 ms when it works. Its own attach (BootstrapInterop.
    /// NativeHookAttachDirect: a cell holding the target in, the trampoline back in the cell) needs none of that. Null when
    /// this MelonLoader has no such method or it failed (the caller then uses NativeHook.Attach).
    /// </summary>
    private static DirectHook AttachDirect(IntPtr target, IntPtr detour)
    {
        if (!_attachDirectLooked)
        {
            _attachDirectLooked = true;
            try
            {
                _attachDirect = typeof(melon::MelonLoader.MelonMod).Assembly.GetType("MelonLoader.InternalUtils.BootstrapInterop", false)?
                    .GetMethod("NativeHookAttachDirect", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                        null, new[] { typeof(IntPtr), typeof(IntPtr) }, null);
            }
            catch (Exception e) { _attachDirect = null; MelonLogger.Warning("[OTH] NativeHookAttachDirect lookup failed: " + e.Message); }
        }
        if (_attachDirect == null) return null;
        IntPtr cell = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(cell, target);
            _attachDirect.Invoke(null, new object[] { cell, detour });
            IntPtr trampoline = Marshal.ReadIntPtr(cell);
            if (trampoline == IntPtr.Zero || trampoline == target)
                throw new InvalidOperationException("no trampoline came back");
            return new DirectHook { Cell = cell, Trampoline = trampoline };
        }
        catch (Exception e)
        {
            Marshal.FreeHGlobal(cell);
            MelonLogger.Warning("[OTH] direct native hook failed, using NativeHook.Attach: " + (e.InnerException ?? e).Message);
            return null;
        }
    }

    private static IReadOnlyDictionary<string, LoadedPlugin> LoadedPlugins()
    {
        var map = new Dictionary<string, LoadedPlugin>();
        Add(map, MelonTypeBase<MelonMod>.RegisteredMelons);
        Add(map, MelonTypeBase<MelonPlugin>.RegisteredMelons);
        return map;
    }

    private static void Add<T>(Dictionary<string, LoadedPlugin> map, IEnumerable<T> melons) where T : MelonBase
    {
        if (melons == null) return;
        foreach (var m in melons)
        {
            if (m == null) continue;
            string name = m.Info?.Name ?? m.GetType().Name;
            string guid = GuidOf(m.GetType()) ?? "com.melon." + name;
            string location = null;
            try { location = m.MelonAssembly?.Location; } catch { }
            map[guid] = new LoadedPlugin { Guid = guid, Name = name, Version = m.Info?.Version, Location = location, Instance = m };
        }
    }

    private static IReadOnlyList<string> DependencyErrorList() => Array.Empty<string>();
}

/// <summary>
/// The MelonLoader entry of each of our mods: the same plugin class as under BepInEx (Load, Log, AddComponent),
/// with MelonLoader calling Load when it initialises the mod (core first, see its MelonPriority).
/// </summary>
public abstract class OthMod : MelonMod
{
    private ModLog _log;

    /// <summary>The mod's log (MelonLoader/Latest.log, under the mod's name).</summary>
    public ModLog Log => _log ??= OthLoader.Wrap(LoggerInstance, Info?.Name ?? GetType().Name);

    /// <summary>The mod's start, as BepInEx's BasePlugin.Load.</summary>
    public abstract void Load();

    public override void OnInitializeMelon()
    {
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        try { Load(); }
        finally { OverTheHillPlus.Logging.BootReport.Loaded(Info?.Name ?? GetType().Name, System.Diagnostics.Stopwatch.GetTimestamp() - began); }
    }

    /// <summary>Adds a component of an injected class to a hidden object that survives scene loads.</summary>
    public T AddComponent<T>() where T : Il2CppObjectBase => OthLoader.AddComponent<T>();
}
#endif
