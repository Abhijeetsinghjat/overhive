using System.Text.RegularExpressions;
using Amc.Core.Security;

namespace Amc.Core.Verification;

/// <summary>
/// Finds a small set of checkable claims in an agent's own text ("all tests pass", "I committed the
/// changes", "created `src/x.ts`", "the build succeeds"). English only. Built for precision over
/// recall: a sentence that hedges, asks, negates or plans ("if the build succeeds", "tests may pass
/// once…", "I'll commit") yields nothing. Quoted code blocks are ignored.
/// </summary>
public static partial class ClaimExtractor
{
    public const int MaxExcerpt = 240;
    private const int MaxPerText = 10;

    public static IReadOnlyList<ExtractedClaim> Extract(string? text)
    {
        var result = new List<ExtractedClaim>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var seen = new HashSet<(ClaimKind, string?)>();
        var clean = InlineCodeWithSpaces().Replace(FencedCode().Replace(text, "\n"), " … ");
        foreach (var raw in SentenceSplit().Split(clean))
        {
            var sentence = ListMarker().Replace(raw.Trim(), "");
            if (sentence.Length < 4 || sentence.StartsWith('>') || sentence.EndsWith('?')) continue;
            foreach (var (kind, path) in Find(sentence))
            {
                if (!seen.Add((kind, path))) continue;
                result.Add(new ExtractedClaim(kind, Excerpt(sentence), path));
                if (result.Count >= MaxPerText) return result;
            }
        }
        return result;
    }

    private static IEnumerable<(ClaimKind, string?)> Find(string sentence)
    {
        foreach (var re in TestPatterns)
            if (Accept(sentence, re.Match(sentence), strictTail: true)) { yield return (ClaimKind.TestsPass, null); break; }
        foreach (var re in BuildPatterns)
            if (Accept(sentence, re.Match(sentence), strictTail: true)) { yield return (ClaimKind.BuildSucceeds, null); break; }
        foreach (var re in CommitPatterns)
            if (Accept(sentence, re.Match(sentence), strictTail: false)) { yield return (ClaimKind.Committed, null); break; }
        foreach (Match m in FileClaim().Matches(sentence))
        {
            if (!Accept(sentence, m, strictTail: false) || CleanPath(m.Groups["path"].Value) is not { } path) continue;
            var kind = m.Groups["verb"].Value.ToLowerInvariant() switch
            {
                "created" or "added" => ClaimKind.FileCreated,
                "deleted" or "removed" => ClaimKind.FileDeleted,
                _ => ClaimKind.FileUpdated,
            };
            yield return (kind, path);
        }
    }

    /// <summary>A match counts only if nothing before it in the sentence hedges or negates it (and, for test/build, nothing after it qualifies it).</summary>
    private static bool Accept(string sentence, Match m, bool strictTail)
    {
        if (!m.Success) return false;
        // "Result: all tests pass" — a label before a colon does not qualify what follows it.
        var before = sentence[..m.Index];
        if (Hedge().IsMatch(before[(before.LastIndexOf(':') + 1)..])) return false;
        return !strictTail || !Qualifier().IsMatch(sentence[(m.Index + m.Length)..]);
    }

    /// <summary>Returns a plausible file path, or null. Requires a slash or a file extension; rejects URLs and library names like "Node.js".</summary>
    internal static string? CleanPath(string raw)
    {
        var quoted = raw.StartsWith('`');
        var p = raw.Trim('`').TrimEnd('.', ',', ':', ';', ')', '!', '\'', '"');
        if (p.StartsWith("./")) p = p[2..];
        if (p.Length is < 3 or > 260 || p.Contains("://") || !p.Any(char.IsLetter)) return null;
        if (p.Contains("..") || p.StartsWith('-')) return null;
        var hasExt = Extension().IsMatch(p);
        if (!p.Contains('/') && !hasExt) return null;
        if (!quoted && !p.Contains('/') && LibraryName().IsMatch(p)) return null;
        if (p.StartsWith("e.g", StringComparison.OrdinalIgnoreCase) || p.StartsWith("i.e", StringComparison.OrdinalIgnoreCase)) return null;
        return p;
    }

    private static string Excerpt(string sentence)
    {
        var s = Redactor.Redact(WhiteSpace().Replace(sentence, " ").Trim());
        return s.Length <= MaxExcerpt ? s : s[..MaxExcerpt] + "…";
    }

    // ---- patterns -------------------------------------------------------------------

    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex[] TestPatterns =
    [
        new(@"\b(?:all\s+(?:of\s+the\s+)?|the\s+)?(?:\d+\s+)?(?:(?:unit|integration|e2e|end-to-end|existing|new|remaining|relevant)\s+)?tests?\s+(?:now\s+|are\s+|all\s+|still\s+)*(?:pass(?:es|ed|ing)?|green|succeed(?:s|ed)?)\b", I),
        new(@"\btest\s+suite\s+(?:now\s+|is\s+|still\s+)*(?:pass(?:es|ed|ing)?|green)\b", I),
        new(@"\b[1-9]\d*\s+passed\s*[,;]?\s*0\s+failed\b", I),
        new(@"\b0\s+failed\s*[,;]?\s*[1-9]\d*\s+passed\b", I),
        new(@"\bfailed:\s*0\s*,\s*passed:\s*[1-9]\d*", I),
    ];

    private static readonly Regex[] BuildPatterns =
    [
        new(@"\b(?:the\s+)?(?:[\w.-]+\s+)?build\s+(?:now\s+|still\s+)*(?:succeeds|succeeded|passes|passed|is\s+(?:green|passing|clean|successful)|was\s+successful|completed\s+successfully|works)\b", I),
        new(@"\b(?:builds|compiles)\s+(?:cleanly|successfully|fine|without\s+(?:any\s+)?(?:errors|warnings))\b", I),
        new(@"\b(?:built|compiled)\s+successfully\b", I),
    ];

    private static readonly Regex[] CommitPatterns =
    [
        new(@"\b(?:I|we)(?:'ve|’ve|\s+have)?\s+(?:just\s+|also\s+|now\s+|already\s+)*committed\b", I),
        new(@"\b(?:created|made|added)\s+(?:a|the|one|two|\d+)\s+(?:new\s+)?commits?\b", I),
        new(@"\bcommitted\s+(?:the|these|those|all|my|your|both|this)\s+(?:[\w-]+\s+)?(?:changes?|work|fix(?:es)?|files?|updates?|edits?)\b", I),
        new(@"\b(?:changes?|work|everything|it|fix)\s+(?:has|have|is|are|was|were)\s+(?:been\s+)?(?:now\s+)?committed\b", I),
        new(@"^(?:\W+\s*)?committed\b", I),
    ];

    [GeneratedRegex(@"\b(?<verb>created|added|wrote|updated|modified|edited|changed|rewrote|refactored|deleted|removed)\s+(?:the\s+|a\s+)?(?:new\s+)?(?:(?:test\s+|config\s+)?file\s+)?(?:(?:called|named|at)\s+)?(?<path>`[^`\s]+`|[\w.~/@+-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileClaim();

    /// <summary>Words before a claim that make it conditional, planned, uncertain or negated.</summary>
    [GeneratedRegex(@"\b(?:if|once|when|whenever|whether|unless|until|should|shouldn't|may|might|could|would|will|won't|can|cannot|can't|must|need|needs|let's|let\s+me|i'll|we'll|you'll|going\s+to|try|trying|make\s+sure|ensure|verify|check|expect|hopefully|probably|likely|assuming|suppose|not|no|never|don't|doesn't|didn't|isn't|aren't|wasn't|weren't|haven't|hasn't|without|so\s+that)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Hedge();

    /// <summary>Words after a test/build claim that qualify it ("all tests pass except two").</summary>
    [GeneratedRegex(@"\b(?:except|but|apart\s+from|other\s+than|besides|aside\s+from|[1-9]\d*\s+(?:tests?\s+)?(?:fail\w*|errors?))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Qualifier();

    [GeneratedRegex(@"```[\s\S]*?(?:```|$)")]
    private static partial Regex FencedCode();

    [GeneratedRegex(@"`[^`\n]*\s[^`\n]*`")]
    private static partial Regex InlineCodeWithSpaces();

    [GeneratedRegex(@"\n+|(?<=[.!?;])\s+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"^(?:[-*+•]\s+|\d+[.)]\s+|#+\s+|\*\*|[✅✔☑]️?\s*)+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"\.[A-Za-z][A-Za-z0-9]{0,9}$")]
    private static partial Regex Extension();

    /// <summary>"Node.js", "Next.js", "Vue.js": library names, not files.</summary>
    [GeneratedRegex(@"^[A-Z][A-Za-z]*\.js$")]
    private static partial Regex LibraryName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
