namespace OverTheHillPlus.Loader;

/// <summary>
/// Where things are, as the player reads it in a setting's description or on a page, for the loader this DLL was built
/// for (-p:Loader=Melon builds the MelonLoader version). No game types: the tests link it.
/// </summary>
public static class LoaderText
{
#if MELON
    public const string LoaderName = "MelonLoader";
    /// <summary>The folder of the settings files (oth.&lt;id&gt;.cfg) and the mods' own data folders.</summary>
    public const string ConfigDir = "UserData/OverTheHill";
    /// <summary>The folder of the debug log (oth-debug) and the reports (oth-reports).</summary>
    public const string DataDir = "UserData/OverTheHill";
    /// <summary>The loader's own log file.</summary>
    public const string LogFile = "MelonLoader/Latest.log";
    /// <summary>The folder the mods' DLLs are in.</summary>
    public const string ModsDir = "Mods";
#else
    public const string LoaderName = "BepInEx";
    public const string ConfigDir = "BepInEx/config";
    public const string DataDir = "BepInEx";
    public const string LogFile = "BepInEx/LogOutput.log";
    public const string ModsDir = "BepInEx/plugins";
#endif
}
