namespace OverTheHillPlus.Core.Shared;

/// <summary>When a pending write is due (pure, unit-tested).</summary>
internal static class SaveSchedule
{
    /// <summary>The time the write is due after a change: a first change sets it, a later one can only bring it nearer.</summary>
    public static float Next(bool alreadyDirty, float dueAt, float now, float delay)
    {
        float at = now + delay;
        return alreadyDirty && dueAt < at ? dueAt : at;
    }
}
