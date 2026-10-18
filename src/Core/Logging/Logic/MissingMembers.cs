using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;

namespace OverTheHillPlus.Logging;

/// <summary>
/// What an assembly's compiled code refers to that is not there (any more): reads the assembly's metadata tables (no code
/// runs, nothing is JIT-compiled) and looks up every type and member it references in the game's assemblies, as
/// loaded now. A game update that renames or removes something a mod calls shows up here at start-up instead of as an
/// exception the first time the player uses the feature. Members are matched by name only (overloads are not told
/// apart). Pure: the caller hands in the metadata and says which assemblies to check.
/// </summary>
internal static class MissingMembers
{
    internal sealed class Result
    {
        public int TypesChecked, MembersChecked;
        public readonly List<string> Missing = new();
        public int MissingTotal;
        public bool Truncated => MissingTotal > Missing.Count;
    }

    private const BindingFlags AnyDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <param name="md">The metadata of the assembly to check.</param>
    /// <param name="assemblyByName">The loaded assembly of that simple name, or null (not loaded: skipped).</param>
    /// <param name="isChecked">True for the simple names of the assemblies whose types are checked (the game's, not the framework's).</param>
    public static Result Find(MetadataReader md, Func<string, Assembly> assemblyByName, Func<string, bool> isChecked, int maxReport = 40)
    {
        var result = new Result();
        var resolved = new Dictionary<TypeReferenceHandle, Type>();
        var reportedTypes = new HashSet<TypeReferenceHandle>();
        var memberCache = new Dictionary<(Type, string), bool>();

        Type Resolve(TypeReferenceHandle handle)
        {
            if (resolved.TryGetValue(handle, out var known)) return known;
            resolved[handle] = null; // also guards against a cycle
            var tr = md.GetTypeReference(handle);
            string name = md.GetString(tr.Name);
            string ns = md.GetString(tr.Namespace);
            Type found = null;
            string where = null;
            switch (tr.ResolutionScope.Kind)
            {
                case HandleKind.AssemblyReference:
                    {
                        string an = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name);
                        if (!isChecked(an)) return null;
                        var asm = assemblyByName(an);
                        if (asm == null) return null;
                        result.TypesChecked++;
                        string full = ns.Length > 0 ? ns + "." + name : name;
                        found = asm.GetType(full, false);
                        where = full + " (" + an + ")";
                        break;
                    }
                case HandleKind.TypeReference:
                    {
                        var outer = Resolve((TypeReferenceHandle)tr.ResolutionScope);
                        if (outer == null) return null;
                        result.TypesChecked++;
                        found = outer.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
                        where = outer.FullName + "+" + name;
                        break;
                    }
                default:
                    return null;
            }
            if (found == null)
            {
                if (reportedTypes.Add(handle)) Add("type " + where);
            }
            resolved[handle] = found;
            return found;
        }

        void Add(string text)
        {
            result.MissingTotal++;
            if (result.Missing.Count < maxReport) result.Missing.Add(text);
        }

        // Type references first (a missing type is reported once, not once per member).
        foreach (var h in md.TypeReferences) Resolve(h);

        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue; // generic instances, arrays, methods of this assembly
            var type = Resolve((TypeReferenceHandle)mr.Parent);
            if (type == null) continue;
            string name = md.GetString(mr.Name);
            result.MembersChecked++;
            if (!memberCache.TryGetValue((type, name), out bool has))
            {
                has = HasMember(type, name);
                memberCache[(type, name)] = has;
                if (!has) Add(type.FullName + "." + name);
            }
        }
        return result;
    }

    /// <summary>True when the type, one of its base types or one of its interfaces declares a member of this name.</summary>
    public static bool HasMember(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            if (name == ".ctor" || name == ".cctor")
            {
                if (t.GetConstructors(AnyDeclared).Length > 0 || t.IsValueType || t.IsAbstract && t.IsSealed) return true;
                continue;
            }
            if (t.GetMember(name, AnyDeclared).Length > 0) return true;
        }
        if (name == ".ctor" || name == ".cctor") return false;
        foreach (var i in type.GetInterfaces())
            if (i.GetMember(name, AnyDeclared).Length > 0) return true;
        return false;
    }
}
