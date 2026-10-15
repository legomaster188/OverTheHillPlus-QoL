using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OverTheHillPlus.Features.Public.Filter;

/// <summary>
/// Swear and slur filter for text this mod publishes to other players: custom server names (shown in everyone's
/// server browser) and the host's join message. Text that fails is refused, never silently censored, so the menu can
/// say why and the game's own default is used instead.
///
/// How a text is checked:
/// 1. Rich-text tags, zero-width/format characters and accents are removed; full-width, small-caps, Cyrillic and
///    Greek look-alike letters are folded to plain Latin letters; everything is lower-cased.
/// 2. The text is split into words. Each word is also checked with in-word separators removed ("f.u.c.k",
///    "f-u-c-k"), split at separators / CamelCase / letter-digit boundaries ("BigAssTruck", "big_ass_truck"), and runs
///    of three or more single letters are joined ("f u c k").
/// 3. Every list word is a pattern where each letter also accepts its l33t forms (4/@ = a, 3 = e, 1/!/| = i,
///    1/| = l, 0 = o, 5/$ = s, 7/+ = t, v = u, 8 = b, 9/6 = g), repeated letters ("fuuuck") and, after the first
///    letter, a few * or # ("f*ck").
/// 4. Scunthorpe problem: short or ambiguous words only match a whole word ("ass" never matches "class", "cock"
///    never "peacock"), and words matched inside longer words have an allow-list ("Scunthorpe", "therapist").
///    A match must also contain at least one real letter, so plain numbers like "455" are fine.
///
/// The word list is stored ROT13-encoded so the source reads cleanly; it is decoded once at start-up.
/// </summary>
internal static class NameFilter
{
    // ---- Word list (ROT13). Format "word:CM": C = S (slur/hate, always blocked) or P (profanity);
    // M = A (anywhere inside a word, with the allow-list), W (whole word + plural), V (whole word + verb/adjective endings).
    private static readonly string[] Packed =
    {
        "avttre:SA", "avttn:SA", "avddn:SA", "avddre:SA", "avtthu:SA", "wvtnobb:SA", "jrgonpx:SA", "enturnq:SA",
        "gbjryurnq:SA", "mvccreurnq:SA", "erqfxva:SA", "furznyr:SA", "snttbg:SA", "sntt:SA", "snt:SW", "qlxr:SW",
        "pbba:SW", "qnexvr:SW", "qnexl:SW", "fcvp:SW", "fcvpx:SW", "ornare:SW", "puvax:SW", "tbbx:SW",
        "xvxr:SW", "xlxr:SW", "urro:SW", "cnxv:SW", "jbt:SW", "tlccb:SW", "tlcb:SW", "genaal:SW",
        "genaavr:SW", "ergneq:SA", "gneq:SW", "fcnm:SW", "ubaxl:SW", "fdhnj:SW", "puvanzna:SW", "uvgyre:SA",
        "anmv:SW", "fvrturvy:SA", "xxx:SW", "xlf:SW", "shpx:PA", "shpp:PA", "cuhpx:PA", "shx:PW",
        "shxva:PW", "shxvat:PW", "shxx:PA", "shd:PW", "spx:PV", "spxvat:PW", "sxvat:PW", "fuvg:PA",
        "ovgpu:PA", "phag:PA", "nff:PW", "nefr:PW", "nffubyr:PA", "nefrubyr:PA", "nffung:PA", "qhzonff:PA",
        "wnpxnff:PA", "sngnff:PA", "fznegnff:PA", "onfgneq:PW", "qvpx:PW", "qvpxurnq:PA", "pbpx:PW", "pbpxfhpxre:PA",
        "chffl:PW", "gjng:PA", "jnax:PV", "jnaxre:PW", "cvff:PV", "obyybpxf:PW", "oryyraq:PA", "fyhg:PA",
        "juber:PA", "gubg:PW", "qbhpur:PW", "qbhpuront:PA", "wvmm:PA", "phz:PW", "phzfubg:PA", "qvyqb:PA",
        "cbea:PA", "encr:PV", "encvfg:PA", "cravf:PA", "intvan:PW", "obbo:PW", "obbof:PW", "gvgf:PW",
        "gvggvrf:PW", "fgsh:PW", "tgsb:PW", "zvys:PW", "fxnax:PW", "oybjwbo:PA", "unaqwbo:PA", "sphx:PA", "cuhx:PW", "ovngpu:PA", "ovbgpu:PA", "fulg:PW", "avoon:SW", "xvyylbhefrys:SA",
    };

    // Deliberate trade-offs: "tranny" (car slang for transmission) and "dyke" (as in "Offa's Dyke") stay blocked
    // because their slur use is far more common in player names than the innocent one.

    /// <summary>Ordinary words that contain an "anywhere" word (ROT13, space separated).</summary>
    private const string AllowPacked = "fphagubecr cravfgbar fuvvgnxr fuvgnxr zvfuvg zngfhfuvgn avttneq avttneqyl favttre favttrevat favttrerq favttref gurencvfg gurencvfgf ergneqnag ergneqnagf gjngre gjngpu ergneqre chfuvg fuvgmh";

    // ---- Self-check cases (ROT13 too).
    private static readonly string[] MustBlock =
    {
        "shpx lbh", "S H P X", "s.h.p.x", "shhhhpx", "sipx guvf", "s*px",
        "fu1g freire", "$u!g", "n$$", "ovt_nff_gehpx", "OvtNffGehpx", "avttre",
        "a1tt3e", "avddn", "A V T T R E", "фhpx", "ＦＵＣＫ", "shсx",
        "fuіg", "sh​px", "s<o></o>hpx", "püag", "OVGPURF", "zbgureshpxre",
        "qhzonff perj", "nffubyr", "snt", "snttbgf", "ergneqrq", "xlf",
        "jnaxref", "encrq", "juber ubhfr", "u1gyre", "nff!", "shpp",
        "chffl", "ᴀff", "f u v g", "nff69", "S-H-P-X", "p h a g f",
    };

    private static readonly string[] MustPass =
    {
        "Cynlre'f ebbz", "Fphagubecr Havgrq", "pynff npg", "Onff Ceb", "tenff uvyy", "Cnff gur uvyy",
        "nffnffva perrq", "pbpxgnvy ubhe", "Pbpxcvg ivrj", "crnpbpx", "Unapbpx", "enppbba",
        "pbpbba", "fcvpr", "Qvpxraf snaf", "gurencvfg", "tencrf", "qencr",
        "phphzore", "Avtre evire", "Avtrevn", "favttre", "Cravfgbar", "fuvvgnxr",
        "gvgyr", "pbafgvghgvba", "Fhffrk", "uryyb uryy", "Jnaxry ebgnel", "pbpxrq",
        "pbpxl", "Shxhfuvzn", "snd", "xhag wr", "Wncna", "Zvffvffvccv",
        "nffhzr", "onffvfg", "Zngfhfuvgn", "guvf uvg", "chfu vg", "ovg punve",
        "jub erfg", "bss ebnq perj", "Gvg sbe gng", "tynff", "Uvgpuuvxre", "fxhax",
        "qbphzrag", "nanylfg", "pvephzfgnapr", "fpencref", "Fcvpr tveyf", "phzhyhf pybhqf",
        "Pbbaubhaq", "Ebbz 455", "TeNFF", "PynffNpg", "Zhq & Uvyyf 4k4", "Puvyy Bss-Ebnq Pb-bc",
        "Fhafrg pehvfr", "Fnygjngre", "Zrygjngre", "Fjrrgjngre", "Avtugjngpu", "ergneqre", "ergneqref",
        "Ubaxl Gbax", "Chfuvg", "Fuvgmh",
    };

    private enum Mode { Anywhere, Whole, WholeVerb }

    private sealed class Entry
    {
        public bool Slur;
        public Mode Mode;
        public int Letters;
        public Regex Pattern;
        public Regex PairPattern;
    }

    private static List<Entry> _entries;
    private static string[] _allow;
    private static bool _initFailed;
    private static readonly Dictionary<string, string> Cache = new();
    private static readonly Dictionary<string, string> CacheStrict = new();

    /// <summary>Number of words in the list (0 if the list failed to load).</summary>
    public static int WordCount { get { Init(); return _entries?.Count ?? 0; } }

    /// <summary>True when the text must not be published. <paramref name="reason"/> says why, with the word masked.</summary>
    public static bool IsBlocked(string text, out string reason) => IsBlocked(text, out reason, false);

    private static bool IsBlocked(string text, out string reason, bool allowSwears)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cache = allowSwears ? Cache : CacheStrict;
        if (cache.TryGetValue(text, out reason)) return reason != null;
        try { reason = Evaluate(text, allowSwears); }
        catch (Exception e)
        {
            // Fail closed: a filter error must not let text through unchecked.
            reason = "the word filter hit an error";
            Plugin.Logger.LogWarning("[NameFilter] check failed: " + e.Message);
        }
        if (cache.Count > 128) cache.Clear();
        cache[text] = reason;
        return reason != null;
    }

    // ---------------------------------------------------------------- matching

    private static string Evaluate(string text, bool allowSwears)
    {
        Init();
        if (_entries == null) return _initFailed ? "the word filter failed to load" : null;
        text = HonkyTonk.Replace(text, "honkytonk"); // the music style, not the slur
        // Check the text as rendered (tags removed) AND as typed (brackets as spaces): text inside an invalid tag such
        // as "<word>" is shown literally by TextMeshPro, so it must not be skipped.
        return EvaluateCore(Tags.Replace(text, ""), allowSwears)
            ?? EvaluateCore(text.Replace('<', ' ').Replace('>', ' '), allowSwears);
    }

    private static string EvaluateCore(string text, bool allowSwears)
    {
        var (whole, parts, pairs) = Candidates(text);
        foreach (var c in whole)
            if (c == "1488") return "it contains a hate code"; // stand-alone number only, so "Room 14880" is fine
        foreach (var e in _entries)
        {
            if (allowSwears && !e.Slur) continue;
            if (e.Mode == Mode.Anywhere)
            {
                foreach (var c in whole)
                {
                    string hit = FindAnywhere(e, c);
                    if (hit != null) return Describe(e, hit);
                }
            }
            else
            {
                foreach (var c in whole)
                    if (WholeMatch(e, c)) return Describe(e, c);
                foreach (var c in parts)
                    if (WholeMatch(e, c)) return Describe(e, c);
            }
            // "fu ck", "sh it", "nig ger": two neighbouring words that together are exactly a list word.
            foreach (var c in pairs)
            {
                var m = e.PairPattern.Match(c);
                if (m.Success && Plausible(e, m.Value)) return Describe(e, c);
            }
        }
        return null;
    }

    private static string Describe(Entry e, string matched)
    {
        string shown = Mask(matched);
        return e.Slur ? $"it contains a slur ('{shown}')" : $"it contains a swear word ('{shown}')";
    }

    private static string Mask(string s)
    {
        s = s.TrimEnd('!', '?');
        if (s.Length <= 1) return s;
        return s[0] + new string('*', Math.Min(s.Length - 1, 12));
    }

    private static string FindAnywhere(Entry e, string candidate)
    {
        var m = e.Pattern.Match(candidate);
        while (m.Success)
        {
            if (Plausible(e, m.Value) && !Allowed(candidate, m.Index, m.Length)) return m.Value;
            if (m.Index + 1 >= candidate.Length) break;
            m = e.Pattern.Match(candidate, m.Index + 1);
        }
        return null;
    }

    private static bool WholeMatch(Entry e, string candidate)
    {
        if (candidate.Length == 0) return false;
        var m = e.Pattern.Match(candidate);
        return m.Success && Plausible(e, m.Value);
    }

    /// <summary>At least one real letter, the first character a letter (or l33t), and no more than half wildcards.</summary>
    private static bool Plausible(Entry e, string matched)
    {
        int letters = 0, wild = 0;
        foreach (char ch in matched)
        {
            if (ch >= 'a' && ch <= 'z') letters++;
            else if (ch == '*' || ch == '#') wild++;
        }
        return letters > 0 && wild <= e.Letters / 2 && matched.Length > 0 && matched[0] != '*' && matched[0] != '#';
    }

    /// <summary>True when an allow-listed ordinary word in the candidate covers the match.</summary>
    private static bool Allowed(string candidate, int start, int length)
    {
        foreach (var a in _allow)
        {
            int at = candidate.IndexOf(a, StringComparison.Ordinal);
            while (at >= 0)
            {
                if (at <= start && at + a.Length >= start + length) return true;
                at = candidate.IndexOf(a, at + 1, StringComparison.Ordinal);
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- normalisation

    private const string Leet = "@$!|+*#";

    private static bool IsWordChar(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || Leet.IndexOf(c) >= 0;

    /// <summary>
    /// Whole-word candidates (each word with separators removed, plus joined single-letter runs) and part candidates
    /// (words split at separators, CamelCase and letter/digit boundaries).
    /// </summary>
    private static (List<string> whole, List<string> parts, List<string> pairs) Candidates(string text)
    {
        var chars = Fold(text);
        var whole = new List<string>();
        var parts = new List<string>();
        var pairs = new List<string>();
        string prevWord = null;
        var singles = new StringBuilder();
        int singleRun = 0;

        void FlushSingles()
        {
            if (singleRun >= 3) whole.Add(singles.ToString());
            singles.Clear();
            singleRun = 0;
        }

        int i = 0;
        while (i < chars.Count)
        {
            if (char.IsWhiteSpace(chars[i].c)) { i++; continue; }
            int start = i;
            while (i < chars.Count && !char.IsWhiteSpace(chars[i].c)) i++;

            var joined = new StringBuilder();
            var part = new StringBuilder();
            for (int k = start; k < i; k++)
            {
                var (c, upper) = chars[k];
                if (!IsWordChar(c)) { Flush(parts, part); continue; }
                if (part.Length > 0)
                {
                    char prev = part[part.Length - 1];
                    // CamelCase: an upper-case letter followed by a lower-case one starts a new part
                    // ("BigAssTruck", "BIGAss"), but "GrASS" stays whole.
                    bool nextLower = k + 1 < i && !chars[k + 1].upper && IsLetter(chars[k + 1].c);
                    bool camel = upper && nextLower && IsLetter(prev) && IsLetter(c);
                    bool digitEdge = (IsDigit(prev) && IsLetter(c)) || (IsLetter(prev) && IsDigit(c));
                    if (camel || digitEdge) Flush(parts, part);
                }
                part.Append(c);
                joined.Append(c);
            }
            Flush(parts, part);

            if (joined.Length == 0) continue;
            string word = joined.ToString();
            whole.Add(word);
            if (prevWord != null) pairs.Add(prevWord + word);
            prevWord = word;
            if (word.Length == 1) { singles.Append(word); singleRun++; }
            else FlushSingles();
        }
        FlushSingles();
        return (whole, parts, pairs);
    }

    private static bool IsLetter(char c) => c >= 'a' && c <= 'z';
    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static void Flush(List<string> parts, StringBuilder part)
    {
        if (part.Length > 0) parts.Add(part.ToString());
        part.Clear();
    }

    private static readonly Regex Tags = new("<[^<>]{0,64}>", RegexOptions.CultureInvariant);
    private static readonly Regex HonkyTonk = new(@"h[o0]nky[\s_\-]*t[o0]nk", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Strips invisible characters/accents and folds look-alikes; keeps whether each letter was upper-case.</summary>
    private static List<(char c, bool upper)> Fold(string text)
    {
        string decomposed = text;
        try { decomposed = text.Normalize(NormalizationForm.FormKD); }
        catch { /* invariant-globalisation runtimes: the manual table below still covers the common cases */ }

        var result = new List<(char, bool)>(decomposed.Length);
        for (int i = 0; i < decomposed.Length; i++)
        {
            char raw = decomposed[i];
            // Enclosed / squared letters and regional indicators (U+1F130..U+1F189, U+1F1E6..U+1F1FF) are surrogate
            // pairs that NFKD leaves alone: fold them to a..z.
            if (char.IsHighSurrogate(raw) && i + 1 < decomposed.Length && char.IsLowSurrogate(decomposed[i + 1]))
            {
                int cp = char.ConvertToUtf32(raw, decomposed[i + 1]);
                int letter = EnclosedLetter(cp);
                if (letter >= 0) { result.Add(((char)('a' + letter), true)); i++; continue; }
            }
            var cat = CharUnicodeInfo.GetUnicodeCategory(raw);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.Format) continue;
            if (cat == UnicodeCategory.Control && !char.IsWhiteSpace(raw)) continue;
            char ch = raw;
            if (ch >= '！' && ch <= '～') ch = (char)(ch - 0xFEE0); // full-width ASCII
            bool upper = char.IsUpper(ch);
            string mapped = Confusable(ch);
            foreach (char m in mapped) result.Add((m, upper));
        }
        return result;
    }

    /// <summary>0..25 for an enclosed/squared Latin capital or a regional indicator letter, else -1.</summary>
    private static int EnclosedLetter(int cp)
    {
        if (cp >= 0x1F130 && cp <= 0x1F149) return cp - 0x1F130; // squared
        if (cp >= 0x1F150 && cp <= 0x1F169) return cp - 0x1F150; // negative circled
        if (cp >= 0x1F170 && cp <= 0x1F189) return cp - 0x1F170; // negative squared
        if (cp >= 0x1F1E6 && cp <= 0x1F1FF) return cp - 0x1F1E6; // regional indicators
        return -1;
    }

    private static readonly Dictionary<char, string> LookAlikes = BuildLookAlikes();

    private static string Confusable(char c)
    {
        if (c < 128) return c >= 'A' && c <= 'Z' ? ((char)(c + 32)).ToString() : c.ToString();
        if (LookAlikes.TryGetValue(c, out var s)) return s;
        char lower = char.ToLowerInvariant(c);
        return LookAlikes.TryGetValue(lower, out s) ? s : lower.ToString();
    }

    private static Dictionary<char, string> BuildLookAlikes()
    {
        var d = new Dictionary<char, string>();
        void Map(string from, string to) { foreach (char f in from) d[f] = to; }
        // Cyrillic
        Map("а", "a"); Map("вьъ", "b"); Map("еёєэ", "e"); Map("кќ", "k");
        Map("м", "m"); Map("нһ", "h"); Map("о", "o"); Map("р", "p"); Map("с", "c");
        Map("т", "t"); Map("уўү", "y"); Map("х", "x"); Map("ії", "i"); Map("ј", "j");
        Map("ѕ", "s"); Map("ԁ", "d"); Map("ӏ", "l"); Map("фғ", "f"); Map("ԛ", "q");
        Map("ԝшщ", "w"); Map("г", "r"); Map("пи", "n"); Map("ц", "u"); Map("з", "3");
        // Greek
        Map("αά", "a"); Map("β", "b"); Map("γ", "y"); Map("δ", "d"); Map("εέ", "e");
        Map("ζ", "z"); Map("ηήπ", "n"); Map("ιίϊ", "i"); Map("κ", "k");
        Map("μυύϋ", "u"); Map("ν", "v"); Map("οόθσ", "o"); Map("ρ", "p");
        Map("ς", "s"); Map("τ", "t"); Map("χ", "x"); Map("ωώ", "w"); Map("φ", "f");
        // Latin extras and small capitals
        Map("ıɩɪ", "i"); Map("łʟ", "l"); Map("ø", "o"); Map("đðᴅ", "d");
        Map("ß", "ss"); Map("æ", "ae"); Map("œ", "oe"); Map("ƒꜰ", "f"); Map("ɑᴀ", "a");
        Map("ɡɢ", "g"); Map("ʋᴜ", "u"); Map("ʙ", "b"); Map("ᴄ¢", "c"); Map("ᴇ€", "e");
        Map("ʜ", "h"); Map("ᴊ", "j"); Map("ᴋ", "k"); Map("ᴍ", "m"); Map("ɴ", "n"); Map("ᴏ", "o");
        Map("ᴘþ", "p"); Map("ʀ", "r"); Map("ꜱ§", "s"); Map("ᴛ", "t"); Map("ᴠ", "v");
        Map("ᴡ", "w"); Map("ʏ", "y"); Map("ᴢ", "z"); Map("¡", "i");
        // Accented Latin, for runtimes without Unicode normalisation
        Map("àáâãäåāăą", "a"); Map("çćč", "c");
        Map("èéêëēėęě", "e"); Map("ìíîïīį", "i");
        Map("ñńň", "n"); Map("òóôõöōő", "o"); Map("śšş", "s");
        Map("ùúûüūůűų", "u"); Map("ýÿ", "y"); Map("źżž", "z");
        Map("ťţ", "t"); Map("ğ", "g"); Map("ř", "r"); Map("ď", "d"); Map("ľĺ", "l");

        // Upper-case look-alikes too: runtimes in invariant-globalisation mode only lower-case ASCII. Cyrillic capitals
        // look like their small letters; Greek capitals look like Latin capitals, so they get their own table.
        foreach (var kv in new List<KeyValuePair<char, string>>(d))
        {
            char c = kv.Key;
            if (c >= 'а' && c <= 'я') d.TryAdd((char)(c - 0x20), kv.Value);
            else if (c >= 'ѐ' && c <= 'џ') d.TryAdd((char)(c - 0x50), kv.Value);
        }
        Map("Α", "a"); Map("Β", "b"); Map("ΕΣ", "e"); Map("Ζ", "z"); Map("Η", "h");
        Map("Ι", "i"); Map("Κ", "k"); Map("Μ", "m"); Map("Ν", "n"); Map("ΟΘ", "o");
        Map("Ρ", "p"); Map("Τ", "t"); Map("Υ", "y"); Map("Χ", "x"); Map("Φ", "f");
        return d;
    }

    // ---------------------------------------------------------------- word list

    private static readonly Dictionary<char, string> LetterClass = new()
    {
        ['a'] = "a4@", ['b'] = "b8", ['e'] = "e3", ['g'] = "g96", ['i'] = "i1!|", ['l'] = "l1|",
        ['o'] = "o0", ['s'] = "s5$", ['t'] = "t7+", ['u'] = "uv",
    };

    private static void Init()
    {
        if (_entries != null || _initFailed) return;
        try
        {
            var list = new List<Entry>(Packed.Length);
            foreach (var packed in Packed)
            {
                // Only the word is ROT13; the two flag letters after ':' are plain.
                int colon = packed.LastIndexOf(':');
                list.Add(Build(Rot13(packed.Substring(0, colon)), packed.Substring(colon + 1)));
            }
            _allow = Rot13(AllowPacked).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _entries = list;
        }
        catch (Exception e)
        {
            _initFailed = true;
            Plugin.Logger.LogError("[NameFilter] word list failed to load: " + e);
        }
    }

    /// <summary>Turns a list word into its pattern: per-letter l33t classes, repeats, wildcards after the first letter.</summary>
    private static Entry Build(string word, string flags)
    {
        bool slur = flags[0] == 'S';
        var mode = flags[1] switch { 'A' => Mode.Anywhere, 'V' => Mode.WholeVerb, _ => Mode.Whole };
        var sb = new StringBuilder();
        int idx = 0;
        while (idx < word.Length)
        {
            char c = word[idx];
            int run = 1;
            while (idx + run < word.Length && word[idx + run] == c) run++;
            string cls = LetterClass.TryGetValue(c, out var s) ? s : c.ToString();
            if (slur && c == 'i') cls += "l"; // "nlgger": a lower-case L looks like a capital I
            if (idx > 0) cls += "*#";
            sb.Append('[').Append(Regex.Escape(cls).Replace("]", "\\]")).Append(']');
            sb.Append(run == 1 ? "+" : "{" + run + ",}");
            idx += run;
        }
        string body = sb.ToString();
        string pattern = mode switch
        {
            Mode.Anywhere => body,
            Mode.Whole => "^" + body + "(?:e?s)?[!?]*$",
            _ => "^" + body + "(?:e?s|e?d|ers?|ing|in|e?y|ies|ie)?[!?]*$",
        };
        return new Entry
        {
            Slur = slur, Mode = mode, Letters = word.Length,
            Pattern = new Regex(pattern, RegexOptions.CultureInvariant),
            PairPattern = new Regex("^" + body + "(?:e?s|e?d|ers?|ing|in|e?y|ies|ie)?[!?]*$", RegexOptions.CultureInvariant),
        };
    }

    /// <summary>ROT13 on ASCII letters only; everything else (digits, symbols, look-alikes) is left as is.</summary>
    private static string Rot13(string s)
    {
        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++)
        {
            char c = a[i];
            if (c >= 'a' && c <= 'z') a[i] = (char)('a' + (c - 'a' + 13) % 26);
            else if (c >= 'A' && c <= 'Z') a[i] = (char)('A' + (c - 'A' + 13) % 26);
        }
        return new string(a);
    }

    // ---------------------------------------------------------------- self-check

    /// <summary>Runs the built-in cases in strict mode. Returns a status line (prefix "Failed" when anything is wrong).</summary>
    public static string SelfCheck()
    {
        Init();
        if (_entries == null) return "Failed: the word list did not load (see log).";
        int ok = 0, total = 0;
        var problems = new List<string>();
        foreach (var packed in MustBlock)
        {
            total++;
            string text = Rot13(packed);
            if (IsBlocked(text, out _, false)) ok++;
            else problems.Add($"not blocked: '{Mask(text)}' ({text.Length} chars)");
        }
        foreach (var packed in MustPass)
        {
            total++;
            string text = Rot13(packed);
            if (!IsBlocked(text, out string why, false)) ok++;
            else problems.Add($"wrongly blocked: '{text}' ({why})");
        }
        foreach (var p in problems) Plugin.Logger.LogWarning("[NameFilter] self-check " + p);
        Plugin.Logger.LogInfo($"[NameFilter] self-check {ok}/{total} ({_entries.Count} words)");
        return problems.Count == 0
            ? $"Name filter self-check passed: {ok}/{total} cases, {_entries.Count} words."
            : $"Failed: name filter self-check {ok}/{total}. First problem: {problems[0]} (all in the log).";
    }
}
