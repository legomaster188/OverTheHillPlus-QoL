using System;

namespace OverTheHillPlus.Core.Manager;

/// <summary>Who needs a mod installed for it to work (shown as a badge on the MODS page).</summary>
public enum ModSide
{
    /// <summary>Only you: nothing of it reaches other players.</summary>
    Client,
    /// <summary>Only the host; guests may be vanilla.</summary>
    Host,
    /// <summary>Everyone in the session. A host running it advertises it (session property othp.req); guests run its
    /// networked part only while the session advertises it (ModManager.ServerModActive).</summary>
    Server,
}

/// <summary>What a mod tells the manager about itself, once, from its plugin's Load.</summary>
public sealed class ModInfo
{
    /// <summary>Short lowercase id, e.g. "chat". Names the config file (oth.chat.cfg) and the enabled switch.</summary>
    public string Id;
    public string Name;
    public string Description;
    public string Version;
    /// <summary>Private mods are never listed in the public manifest or release.</summary>
    public bool Private;
    /// <summary>Who needs it installed (default <see cref="ModSide.Client"/>).</summary>
    public ModSide Side;
    /// <summary>How finished it is: "Stable", "Beta" or "Rough" (shown on its card and in the release list). Empty: the
    /// Mod Manager's table decides (<see cref="ModMaturity"/>: Beta for a mod of ours it doesn't list).</summary>
    public string Maturity;
    /// <summary>Draws the mod's everyday settings (SimpleMenu rows), first on its MODS page; null when it has none.</summary>
    public Action DrawSimple;
    /// <summary>Draws the mod's advanced settings at the top of its page's Advanced fold, above the cards its features
    /// added with Registry.Section; null when it has none.</summary>
    public Action DrawAdvanced;
}
