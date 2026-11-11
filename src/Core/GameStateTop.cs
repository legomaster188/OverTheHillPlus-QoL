#if MELON
using Il2CppFunCore.Managers;
using Il2CppFunCore.StateMachine;
#else
using FunCore.Managers;
using FunCore.StateMachine;
#endif

namespace OverTheHillPlus;

internal static partial class GameState
{
    /// <summary>The top of the game's state stack, or null when there is none: no manager yet, or an empty stack. The game's own
    /// PeekState throws "Stack empty" on the first frames (scene 0_Entry, before the main menu is pushed), which every caller
    /// then logged as an error, twenty times a minute until the menu came up.</summary>
    public static State TopState() => TopState(HybridSingleton<GameStateManager>.Instance);

    /// <summary>As <see cref="TopState()"/>, for a caller that already holds the manager.</summary>
    public static State TopState(GameStateManager gsm)
    {
        if (gsm == null) return null;
        var stack = gsm.gameStateMachine?.stateStack;
        if (stack == null || stack.Count == 0) return null;
        return gsm.PeekState();
    }
}
