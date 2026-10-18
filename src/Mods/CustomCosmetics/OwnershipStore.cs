using System;
using System.IO;
using BepInEx;
using OverTheHillPlus.Core.Shared;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// The sidecar file behind <see cref="Ownership"/>: BepInEx/config/oth.customcosmetics.&lt;profile&gt;.sidecar, one per game
/// profile, written on every change (it is a few lines). The game's save is never touched with a custom item id. A file
/// that can't be read is left as it is and never written over, so a damaged file is still there to recover from.
/// </summary>
internal static class OwnershipStore
{
    private const string Mod = CustomCosmeticsPlugin.ModId;

    private static string _key, _name;
    private static Ownership _data = new();
    private static string _blocked;

    /// <summary>The profile's data (loaded when the game's profile changes).</summary>
    public static Ownership Current
    {
        get
        {
            string name = null;
            try { name = OverlandSaver.CurrentProfileSave?.Profile?.Name; }
            catch (Exception e) { DebugLog.Error(Mod, e, "OwnershipStore.Current"); }
            if (_key == null || name != _name)
            {
                _name = name;
                string key = Ownership.FileKey(name);
                if (key != _key) Load(key);
            }
            return _data;
        }
    }

    private static string PathOf(string key) => Path.Combine(Paths.ConfigPath, "oth.customcosmetics." + key + ".sidecar");

    private static void Load(string key)
    {
        _key = key;
        _data = new Ownership();
        _blocked = null;
        string path = PathOf(key);
        try
        {
            AtomicFile.Recover(path);
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            var data = Ownership.Decode(text, out int bad);
            if (data == null)
            {
                _blocked = key;
                CustomCosmeticsPlugin.Logger?.LogWarning($"Custom Cosmetics: {Path.GetFileName(path)} is not ours or is damaged; it is left as it is and nothing is saved over it this session.");
                return;
            }
            _data = data;
            CustomCosmeticsPlugin.Logger?.LogInfo($"Custom Cosmetics: profile \"{key}\": {data.OwnedCount} owned, {data.InUseCount} mounted" +
                                                  (bad > 0 ? $", {bad} unreadable line(s) skipped" : "") + ".");
        }
        catch (Exception e)
        {
            _blocked = key;
            DebugLog.Error(Mod, e, "OwnershipStore.Load");
            CustomCosmeticsPlugin.Logger?.LogWarning("Custom Cosmetics: could not read the sidecar (left as it is): " + e.Message);
        }
    }

    /// <summary>Writes the current data (after a change).</summary>
    public static void Save()
    {
        if (_key == null || _blocked == _key) return;
        try { AtomicFile.WriteAllText(PathOf(_key), _data.Encode()); }
        catch (Exception e)
        {
            DebugLog.Error(Mod, e, "OwnershipStore.Save");
            CustomCosmeticsPlugin.Logger?.LogWarning("Custom Cosmetics: could not write the sidecar: " + e.Message);
        }
    }
}
