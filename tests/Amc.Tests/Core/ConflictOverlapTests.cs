using Amc.Core.Conflicts;
using Amc.Core.Domain;
using Amc.Core.Events;
using Amc.Core.State;

namespace Amc.Tests.Core;

public sealed class ConflictOverlapTests
{
    // ---- hunk parsing ----------------------------------------------------------------------

    [Theory]
    [InlineData("@@ -3 +3 @@", 3, 1, 3, 1)]
    [InlineData("@@ -10,0 +11 @@", 10, 0, 11, 1)]
    [InlineData("@@ -5,2 +4,0 @@ class Foo", 5, 2, 4, 0)]
    [InlineData("@@ -0,0 +1,12 @@", 0, 0, 1, 12)]
    [InlineData("@@ -40,11 +40,20 @@ function x() {", 40, 11, 40, 20)]
    public void Hunk_headers_parse(string line, int a, int b, int c, int d)
    {
        Assert.True(HunkParser.TryHeader(line, out var h));
        Assert.Equal(new DiffHunk(a, b, c, d), h);
    }

    [Theory]
    [InlineData("@@ garbage @@")]
    [InlineData("@@ -x +1 @@")]
    [InlineData("@@ -1 +1")]
    public void Malformed_headers_are_rejected(string line) => Assert.False(HunkParser.TryHeader(line, out _));

    [Fact]
    public void Parser_skips_hunk_bodies_by_count_so_content_never_looks_like_a_header()
    {
        const string diff = """
            diff --git a/src/x.ts b/src/x.ts
            index 111..222 100644
            --- a/src/x.ts
            +++ b/src/x.ts
            @@ -2,2 +2,2 @@
            --- a/not-a-header
            -diff --git a/fake b/fake
            ++++ b/also-content
            +@@ -99 +99 @@
            \ No newline at end of file
            @@ -10,0 +11,2 @@
            +one
            +two
            """;
        var f = Assert.Single(HunkParser.Parse(diff));
        Assert.Equal(("src/x.ts", "src/x.ts"), (f.OldPath, f.NewPath));
        Assert.Equal([new DiffHunk(2, 2, 2, 2), new DiffHunk(10, 0, 11, 2)], f.Hunks);
    }

    [Fact]
    public void Parser_reads_new_deleted_renamed_and_binary_sections()
    {
        const string diff = """
            diff --git a/new.txt b/new.txt
            new file mode 100644
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,3 @@
            +a
            +b
            +c
            diff --git a/gone.txt b/gone.txt
            deleted file mode 100644
            --- a/gone.txt
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -x
            -y
            diff --git a/old/name.ts b/new/name.ts
            similarity index 90%
            rename from old/name.ts
            rename to new/name.ts
            --- a/old/name.ts
            +++ b/new/name.ts
            @@ -7 +7 @@
            -was
            +now
            diff --git a/img.png b/img.png
            Binary files a/img.png and b/img.png differ
            """;
        var files = HunkParser.Parse(diff);
        Assert.Equal(4, files.Count);
        Assert.True(files[0].NewFile);
        Assert.Null(files[0].OldPath);
        Assert.Equal(new DiffHunk(0, 0, 1, 3), Assert.Single(files[0].Hunks));
        Assert.True(files[1].Deleted);
        Assert.Equal(new DiffHunk(1, 2, 0, 0), Assert.Single(files[1].Hunks));
        Assert.Equal(("old/name.ts", "new/name.ts"), (files[2].OldPath, files[2].NewPath));
        Assert.True(files[3].Binary);
        Assert.Same(files[2], HunkParser.ForPath(files, "new/name.ts"));
        Assert.Same(files[2], HunkParser.ForPath(files, "old/name.ts"));
        Assert.Empty(HunkParser.Parse("").SelectMany(f => f.Hunks));
    }

    // ---- overlap ------------------------------------------------------------------------

    private static FileDiff Diff(params DiffHunk[] hunks) => new("f", "f", false, false, false, [.. hunks]);
    private static OverlapPair Cmp(FileDiff a, FileDiff b, int margin = 3) => OverlapMath.Compare("wt:a", "wt:b", "base", a, b, margin);

    [Fact]
    public void Intersecting_ranges_overlap_and_report_both_sides_in_their_own_coordinates()
    {
        // A rewrote base 40-50 into 40-80; B changed base 45-50 (its own lines 45-52).
        var p = Cmp(Diff(new DiffHunk(40, 11, 40, 41), new DiffHunk(200, 1, 230, 1)), Diff(new DiffHunk(45, 6, 45, 8)));
        Assert.True(p.Overlapping);
        Assert.Equal([new LineSpan(40, 80)], p.OverlapA);
        Assert.Equal([new LineSpan(45, 52)], p.OverlapB);
        Assert.Equal([new LineSpan(40, 50)], p.OverlapBase);
        Assert.Equal([new LineSpan(40, 80), new LineSpan(230, 230)], p.ChangedA);
    }

    [Theory]
    [InlineData(13, true)]  // directly adjacent (0 unchanged lines between): git conflicts too
    [InlineData(15, true)]  // 2 unchanged lines between: within the margin
    [InlineData(16, false)] // 3 unchanged lines between: separate
    [InlineData(40, false)]
    public void Margin_decides_near_ranges(int otherStart, bool overlapping)
    {
        var p = Cmp(Diff(new DiffHunk(10, 3, 10, 3)), Diff(new DiffHunk(otherStart, 2, otherStart, 2)));
        Assert.Equal(overlapping, p.Overlapping);
        Assert.Equal(overlapping, Cmp(Diff(new DiffHunk(otherStart, 2, otherStart, 2)), Diff(new DiffHunk(10, 3, 10, 3))).Overlapping);
    }

    [Fact]
    public void Insertions_at_the_same_place_or_inside_a_change_overlap()
    {
        Assert.True(Cmp(Diff(new DiffHunk(20, 0, 21, 4)), Diff(new DiffHunk(20, 0, 21, 2))).Overlapping);
        Assert.True(Cmp(Diff(new DiffHunk(18, 5, 18, 5)), Diff(new DiffHunk(20, 0, 21, 1))).Overlapping);
        Assert.False(Cmp(Diff(new DiffHunk(20, 0, 21, 4)), Diff(new DiffHunk(60, 0, 61, 1))).Overlapping);
        Assert.Equal(0, OverlapMath.Gap(new DiffHunk(20, 0, 21, 1), new DiffHunk(21, 1, 22, 1))); // insertion right before a changed line
    }

    [Fact]
    public void Deletions_and_whole_file_changes()
    {
        // Deleted file (all base lines) vs any edit → overlap.
        Assert.True(Cmp(Diff(new DiffHunk(1, 120, 0, 0)), Diff(new DiffHunk(77, 1, 77, 1))).Overlapping);
        // Both sides created the same new path (add/add) → overlap.
        Assert.True(Cmp(FileDiff.Added("f", 10), FileDiff.Added("f", 3)).Overlapping);
        // A deletion shows in the agent's own file at the line it follows.
        var p = Cmp(Diff(new DiffHunk(30, 4, 29, 0)), Diff(new DiffHunk(31, 1, 31, 1)));
        Assert.True(p.Overlapping);
        Assert.Equal([new LineSpan(29, 29)], p.OverlapA);
        // Nothing changed on one side (e.g. reverted) → no overlap.
        Assert.False(Cmp(Diff(), Diff(new DiffHunk(1, 1, 1, 1))).Overlapping);
    }

    [Fact]
    public void Binary_changes_on_both_sides_overlap_without_line_ranges()
    {
        var bin = new FileDiff("f", "f", false, false, true, []);
        var p = Cmp(bin, bin);
        Assert.True(p.Overlapping);
        Assert.NotNull(p.Note);
        Assert.Equal(OverlapVerdict.Overlapping, OverlapMath.Verdict([p]));
    }

    [Fact]
    public void Verdict_and_formatting()
    {
        var sep = Cmp(Diff(new DiffHunk(1, 1, 1, 1)), Diff(new DiffHunk(50, 1, 50, 1)));
        Assert.Equal(OverlapVerdict.Separate, OverlapMath.Verdict([sep]));
        Assert.Equal(OverlapVerdict.Unknown, OverlapMath.Verdict([sep with { Note = "diff too large" }]));
        Assert.Equal(OverlapVerdict.Unknown, OverlapMath.Verdict([]));
        Assert.Equal("40-80", OverlapMath.Format([new LineSpan(40, 80)]));
        Assert.Equal("1, 3-4, 9 and 1 more", OverlapMath.Format([new(1, 1), new(3, 4), new(9, 9), new(12, 13)]));
        Assert.Equal([new LineSpan(1, 5), new LineSpan(8, 9)], OverlapMath.Merge([new(4, 5), new(1, 3), new(8, 9)]));
    }

    // ---- messages & dedupe -----------------------------------------------------------------

    [Fact]
    public void Message_names_the_other_agent_branch_and_both_line_ranges()
    {
        var me = new WarnParty("codex:1", "Codex", "Codex CLI", "wt:a", "feat/x");
        var other = new WarnParty("gemini:2", "Gemini", "Gemini CLI", "wt:b", "fix/y");
        var text = ConflictWarnings.Message("src/auth.ts", me,
            [new WarnCounterpart([other], "fix/y", [new LineSpan(52, 70)], [new LineSpan(40, 80)], true)]);
        Assert.Equal("Heads-up from Overhive: Gemini (Gemini CLI, branch fix/y) is also editing src/auth.ts lines 52-70, " +
                     "overlapping your lines 40-80. Coordinate before changing those lines. (Line ranges observed in git.)", text);

        var separate = ConflictWarnings.Message("src/auth.ts", me,
            [new WarnCounterpart([other with { Inferred = true }], null, [new LineSpan(200, 210)], [new LineSpan(40, 80)], false)]);
        Assert.Contains("Gemini (Gemini CLI, branch detached) [attribution inferred] is also editing src/auth.ts lines 200-210; your lines 40-80 are in a different region", separate);

        var unknown = ConflictWarnings.Message("a.bin", me, [new WarnCounterpart([], "fix/y", [], [], null)]);
        Assert.Equal("Heads-up from Overhive: another worktree (branch fix/y) is also editing a.bin in another worktree. Coordinate before changing it further.", unknown);
    }

    [Fact]
    public void Message_text_is_redacted()
    {
        var me = new WarnParty("a", "A", "T", "wt:a", "x");
        var text = ConflictWarnings.Message("cfg/token=ghp_abcdefghijklmnopqrstuvwxyz0123456789.txt", me, [new WarnCounterpart([], "y", [], [], true)]);
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789", text);
    }

    [Fact]
    public void Auto_warn_dedupes_per_agent_and_rewarns_only_when_ranges_move()
    {
        var t0 = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        List<LineSpan> told = [new(40, 50)];
        var history = new List<ConflictWarning>();
        Assert.True(ConflictWarnings.ShouldAutoWarn(history, "a", told, t0, out _));

        history.Add(new ConflictWarning(t0, true, [new ConflictWarningRecipient("a", "queued", "m1", "pty", null, told)]));
        Assert.False(ConflictWarnings.ShouldAutoWarn(history, "a", [new(100, 120)], t0.AddMinutes(1), out var r1));
        Assert.Equal("warned recently", r1);
        Assert.False(ConflictWarnings.ShouldAutoWarn(history, "a", [new(45, 58)], t0.AddHours(1), out var r2)); // within the slack
        Assert.Equal("ranges unchanged", r2);
        Assert.True(ConflictWarnings.ShouldAutoWarn(history, "a", [new(100, 120)], t0.AddHours(1), out _));
        Assert.True(ConflictWarnings.ShouldAutoWarn(history, "b", told, t0.AddMinutes(1), out _)); // another agent

        // A failed attempt holds off retries for the interval, then retries.
        var failed = new List<ConflictWarning> { new(t0, true, [new ConflictWarningRecipient("c", "failed")]) };
        Assert.False(ConflictWarnings.ShouldAutoWarn(failed, "c", told, t0.AddMinutes(2), out _));
        Assert.True(ConflictWarnings.ShouldAutoWarn(failed, "c", told, t0.AddMinutes(11), out _));
    }

    // ---- reducer -------------------------------------------------------------------------

    [Fact]
    public void Analysis_folded_before_the_conflict_exists_attaches_when_it_is_derived_and_survives_rederivation()
    {
        var f = new EventFactory();
        var s = new WorldState();
        const string key = "cross-worktree|repo:1|src/x.ts";
        var overlap = new ConflictOverlap { Verdict = OverlapVerdict.Overlapping, InputKey = "k", Margin = 3 };
        // One batch: the analysis event comes before the events that make the conflict appear.
        Reducer.ApplyBatch(s,
        [
            f.Make(EventTypes.ConflictAnalyzed, new ConflictAnalysisData(key, overlap), evidence: Evidence.Observed),
            f.Main("claude:a"), f.Main("claude:b", 101), f.Repo("repo:1"),
            f.Worktree("wt:1", "/r1", "repo:1"), f.Worktree("wt:2", "/r2", "repo:1"),
            f.Edit("claude:a", "wt:1", "src/x.ts"), f.Edit("claude:b", "wt:2", "src/x.ts"),
            f.GitStatus("wt:1", "src/x.ts", "M"), f.GitStatus("wt:2", "src/x.ts", "M"),
        ]);
        var c = s.Conflicts[key];
        Assert.Equal(OverlapVerdict.Overlapping, c.Insight?.Overlap?.Verdict);

        // A warning is recorded on the same insight; a third agent re-derives the conflict object.
        var changed = Reducer.ApplyBatch(s, [f.Make(EventTypes.ConflictWarned, new ConflictWarnedData(key, false, [new("claude:a", "delivered")]))]);
        Assert.Contains(new EntityRef(EntityKinds.Conflict, key), changed);
        Reducer.ApplyBatch(s, [f.Main("claude:c", 102), f.Edit("claude:c", "wt:2", "src/x.ts")]);
        Assert.Equal(3, s.Conflicts[key].AgentIds.Count);
        Assert.Equal("delivered", Assert.Single(s.Conflicts[key].Insight!.Warnings).Recipients[0].Outcome);
        Assert.NotNull(s.Conflicts[key].Insight!.Overlap);

        // Survives a snapshot round trip.
        var restored = Json.Deserialize<WorldState>(Json.Serialize(s))!;
        Assert.Equal(OverlapVerdict.Overlapping, restored.Conflicts[key].Insight?.Overlap?.Verdict);
        Assert.Equal("high", restored.ConflictInsights[key].Overlap!.Severity);
    }

    [Fact]
    public void Orphaned_insights_are_pruned_after_an_hour()
    {
        var f = new EventFactory();
        var s = new WorldState();
        Reducer.ApplyBatch(s, [f.Make(EventTypes.ConflictAnalyzed, new ConflictAnalysisData("cross-worktree|gone", new ConflictOverlap()))]);
        Assert.True(s.ConflictInsights.ContainsKey("cross-worktree|gone"));
        f.Advance(TimeSpan.FromMinutes(61));
        Reducer.ApplyBatch(s, [f.Main("claude:z")]);
        Assert.False(s.ConflictInsights.ContainsKey("cross-worktree|gone"));
    }
}
