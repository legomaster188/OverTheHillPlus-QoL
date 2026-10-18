using System;
using System.Collections.Generic;
using System.Text;

namespace OverTheHillPlus.Logging;

/// <summary>How much of the debug log is written ([Debug] Level in BepInEx/config/oth.core.cfg).</summary>
public enum DebugLevel
{
    /// <summary>No debug log file, no hang watchdog, no hook timing (the crash trail and the crash report stay on).</summary>
    Off = 0,
    /// <summary>Hangs, frame spikes, slow hooks, errors (each distinct one once, with repeat counts) and crash reports.</summary>
    Errors = 1,
    /// <summary>Errors plus the mods' <c>DebugLog.Info</c> lines.</summary>
    Info = 2,
    /// <summary>Info plus per-hook timing tables every 60 s and the mods' <c>DebugLog.Verbose</c> lines.</summary>
    Verbose = 3,
}

/// <summary>Who an error belongs to.</summary>
internal enum Blame
{
    /// <summary>No frame of ours in the stack and none of our hooks or patches was running.</summary>
    Game,
    /// <summary>A frame of one of our mods is in the stack.</summary>
    Ours,
    /// <summary>Only game frames, but one of our hooks or guarded patches was running when it happened (e.g. the game's
    /// OnEnable running on an object our code just made).</summary>
    OursByTrail,
}

/// <summary>The result of <see cref="StackText.Classify"/>.</summary>
internal readonly struct Attribution
{
    public readonly Blame Blame;
    /// <summary>The mod (null for <see cref="Blame.Game"/>; "?" for ours without a known mod).</summary>
    public readonly string ModId;
    /// <summary>The first frame of the stack ("" when there is none).</summary>
    public readonly string TopFrame;
    /// <summary>The first frame of ours (null when there is none).</summary>
    public readonly string OurFrame;

    public Attribution(Blame blame, string modId, string topFrame, string ourFrame)
    {
        Blame = blame;
        ModId = modId;
        TopFrame = topFrame ?? "";
        OurFrame = ourFrame;
    }

    /// <summary>"the game's own", "ours: camera" or "ours by trail: camera".</summary>
    public string Describe() => Blame switch
    {
        Blame.Ours => "ours: " + (ModId ?? "?"),
        Blame.OursByTrail => "ours by trail: " + (ModId ?? "?"),
        _ => "the game's own",
    };
}

/// <summary>
/// Reading stack traces and log text (pure, no game types): the frames of a .NET or Unity stack, which mod a frame or
/// a crash-trail step belongs to, the exception type in a log line, and a message with its numbers taken out (so the
/// same error with another object id counts as a repeat).
/// </summary>
internal static class StackText
{
    /// <summary>Root namespace of every mod of ours.</summary>
    public const string OurNamespace = "OverTheHillPlus";

    // Core's dispatch and logging code: in a stack it sits under the mod's frame, so it never names the mod.
    private static readonly string[] Plumbing =
    {
        "OverTheHillPlus.Logging.", "OverTheHillPlus.Guard", "OverTheHillPlus.DebugLog.", "OverTheHillPlus.CrashTrail.",
        "OverTheHillPlus.ErrorTap.", "OverTheHillPlus.Hooks.", "OverTheHillPlus.Features.Registry.", "OverTheHillPlus.CoreBehaviour.",
    };

    /// <summary>
    /// The method name of one stack line, as "Namespace.Type.Method", or null when the line is not a frame. Takes .NET
    /// lines ("   at Ns.Type.Method(Args) in file:line 3") and Unity ones ("Ns.Type:Method(Args)",
    /// "FunTextTMP.OnEnable () (at file:0)").
    /// </summary>
    public static string FrameName(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        string t = line.Trim();
        if (t.StartsWith("at ", StringComparison.Ordinal)) t = t.Substring(3).TrimStart();
        if (t.Length == 0 || t.StartsWith("---", StringComparison.Ordinal)) return null;
        int paren = t.IndexOf('(');
        if (paren <= 0) return null;
        string name = t.Substring(0, paren).TrimEnd();
        int bracket = name.IndexOf('[');
        if (bracket > 0) name = name.Substring(0, bracket);
        if (name.Length == 0 || name.IndexOf(' ') >= 0 || name.IndexOf('\t') >= 0) return null;
        // Unity writes "Type:Method"; an "Exception:" message line has a space after its colon and was refused above.
        name = name.Replace(':', '.');
        return name.Trim('.').Length == 0 ? null : name;
    }

    /// <summary>Every frame of a stack trace, top first.</summary>
    public static IEnumerable<string> Frames(string stack)
    {
        if (string.IsNullOrEmpty(stack)) yield break;
        int start = 0;
        while (start < stack.Length)
        {
            int nl = stack.IndexOf('\n', start);
            if (nl < 0) nl = stack.Length;
            string f = FrameName(stack.Substring(start, nl - start));
            if (f != null) yield return f;
            start = nl + 1;
        }
    }

    /// <summary>The first frame of a stack trace, or "".</summary>
    public static string TopFrame(string stack)
    {
        foreach (var f in Frames(stack)) return f;
        return "";
    }

    /// <summary>True for core's dispatch and logging frames (they run every mod's code).</summary>
    public static bool IsPlumbing(string frame)
    {
        if (frame == null) return false;
        foreach (var p in Plumbing)
            if (frame.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Which mod a frame belongs to: the longest prefix of "Ns.Type.Method" that <paramref name="typeToMod"/> knows as
    /// a type (nested types written with dots), else "?" for any frame in our namespace, else null (not ours).
    /// </summary>
    public static string ModOfFrame(string frame, Func<string, string> typeToMod)
    {
        if (string.IsNullOrEmpty(frame)) return null;
        if (typeToMod != null)
        {
            string name = frame;
            int dot = name.LastIndexOf('.');
            while (dot > 0)
            {
                name = name.Substring(0, dot);
                string mod = typeToMod(name);
                if (mod != null) return mod;
                dot = name.LastIndexOf('.');
            }
        }
        return frame.StartsWith(OurNamespace + ".", StringComparison.Ordinal) ? "?" : null;
    }

    /// <summary>
    /// Whose error it is: the first (topmost) frame of ours names the mod; with no frame of ours, the mod whose hook
    /// or guarded patch was running (<paramref name="runningMod"/>) is blamed "by trail"; else it is the game's own.
    /// Core's dispatch frames alone blame <paramref name="runningMod"/>, or core.
    /// </summary>
    public static Attribution Classify(string stack, Func<string, string> typeToMod, string runningMod)
    {
        string top = null;
        bool plumbing = false;
        foreach (var f in Frames(stack))
        {
            top ??= f;
            if (IsPlumbing(f)) { plumbing = true; continue; }
            string mod = ModOfFrame(f, typeToMod);
            if (mod != null) return new Attribution(Blame.Ours, mod, top, f);
        }
        if (plumbing) return new Attribution(Blame.Ours, string.IsNullOrEmpty(runningMod) ? "core" : runningMod, top, null);
        if (!string.IsNullOrEmpty(runningMod)) return new Attribution(Blame.OursByTrail, runningMod, top, null);
        return new Attribution(Blame.Game, null, top, null);
    }

    /// <summary>The first exception type named in a log text ("System.NullReferenceException", "Il2CppException"), or null.</summary>
    public static string ExceptionType(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int at = 0;
        while (true)
        {
            int i = text.IndexOf("Exception", at, StringComparison.Ordinal);
            if (i < 0) return null;
            int end = i + "Exception".Length;
            at = end;
            // Must end the word ("Exception:" / "Exception " / end), not be part of e.g. "ExceptionTap".
            if (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) continue;
            int start = i;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '.' || text[start - 1] == '_')) start--;
            string name = text.Substring(start, end - start).Trim('.');
            if (name.Length > "Exception".Length || name == "Exception") return name;
        }
    }

    /// <summary>
    /// The first line of a message with every run of digits (and 0x hex numbers) as '#', cut to
    /// <paramref name="max"/> characters: "object 123 at 0x7FF" and "object 45 at 0x1A" count as the same error.
    /// </summary>
    public static string Normalize(string message, int max = 160)
    {
        if (string.IsNullOrEmpty(message)) return "";
        int nl = message.IndexOf('\n');
        string line = (nl >= 0 ? message.Substring(0, nl) : message).Trim();
        var sb = new StringBuilder(Math.Min(line.Length, max + 8));
        for (int i = 0; i < line.Length && sb.Length < max; i++)
        {
            char c = line[i];
            if (c == '0' && i + 1 < line.Length && (line[i + 1] == 'x' || line[i + 1] == 'X'))
            {
                i += 2;
                while (i < line.Length && Uri.IsHexDigit(line[i])) i++;
                i--;
                sb.Append('#');
            }
            else if (char.IsDigit(c))
            {
                while (i + 1 < line.Length && char.IsDigit(line[i + 1])) i++;
                sb.Append('#');
            }
            else sb.Append(c == '\r' || c == '\t' ? ' ' : c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Which mod a crash-trail step belongs to: "tick Owner (camera)" names it in brackets at the end, "camera+: drone
    /// check" or "vera: build template" before the colon (letters and digits only, lower case). Null when neither is
    /// a mod id <paramref name="isModId"/> knows.
    /// </summary>
    public static string ModOfStep(string step, Func<string, bool> isModId)
    {
        if (string.IsNullOrEmpty(step) || isModId == null) return null;
        string s = step.Trim();
        if (s.EndsWith(")", StringComparison.Ordinal))
        {
            int open = s.LastIndexOf('(');
            if (open >= 0)
            {
                string inner = s.Substring(open + 1, s.Length - open - 2).Trim();
                if (inner.Length > 0 && isModId(inner)) return inner;
            }
        }
        int colon = s.IndexOf(':');
        if (colon > 0)
        {
            string head = s.Substring(0, colon).Trim().ToLowerInvariant();
            var sb = new StringBuilder(head.Length);
            foreach (char c in head)
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
            string id = sb.ToString();
            if (id.Length > 0 && isModId(id)) return id;
        }
        return null;
    }
}
