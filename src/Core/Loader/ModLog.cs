using System;

namespace OverTheHillPlus.Loader;

/// <summary>How serious a log line is (BepInEx's levels; MelonLoader shows Fatal and Error as errors, Message and Info
/// as messages, and Debug only in its debug mode).</summary>
public enum LogKind { Fatal, Error, Warning, Message, Info, Debug }

/// <summary>
/// A mod's log, the same API under both loaders (LogInfo, LogWarning, LogError, ...). Under BepInEx it writes to the
/// plugin's ManualLogSource (BepInEx/LogOutput.log); under MelonLoader to the mod's MelonLogger (MelonLoader/Latest.log).
/// </summary>
public sealed class ModLog
{
    private readonly Action<LogKind, object> _write;

    public ModLog(string sourceName, Action<LogKind, object> write)
    {
        SourceName = sourceName ?? "?";
        _write = write;
    }

    /// <summary>The name lines are logged under (the plugin's name).</summary>
    public string SourceName { get; }

    public void Log(LogKind kind, object data) => _write?.Invoke(kind, data);

    public void LogFatal(object data) => Log(LogKind.Fatal, data);
    public void LogError(object data) => Log(LogKind.Error, data);
    public void LogWarning(object data) => Log(LogKind.Warning, data);
    public void LogMessage(object data) => Log(LogKind.Message, data);
    public void LogInfo(object data) => Log(LogKind.Info, data);
    public void LogDebug(object data) => Log(LogKind.Debug, data);

#if !MELON && !OTH_PURE
    /// <summary>A BepInEx plugin's own log (<c>Logger = Log;</c> in Load).</summary>
    public static implicit operator ModLog(BepInEx.Logging.ManualLogSource source) => OthLoader.Wrap(source);
#endif
}
