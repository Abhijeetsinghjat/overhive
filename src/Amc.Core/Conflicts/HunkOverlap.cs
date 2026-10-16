using Amc.Core.Domain;

namespace Amc.Core.Conflicts;

/// <summary>
/// One hunk of a zero-context (`-U0`) unified diff: <c>@@ -OldStart,OldCount +NewStart,NewCount @@</c>.
/// A pure insertion has OldCount 0 and OldStart = the base line it follows; a pure deletion has NewCount 0.
/// </summary>
public readonly record struct DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount)
{
    /// <summary>First base line touched. For an insertion the (empty) span sits between OldStart and OldStart+1.</summary>
    public int BaseLo => OldCount == 0 ? OldStart + 1 : OldStart;
    /// <summary>Last base line touched (BaseHi &lt; BaseLo for an insertion).</summary>
    public int BaseHi => OldCount == 0 ? OldStart : OldStart + OldCount - 1;

    /// <summary>Lines in the worktree's own file. A deletion is shown at the line it follows (at least 1).</summary>
    public LineSpan NewSpan => NewCount == 0
        ? new LineSpan(Math.Max(NewStart, 1), Math.Max(NewStart, 1))
        : new LineSpan(NewStart, NewStart + NewCount - 1);
}

/// <summary>One file's section of a unified diff.</summary>
public sealed record FileDiff(string? OldPath, string? NewPath, bool NewFile, bool Deleted, bool Binary, List<DiffHunk> Hunks)
{
    public static FileDiff Empty => new(null, null, false, false, false, []);

    /// <summary>Whole file added (untracked or created after the base) with <paramref name="lines"/> lines.</summary>
    public static FileDiff Added(string path, int lines) => new(null, path, true, false, false, [new DiffHunk(0, 0, 1, Math.Max(lines, 1))]);
}

/// <summary>Parses `git diff -U0` output. Pure; tolerant of anything it does not understand.</summary>
public static class HunkParser
{
    public static List<FileDiff> Parse(string diff, int maxHunks = 5000)
    {
        var files = new List<FileDiff>();
        string? oldPath = null, newPath = null;
        bool newFile = false, deleted = false, binary = false, inFile = false;
        var hunks = new List<DiffHunk>();
        void Flush()
        {
            if (inFile) files.Add(new FileDiff(oldPath, newPath, newFile, deleted, binary, hunks));
            oldPath = newPath = null;
            newFile = deleted = binary = false;
            hunks = [];
        }

        var lines = diff.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd('\r');
            if (l.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                inFile = true;
                continue;
            }
            if (!inFile) continue;
            if (l.StartsWith("new file mode", StringComparison.Ordinal)) newFile = true;
            else if (l.StartsWith("deleted file mode", StringComparison.Ordinal)) deleted = true;
            else if (l.StartsWith("rename from ", StringComparison.Ordinal)) oldPath = Unquote(l["rename from ".Length..]);
            else if (l.StartsWith("rename to ", StringComparison.Ordinal)) newPath = Unquote(l["rename to ".Length..]);
            else if (l.StartsWith("Binary files ", StringComparison.Ordinal) || l == "GIT binary patch") binary = true;
            else if (l.StartsWith("--- ", StringComparison.Ordinal)) oldPath ??= Side(l[4..], "a/");
            else if (l.StartsWith("+++ ", StringComparison.Ordinal)) newPath ??= Side(l[4..], "b/");
            else if (l.StartsWith("@@ ", StringComparison.Ordinal) && TryHeader(l, out var h))
            {
                if (hunks.Count < maxHunks) hunks.Add(h);
                // Skip the hunk body by count, so a content line such as "--- a/x" or "diff --git" is never read as a header.
                var remaining = h.OldCount + h.NewCount;
                while (remaining > 0 && i + 1 < lines.Length)
                {
                    var body = lines[i + 1];
                    if (body.StartsWith('\\')) { i++; continue; }
                    if (body.Length == 0 || body[0] is not ('+' or '-' or ' ')) break;
                    i++;
                    remaining--;
                }
            }
        }
        Flush();
        return files;
    }

    /// <summary>The section for <paramref name="path"/> (by new path, then old path), or the first section.</summary>
    public static FileDiff ForPath(IReadOnlyList<FileDiff> files, string path) =>
        files.FirstOrDefault(f => f.NewPath == path) ?? files.FirstOrDefault(f => f.OldPath == path) ?? files.FirstOrDefault() ?? FileDiff.Empty;

    public static bool TryHeader(string line, out DiffHunk hunk)
    {
        hunk = default;
        // @@ -a[,b] +c[,d] @@ optional section heading
        var end = line.IndexOf(" @@", 3, StringComparison.Ordinal);
        if (end < 0) return false;
        var parts = line[3..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0][0] != '-' || parts[1][0] != '+') return false;
        if (!Range(parts[0][1..], out var a, out var b) || !Range(parts[1][1..], out var c, out var d)) return false;
        hunk = new DiffHunk(a, b, c, d);
        return true;
    }

    private static bool Range(string s, out int start, out int count)
    {
        count = 1;
        var comma = s.IndexOf(',');
        if (comma >= 0)
            return int.TryParse(s[..comma], out start) & int.TryParse(s[(comma + 1)..], out count) && start >= 0 && count >= 0;
        return int.TryParse(s, out start) && start >= 0;
    }

    private static string? Side(string s, string prefix)
    {
        s = Unquote(s.TrimEnd('\t'));
        if (s == "/dev/null") return null;
        return s.StartsWith(prefix, StringComparison.Ordinal) ? s[prefix.Length..] : s;
    }

    /// <summary>Git C-quotes paths with special characters ("a/x\ty"); only common escapes are decoded.</summary>
    private static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        return s[1..^1].Replace("\\\"", "\"").Replace("\\t", "\t").Replace("\\n", "\n").Replace("\\\\", "\\");
    }
}

/// <summary>Decides whether two worktrees' changes to one file overlap (pure).</summary>
public static class OverlapMath
{
    /// <summary>Changes separated by fewer than this many unchanged base lines count as overlapping.</summary>
    public const int DefaultMargin = 3;
    public const int MaxSpans = 50;

    /// <summary>Unchanged base lines between two hunks; negative when they intersect.</summary>
    public static int Gap(DiffHunk a, DiffHunk b) => Math.Max(b.BaseLo - a.BaseHi - 1, a.BaseLo - b.BaseHi - 1);

    public static bool Near(DiffHunk a, DiffHunk b, int margin) => Gap(a, b) < margin;

    public static OverlapPair Compare(string worktreeA, string worktreeB, string? mergeBase, FileDiff a, FileDiff b, int margin = DefaultMargin)
    {
        var changedA = Merge(a.Hunks.Select(h => h.NewSpan));
        var changedB = Merge(b.Hunks.Select(h => h.NewSpan));
        if (a.Binary || b.Binary)
        {
            // Git cannot merge binary content: changes on both sides conflict.
            var both = (a.Binary || a.Hunks.Count > 0) && (b.Binary || b.Hunks.Count > 0);
            return new OverlapPair(worktreeA, worktreeB, mergeBase, changedA, changedB, [], [], [], both, Note: "binary file: line ranges unavailable");
        }
        var hitA = new HashSet<int>();
        var hitB = new HashSet<int>();
        var baseSpans = new List<LineSpan>();
        for (var i = 0; i < a.Hunks.Count; i++)
            for (var j = 0; j < b.Hunks.Count; j++)
            {
                var (x, y) = (a.Hunks[i], b.Hunks[j]);
                if (!Near(x, y, margin)) continue;
                hitA.Add(i);
                hitB.Add(j);
                var lo = Math.Max(1, Math.Min(Math.Min(x.BaseLo, y.BaseLo), Math.Min(x.BaseHi, y.BaseHi)));
                baseSpans.Add(new LineSpan(lo, Math.Max(lo, Math.Max(x.BaseHi, y.BaseHi))));
            }
        return new OverlapPair(worktreeA, worktreeB, mergeBase, changedA, changedB,
            Merge(hitA.Order().Select(i => a.Hunks[i].NewSpan)), Merge(hitB.Order().Select(j => b.Hunks[j].NewSpan)),
            Merge(baseSpans), hitA.Count > 0);
    }

    public static OverlapVerdict Verdict(IReadOnlyCollection<OverlapPair> pairs) =>
        pairs.Any(p => p.Overlapping) ? OverlapVerdict.Overlapping
        : pairs.Count > 0 && pairs.All(p => p.Note is null) ? OverlapVerdict.Separate
        : OverlapVerdict.Unknown;

    /// <summary>Sorted spans with overlapping/touching ones merged, capped at <see cref="MaxSpans"/>.</summary>
    public static List<LineSpan> Merge(IEnumerable<LineSpan> spans)
    {
        var result = new List<LineSpan>();
        foreach (var s in spans.OrderBy(s => s.Start).ThenBy(s => s.End))
        {
            if (result.Count > 0 && s.Start <= result[^1].End + 1)
                result[^1] = result[^1] with { End = Math.Max(result[^1].End, s.End) };
            else result.Add(s);
        }
        return result.Count > MaxSpans ? result[..MaxSpans] : result;
    }

    /// <summary>"40-80", "52", "3-5, 9-12 and 4 more" (ASCII: the text may be typed into a terminal).</summary>
    public static string Format(IReadOnlyList<LineSpan> spans, int max = 3)
    {
        if (spans.Count == 0) return "";
        var shown = string.Join(", ", spans.Take(max).Select(s => s.Start == s.End ? s.Start.ToString() : $"{s.Start}-{s.End}"));
        return spans.Count > max ? $"{shown} and {spans.Count - max} more" : shown;
    }

    /// <summary>"line 52" or "lines 40-80, 90".</summary>
    public static string FormatLines(IReadOnlyList<LineSpan> spans, int max = 3) =>
        (spans is [{ } one] && one.Start == one.End ? "line " : "lines ") + Format(spans, max);

    /// <summary>True when every span in <paramref name="current"/> lies within some span of <paramref name="previous"/> widened by <paramref name="slack"/> lines.</summary>
    public static bool Covered(IReadOnlyList<LineSpan> previous, IReadOnlyList<LineSpan> current, int slack) =>
        current.All(c => previous.Any(p => c.Start >= p.Start - slack && c.End <= p.End + slack));
}
