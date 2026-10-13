using System.Text.RegularExpressions;

namespace Amc.Core.Parsing;

public sealed record TestCommand(string Runner);
public sealed record TestSummary(int? Passed, int? Failed, int? Skipped, IReadOnlyList<string> FailedTests);

/// <summary>
/// Recognizes test commands and parses runner summaries. Results are evidence level
/// "inferred": we parse text the runner printed; an unknown format yields counts = null
/// and pass/fail falls back to the observed exit code.
/// </summary>
public static partial class TestOutputParser
{
    private static readonly (Regex Pattern, string Runner)[] Commands =
    [
        (new(@"(^|[\s;&|(])dotnet\s+test\b"), "dotnet"),
        (new(@"(^|[\s;&|(])(npx\s+|pnpm\s+|yarn\s+)?(vitest|jest)\b"), "js"),
        (new(@"(^|[\s;&|(])(npm|pnpm|yarn|bun)\s+(run\s+)?test\b"), "js"),
        (new(@"(^|[\s;&|(])(python3?\s+-m\s+)?pytest\b"), "pytest"),
        (new(@"(^|[\s;&|(])go\s+test\b"), "go"),
        (new(@"(^|[\s;&|(])cargo\s+test\b"), "cargo"),
        (new(@"(^|[\s;&|(])(mvn|\./mvnw|gradle|\./gradlew)\b.*\btest\b"), "jvm"),
        (new(@"(^|[\s;&|(])make\s+(\S+[-_])?test\b"), "make"),
    ];

    public static TestCommand? Detect(string command)
    {
        foreach (var (p, runner) in Commands)
            if (p.IsMatch(command)) return new TestCommand(runner);
        return null;
    }

    public static TestSummary Parse(string output)
    {
        int? passed = null, failed = null, skipped = null;
        var failedTests = new List<string>();

        // dotnet: "Failed!  - Failed:     2, Passed:    10, Skipped:     0, Total:    12" (one line per test assembly)
        foreach (Match m in DotnetSummary().Matches(output))
        {
            failed = (failed ?? 0) + int.Parse(m.Groups["f"].Value);
            passed = (passed ?? 0) + int.Parse(m.Groups["p"].Value);
            skipped = (skipped ?? 0) + int.Parse(m.Groups["s"].Value);
        }
        // dotnet (newer, totals): "Test summary: total: 12, failed: 2, succeeded: 10, skipped: 0"
        if (passed is null && DotnetTotals().Match(output) is { Success: true } t)
        {
            failed = int.Parse(t.Groups["f"].Value); passed = int.Parse(t.Groups["p"].Value); skipped = int.Parse(t.Groups["s"].Value);
        }
        foreach (Match m in DotnetFailedTest().Matches(output)) failedTests.Add(m.Groups["n"].Value.Trim());

        // jest / vitest: "Tests:  2 failed, 1 skipped, 10 passed, 13 total" / "Tests  2 failed | 10 passed (12)"
        if (passed is null && JsTests().Match(output) is { Success: true } js)
        {
            var line = js.Value;
            failed = Count(line, "failed"); passed = Count(line, "passed"); skipped = Count(line, "skipped");
        }
        // pytest: "==== 2 failed, 10 passed, 1 skipped in 0.12s ===="
        if (passed is null && PytestSummary().Match(output) is { Success: true } py)
        {
            var line = py.Value;
            failed = Count(line, "failed") + (Count(line, "error") ?? 0); passed = Count(line, "passed"); skipped = Count(line, "skipped");
        }
        // go: "--- FAIL: TestX" lines; "ok" / "FAIL" package lines
        if (passed is null && GoPkg().IsMatch(output))
        {
            failed = GoFail().Matches(output).Count;
            passed = GoPass().Matches(output).Count;
            foreach (Match m in GoFail().Matches(output)) failedTests.Add(m.Groups["n"].Value);
        }
        // cargo: "test result: FAILED. 10 passed; 2 failed; 0 ignored"
        foreach (Match m in CargoSummary().Matches(output))
        {
            passed = (passed ?? 0) + int.Parse(m.Groups["p"].Value);
            failed = (failed ?? 0) + int.Parse(m.Groups["f"].Value);
            skipped = (skipped ?? 0) + int.Parse(m.Groups["s"].Value);
        }
        return new TestSummary(passed, failed, skipped, failedTests.Distinct().Take(50).ToList());
    }

    private static int? Count(string line, string word) =>
        Regex.Match(line, @"(\d+)\s+" + word) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    [GeneratedRegex(@"Failed:\s*(?<f>\d+),\s*Passed:\s*(?<p>\d+),\s*Skipped:\s*(?<s>\d+)")]
    private static partial Regex DotnetSummary();
    [GeneratedRegex(@"total:\s*\d+,\s*failed:\s*(?<f>\d+),\s*succeeded:\s*(?<p>\d+),\s*skipped:\s*(?<s>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DotnetTotals();
    [GeneratedRegex(@"^\s*Failed\s+(?<n>[\w.`<>,\[\]]+)\s*\[", RegexOptions.Multiline)]
    private static partial Regex DotnetFailedTest();
    [GeneratedRegex(@"^\s*Tests:?\s+.*\b(passed|failed)\b.*$", RegexOptions.Multiline)]
    private static partial Regex JsTests();
    [GeneratedRegex(@"=+ .*\b(passed|failed|error)\b.* in [\d.]+s", RegexOptions.Multiline)]
    private static partial Regex PytestSummary();
    [GeneratedRegex(@"^(ok|FAIL)\s+\S+", RegexOptions.Multiline)]
    private static partial Regex GoPkg();
    [GeneratedRegex(@"^\s*--- FAIL: (?<n>\S+)", RegexOptions.Multiline)]
    private static partial Regex GoFail();
    [GeneratedRegex(@"^\s*--- PASS: \S+", RegexOptions.Multiline)]
    private static partial Regex GoPass();
    [GeneratedRegex(@"test result: \w+\. (?<p>\d+) passed; (?<f>\d+) failed; (?<s>\d+) ignored")]
    private static partial Regex CargoSummary();
}
