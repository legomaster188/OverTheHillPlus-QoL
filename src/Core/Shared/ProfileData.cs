using System;
using System.IO;
using OverTheHillPlus.Cars;
using OverTheHillPlus.Core.Shared;
using OverTheHillPlus.Loader;
using UnityEngine;

namespace OverTheHillPlus.Shared;

/// <summary>
/// A mod's own numbers kept per game profile in its own sidecar (<c>oth.&lt;mod&gt;.&lt;profile&gt;.json</c> in the config folder), never in the
/// game's save. The mod hands over how to read and write its data as text; this class loads the right file when the profile
/// is known or changes, writes it (temp file then replace) a few seconds after a change, on a profile switch and on quit, and keeps a
/// file it cannot read aside as ".bad" instead of writing over it. Main thread only.
/// </summary>
internal sealed class ProfileData
{
    private readonly string _id;
    private readonly Func<string, bool> _load;
    private readonly Func<string> _save;
    private string _key;
    private bool _dirty, _blocked, _hadRunner, _warned;
    private float _nextCheck, _saveAt = -1f;

    /// <summary>Where a problem goes (the mod's log); null = silent.</summary>
    public Action<string> Warn;

    public ProfileData(string modId, Func<string, bool> load, Func<string> save)
    {
        _id = modId;
        _load = load;
        _save = save;
    }

    /// <summary>File-name-safe key of the profile in use ("default" until the game has loaded one).</summary>
    public static string CurrentKey
    {
        get
        {
            try { return CarSidecar.FileKey(OverlandSaver.CurrentProfileSave?.Profile?.Name); }
            catch (Exception e) { DebugLog.Error("core", e, "ProfileData.CurrentKey"); return "default"; }
        }
    }

    public string Key => _key ?? "";

    public string PathFor(string key) => Path.Combine(OthLoader.ConfigDir, "oth." + _id + "." + key + ".json");

    /// <summary>
    /// The data changed: it is written <paramref name="delay"/> seconds from now (a later call can only pull that nearer).
    /// Rare events keep the short default; a counter that moves every frame should pass 30 to 60 so the disk isn't hit often.
    /// </summary>
    public void MarkDirty(float delay = 5f)
    {
        _saveAt = SaveSchedule.Next(_dirty, _saveAt, Time.unscaledTime, delay);
        _dirty = true;
    }

    /// <summary>Call every frame (cheap): follows the profile and writes pending changes.</summary>
    public void Tick()
    {
        float now = Time.unscaledTime;
        if (_dirty && now >= _saveAt) Flush();
        // leaving a world writes at once, so the long delays above never cost a session's numbers
        bool inWorld = GameState.Runner != null;
        if (_hadRunner && !inWorld) Flush();
        _hadRunner = inWorld;
        if (now < _nextCheck) return;
        _nextCheck = now + 2f;
        string key = CurrentKey;
        if (key == _key) return;
        Flush();
        _key = key;
        Read(key);
    }

    private void Read(string key)
    {
        _blocked = false;
        string path = PathFor(key);
        try
        {
            AtomicFile.Recover(path);
            bool ok = !File.Exists(path) ? _load(null) : _load(File.ReadAllText(path));
            if (ok) return;
            string bad = AtomicFile.SetAside(path);
            Warn?.Invoke($"{Path.GetFileName(path)} could not be read; " + (bad != null ? "kept aside as " + Path.GetFileName(bad) : "it is left as it is and not written over this session"));
            _blocked = bad == null;
            _load(null);
        }
        catch (Exception e)
        {
            DebugLog.Error(_id, e, "ProfileData.Read");
            _blocked = true;
            Warn?.Invoke("could not read " + Path.GetFileName(path) + ": " + e.Message);
            try { _load(null); } catch (Exception) { /* the line above has the cause */ }
        }
    }

    /// <summary>Writes now when something changed.</summary>
    public void Flush()
    {
        if (!_dirty || _key == null) return;
        if (_blocked) { _dirty = false; return; }
        try
        {
            AtomicFile.WriteAllText(PathFor(_key), _save());
            _dirty = false;
            _warned = false;
        }
        catch (Exception e)
        {
            // still dirty: tried again in half a minute
            _saveAt = Time.unscaledTime + 30f;
            DebugLog.Error(_id, e, "ProfileData.Flush");
            if (!_warned) Warn?.Invoke("could not save (trying again later): " + e.Message);
            _warned = true;
        }
    }
}
