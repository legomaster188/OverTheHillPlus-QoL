using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.Logging;

/// <summary>
/// Which mod a type, an assembly or a log source belongs to, for blaming errors: every loaded plugin's types by full
/// name (nested types with dots, as stack traces write them), its log source name (the plugin's name) and its id
/// (com.oth.&lt;id&gt; gives &lt;id&gt;; another creator's plugin keeps its GUID). Built once on the first frame, when
/// every plugin has loaded; read from any thread afterwards (the maps are never changed once published).
/// </summary>
internal static class ModMap
{
    private sealed class Maps
    {
        public readonly Dictionary<string, string> Types = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Sources = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<Assembly, string> Assemblies = new();
        public readonly HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase);
    }

    private static Maps _maps;

    /// <summary>A mod assembly with types that could not load.</summary>
    internal sealed class TypeLoadProblem
    {
        public string Mod;
        public int Failed, Total;
        public List<string> Messages;
    }

    /// <summary>Mod assemblies with types that did not load, found by the last <see cref="Build"/>.</summary>
    internal static IReadOnlyList<TypeLoadProblem> TypeLoadProblems { get; private set; } = Array.Empty<TypeLoadProblem>();

    /// <summary>"com.oth.camera" gives "camera"; any other GUID stays as it is.</summary>
    public static string IdOfGuid(string guid) =>
        guid != null && guid.StartsWith("com.oth.", StringComparison.OrdinalIgnoreCase) ? guid.Substring(8) : guid;

    /// <summary>Builds the maps (main thread, once every plugin has loaded). Safe to call again.</summary>
    public static void Build()
    {
        var m = new Maps();
        var problems = new List<TypeLoadProblem>();
        try
        {
            foreach (var kv in IL2CPPChainloader.Instance.Plugins)
            {
                var info = kv.Value;
                string id = IdOfGuid(info?.Metadata?.GUID);
                if (string.IsNullOrEmpty(id)) continue;
                m.Ids.Add(id);
                if (!string.IsNullOrEmpty(info.Metadata.Name)) m.Sources[info.Metadata.Name] = id;
                Assembly asm = null;
                try { asm = info.Instance?.GetType().Assembly; } catch { }
                if (asm == null || m.Assemblies.ContainsKey(asm)) continue;
                m.Assemblies[asm] = id;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                    // Types of the mod that could not load (a game type they use is gone): the start-up self-checks report them.
                    var messages = new List<string>();
                    int failed = 0;
                    foreach (var le in e.LoaderExceptions ?? Array.Empty<Exception>())
                    {
                        failed++;
                        if (messages.Count < 3 && le != null) messages.Add(le.Message);
                    }
                    problems.Add(new TypeLoadProblem { Mod = id, Failed = Math.Max(failed, 1), Total = e.Types?.Length ?? 0, Messages = messages });
                }
                if (types == null || types.Length > 20000) continue;
                foreach (var t in types)
                {
                    string name = t?.FullName;
                    if (name == null) continue;
                    name = name.Replace('+', '.');
                    if (!m.Types.ContainsKey(name)) m.Types[name] = id;
                }
            }
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogDebug("[Debug] mod map: " + e.Message); }
        try
        {
            foreach (var mod in Core.Manager.ModManager.All)
                if (!string.IsNullOrEmpty(mod?.Id)) m.Ids.Add(mod.Id);
        }
        catch { }
        m.Ids.Add("core");
        TypeLoadProblems = problems;
        Volatile.Write(ref _maps, m);
    }

    /// <summary>The mod a type belongs to (full name, nested types with dots), or null.</summary>
    public static string ModOfType(string typeName)
    {
        var m = Volatile.Read(ref _maps);
        return m != null && typeName != null && m.Types.TryGetValue(typeName, out var id) ? id : null;
    }

    /// <summary>The mod of a BepInEx log source (a plugin's name), or null.</summary>
    public static string ModOfSource(string source)
    {
        var m = Volatile.Read(ref _maps);
        return m != null && source != null && m.Sources.TryGetValue(source, out var id) ? id : null;
    }

    /// <summary>The mod of an assembly, or null (any thread; before <see cref="Build"/>, OTH.&lt;Name&gt; gives the lower-case name).</summary>
    public static string ModOfAssembly(Assembly asm)
    {
        if (asm == null) return null;
        var m = Volatile.Read(ref _maps);
        if (m != null && m.Assemblies.TryGetValue(asm, out var id)) return id;
        string name = asm.GetName().Name;
        if (name == "OTH.Core") return "core";
        return name != null && name.StartsWith("OTH.", StringComparison.Ordinal) ? name.Substring(4).ToLowerInvariant() : null;
    }

    /// <summary>True for a known mod id (any thread). Before <see cref="Build"/>, the registered mods.</summary>
    public static bool IsModId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var m = Volatile.Read(ref _maps);
        if (m != null) return m.Ids.Contains(id);
        if (string.Equals(id, "core", StringComparison.OrdinalIgnoreCase)) return true;
        if (!CrashTrail.OnMainThread) return false;
        try { return Core.Manager.ModManager.Find(id) != null; } catch { return false; }
    }

    /// <summary>The mod a crash-trail step names (see <see cref="StackText.ModOfStep"/>), any thread.</summary>
    public static string ModOfStep(string step) => StackText.ModOfStep(step, IsModId);
}
