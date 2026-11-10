#if !MELON
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.InteropTypes;

namespace OverTheHillPlus.Loader;

/// <summary>BepInEx 6 (IL2CPP): plugins in BepInEx/plugins, settings in BepInEx/config, the log in BepInEx/LogOutput.log.</summary>
public static partial class OthLoader
{
    /// <summary>BepInEx's version.</summary>
    public static string Version =>
        typeof(Paths).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Paths).Assembly.GetName().Version?.ToString();

    /// <summary>The settings folder: BepInEx/config.</summary>
    public static string ConfigDir => Paths.ConfigPath;

    /// <summary>Where the debug log, reports and crash trail go: the BepInEx folder.</summary>
    public static string DataDir => Paths.BepInExRootPath;

    /// <summary>Where plugins are loaded from: BepInEx/plugins.</summary>
    public static string PluginsDir => Paths.PluginPath;

    /// <summary>Our mods' folder, where the installer puts downloads: BepInEx/plugins/OverTheHillMods.</summary>
    public static string ModsDir => Path.Combine(Paths.PluginPath, "OverTheHillMods");

    /// <summary>The game's interop assemblies: BepInEx/interop.</summary>
    public static string InteropDir => Path.Combine(Paths.BepInExRootPath, "interop");

    /// <summary>The MODS page's AVAILABLE tab may download and install our release's DLLs (they are BepInEx builds).</summary>
    public static bool CanInstall => true;

    private static readonly ConditionalWeakTable<ManualLogSource, ModLog> Wrapped = new();

    /// <summary>A ModLog that writes to this BepInEx log source (one per source).</summary>
    public static ModLog Wrap(ManualLogSource source)
    {
        if (source == null) return null;
        return Wrapped.GetValue(source, Make);
    }

    private static ModLog Make(ManualLogSource s) => new(s.SourceName, (LogKind kind, object data) => s.Log(Level(kind), data));

    private static LogLevel Level(LogKind kind) => kind switch
    {
        LogKind.Fatal => LogLevel.Fatal,
        LogKind.Error => LogLevel.Error,
        LogKind.Warning => LogLevel.Warning,
        LogKind.Message => LogLevel.Message,
        LogKind.Info => LogLevel.Info,
        _ => LogLevel.Debug,
    };

    private static LogKind Kind(LogLevel level) =>
        (level & LogLevel.Fatal) != 0 ? LogKind.Fatal :
        (level & LogLevel.Error) != 0 ? LogKind.Error :
        (level & LogLevel.Warning) != 0 ? LogKind.Warning :
        (level & LogLevel.Message) != 0 ? LogKind.Message :
        (level & LogLevel.Info) != 0 ? LogKind.Info : LogKind.Debug;

    /// <summary>A new log source of our own (e.g. for a part of core that logs under its own name).</summary>
    public static ModLog CreateLog(string name) => Wrap(BepInEx.Logging.Logger.CreateLogSource(name));

    /// <summary>Every line any plugin logs (source name, level, text), on whatever thread logged it.</summary>
    public static void AddLogListener(Action<string, LogKind, string> listener)
    {
        if (listener != null) BepInEx.Logging.Logger.Listeners.Add(new Listener(listener));
    }

    private sealed class Listener : ILogListener
    {
        private readonly Action<string, LogKind, string> _on;
        public Listener(Action<string, LogKind, string> on) { _on = on; }
        public LogLevel LogLevelFilter => LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info;
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (eventArgs == null) return;
            _on(eventArgs.Source?.SourceName, Kind(eventArgs.Level), eventArgs.Data?.ToString());
        }
        public void Dispose() { }
    }

    /// <summary>Adds a component of an injected class to BepInEx's own hidden object (what BasePlugin.AddComponent does).</summary>
    public static T AddComponent<T>() where T : Il2CppObjectBase => IL2CPPChainloader.AddUnityComponent<T>();

    /// <summary>Registers a managed class in IL2CPP (a MonoBehaviour of ours) once.</summary>
    public static void RegisterType<T>() where T : class
    {
        if (!Il2CppInterop.Runtime.Injection.ClassInjector.IsTypeRegisteredInIl2Cpp<T>())
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<T>();
    }

    /// <summary>Puts a native hook on <paramref name="target"/>; <paramref name="original"/> calls the game's code.
    /// The returned object keeps the hook alive.</summary>
    public static object NativeDetour<T>(IntPtr target, T hook, out T original) where T : Delegate =>
        BepInEx.Unity.IL2CPP.Hook.INativeDetour.CreateAndApply(target, hook, out original);

    // BepInPlugin.Version is a SemanticVersioning type, which core doesn't reference: read it as an object.
    private static readonly PropertyInfo VersionProp = typeof(BepInPlugin).GetProperty("Version");

    /// <summary>BepInEx's PluginInfo as core sees it.</summary>
    public static LoadedPlugin From(PluginInfo p)
    {
        var meta = p?.Metadata;
        if (meta == null) return null;
        string version = null;
        try { version = VersionProp?.GetValue(meta)?.ToString(); } catch { }
        object instance = null;
        try { instance = p.Instance; } catch { }
        return new LoadedPlugin { Guid = meta.GUID, Name = meta.Name, Version = version, Location = p.Location, Instance = instance };
    }

    private static IReadOnlyDictionary<string, LoadedPlugin> LoadedPlugins()
    {
        var map = new Dictionary<string, LoadedPlugin>();
        var plugins = IL2CPPChainloader.Instance?.Plugins;
        if (plugins == null) return map;
        foreach (var kv in plugins)
        {
            var lp = From(kv.Value);
            if (lp != null) map[kv.Key] = lp;
        }
        return map;
    }

    private static IReadOnlyList<string> DependencyErrorList() =>
        (IReadOnlyList<string>)IL2CPPChainloader.Instance?.DependencyErrors ?? Array.Empty<string>();
}
#endif
