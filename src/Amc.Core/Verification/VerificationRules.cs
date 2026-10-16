using System.Text.RegularExpressions;
using Amc.Core.Parsing;

namespace Amc.Core.Verification;

/// <summary>Recognizes test/build commands and test files by convention.</summary>
public static partial class VerificationRules
{
    public static bool IsTestCommand(string command) => TestOutputParser.Detect(command) is not null;

    public static bool IsBuildCommand(string command) => BuildCommand().IsMatch(command);

    /// <summary>Test files by common conventions: tests/, __tests__/, spec/, *.test.*, *.spec.*, *_test.go, test_*.py, *Tests.cs …</summary>
    public static bool IsTestFile(string path) => TestFile().IsMatch(path.Replace('\\', '/'));

    /// <summary>Documentation and notes do not count as source code for the test-change hint.</summary>
    public static bool IsDocFile(string path) => DocFile().IsMatch(path);

    [GeneratedRegex(@"(^|[\s;&|(])(?:(?:dotnet\s+build|msbuild|(?:npm|pnpm|yarn|bun)\s+(?:run\s+)?build|cargo\s+build|go\s+build|swift\s+build|cmake\s+--build|(?:npx\s+)?tsc|(?:npx\s+)?(?:vite|next)\s+build)\b|make(?:\s+-\S+)*(?:\s+(?:all|build))?\s*(?:$|[;&|)])|(?:mvn|\./mvnw)\b.*\b(?:compile|package|install)\b|(?:gradle|\./gradlew)\b.*\b(?:build|assemble)\b)")]
    private static partial Regex BuildCommand();

    [GeneratedRegex(@"(^|/)([Tt]ests?|__tests__|[Ss]pecs?|__snapshots__)/|\.(test|spec)\.[A-Za-z0-9]+$|_test\.[A-Za-z0-9]+$|(^|/)test_[^/]*\.py$|(Tests?|Spec)\.(cs|fs|vb|java|kt|scala|swift)$")]
    private static partial Regex TestFile();

    [GeneratedRegex(@"\.(md|mdx|txt|rst|adoc)$", RegexOptions.IgnoreCase)]
    private static partial Regex DocFile();
}
