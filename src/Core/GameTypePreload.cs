using System;
using System.Diagnostics;
using System.Reflection;
#if MELON
using Il2Cpp;
using Il2CppFunCore.StateMachine;
#else
using FunCore.StateMachine;
#endif

namespace OverTheHillPlus.Core;

/// <summary>
/// Loads the game's UI and state wrapper classes while nothing has called them yet.
///
/// <para>The .NET 6 runtime crashes (c0000005 in ClassLoader::PropagateCovariantReturnMethodImplSlots) when it first loads
/// a class that overrides a virtual method whose slot on the parent no longer maps back to a method, which is what a
/// slot looks like once tiered compilation or a patch has moved on from the plain stub. So UIOptionSelector, loaded for
/// the first time by the MODS screen, could take the whole game down, depending on what the menus had called before it.
/// At start-up every slot is still plain and the load is safe, and a class is only ever loaded once.</para>
/// </summary>
internal static class GameTypePreload
{
    internal static void Run()
    {
        var clock = Stopwatch.StartNew();
        int types = 0, failed = 0;
        foreach (var assembly in new[] { typeof(UISelector).Assembly, typeof(State).Assembly, typeof(UnityEngine.UI.Selectable).Assembly })
        {
            try { types += assembly.GetTypes().Length; }
            catch (ReflectionTypeLoadException e)
            {
                types += e.Types.Length;
                foreach (var t in e.Types) if (t == null) failed++;
            }
            catch (Exception e) { CorePlugin.Logger?.LogWarning("[Core] preloading " + assembly.GetName().Name + ": " + e.Message); }
        }
        CorePlugin.Logger?.LogInfo($"[Core] preloaded {types} game classes in {clock.ElapsedMilliseconds} ms ({failed} failed).");
    }
}
