using Amc.Core.Parsing;
using Amc.Core.Security;

namespace Amc.Tests.Core;

public class TestOutputParserTests
{
    [Theory]
    [InlineData("cd /x && dotnet test --no-build", "dotnet")]
    [InlineData("npx vitest run", "js")]
    [InlineData("npm test -- --watch=false", "js")]
    [InlineData("python -m pytest -q", "pytest")]
    [InlineData("go test ./...", "go")]
    [InlineData("cargo test", "cargo")]
    [InlineData("make unit-test", "make")]
    public void Detects_runner(string cmd, string runner) => Assert.Equal(runner, TestOutputParser.Detect(cmd)?.Runner);

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("git commit -m 'test'")]
    [InlineData("cat testfile.txt")]
    public void Ignores_non_test_commands(string cmd) => Assert.Null(TestOutputParser.Detect(cmd));

    [Fact]
    public void Parses_dotnet_summaries_across_assemblies_and_failed_names()
    {
        var output = """
              Failed Activities.ActivityServiceTests.Rejects_empty [12 ms]
            Failed!  - Failed:     2, Passed:    10, Skipped:     1, Total:    13, Duration: 1 s - A.Tests.dll (net10.0)
            Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 s - B.Tests.dll (net10.0)
            """;
        var s = TestOutputParser.Parse(output);
        Assert.Equal((15, 2, 1), (s.Passed, s.Failed, s.Skipped));
        Assert.Contains("Activities.ActivityServiceTests.Rejects_empty", s.FailedTests);
    }

    [Theory]
    [InlineData("Tests:       2 failed, 1 skipped, 10 passed, 13 total", 10, 2, 1)]
    [InlineData("========= 3 failed, 7 passed, 2 skipped in 1.20s =========", 7, 3, 2)]
    [InlineData("test result: FAILED. 8 passed; 1 failed; 0 ignored; 0 measured", 8, 1, 0)]
    public void Parses_other_runners(string output, int passed, int failed, int skipped)
    {
        var s = TestOutputParser.Parse(output);
        Assert.Equal((passed, failed, skipped), (s.Passed, s.Failed, s.Skipped));
    }

    [Fact]
    public void Unknown_format_yields_null_counts() => Assert.Null(TestOutputParser.Parse("all good").Passed);
}

public class RedactorTests
{
    [Theory]
    [InlineData("export ANTHROPIC_API_KEY=sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123")]
    [InlineData("curl -H 'Authorization: Bearer abcdefghijklmnop1234567890'")]
    [InlineData("git clone https://user:hunter2secret@github.com/x/y")]
    [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("DB_PASSWORD=Sup3rS3cret!")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----")]
    [InlineData("curl -H 'Authorization: Basic Sup3rS3cretAAAA=='")]
    [InlineData("mysql -uroot -pSup3rS3cret mydb")]
    [InlineData("docker login --password Sup3rS3cret registry")]
    [InlineData("curl -u admin:Sup3rS3cret https://x")]
    [InlineData("export MY_PASS=Sup3rS3cret")]
    [InlineData("DB_PWD=Sup3rS3cret ./run")]
    public void Removes_secrets(string input)
    {
        var r = Redactor.Redact(input);
        Assert.Contains(Redactor.Mask, r);
        foreach (var secret in new[] { "abcdefghijklmnopqrstuvwxyz0123", "abcdefghijklmnop1234567890", "hunter2secret", "Sup3rS3cret", "AKIAABCDEFGHIJKLMNOP", "MIIabc" })
            Assert.DoesNotContain(secret, r);
    }

    [Theory]
    [InlineData("find . -path ./node_modules -prune -o -print")]
    [InlineData("Failed!  - Failed:     1, Passed:     4, Skipped:     0")]
    [InlineData("git log --pretty=oneline -n 5")]
    public void Does_not_redact_common_non_secrets(string s) => Assert.Equal(s, Redactor.Redact(s));

    [Fact]
    public void Leaves_ordinary_text_alone()
    {
        const string s = "dotnet test src/Activities/ActivityService.cs --filter Category=Unit";
        Assert.Equal(s, Redactor.Redact(s));
    }

    [Fact]
    public void Cap_keeps_tail()
    {
        var s = new string('a', 100) + "TAIL";
        var r = Redactor.RedactAndCap(s, 10)!;
        Assert.EndsWith("TAIL", r);
        Assert.StartsWith("…[truncated", r);
    }
}
