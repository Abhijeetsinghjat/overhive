using Amc.Cli.Native;

namespace Amc.Tests.Terminals;

/// <summary>CreateProcessW command lines, program lookup and environment blocks (pure; run on every OS).</summary>
public class WinCommandLineTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"C:\dir\file", @"C:\dir\file")]
    [InlineData(@"C:\my dir\", "\"C:\\my dir\\\\\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    public void Quote_follows_the_msvc_rules(string arg, string expected) => Assert.Equal(expected, WinCommandLine.Quote(arg));

    [Fact]
    public void Batch_arguments_are_quoted_or_refused()
    {
        Assert.Equal("plain", WinCommandLine.QuoteForBatch("plain"));
        Assert.Equal("\"a&b\"", WinCommandLine.QuoteForBatch("a&b"));
        Assert.Equal("\"a b\"", WinCommandLine.QuoteForBatch("a b"));
        foreach (var bad in new[] { "%PATH%", "a\"b", "!x!", "a\nb" })
            Assert.Throws<PtySpawnException>(() => WinCommandLine.QuoteForBatch(bad));
    }

    [Fact]
    public void Programs_are_found_on_PATH_with_PATHEXT_and_batch_files_run_through_cmd()
    {
        var root = Path.Combine(Path.GetTempPath(), "amc-wincl-" + Guid.NewGuid().ToString("N"));
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        var cwd = Directory.CreateDirectory(Path.Combine(root, "cwd")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(bin, "tool.exe"), "");
            File.WriteAllText(Path.Combine(bin, "shim.cmd"), "");
            File.WriteAllText(Path.Combine(cwd, "local.exe"), "");
            var path = "C:\\nowhere;" + bin;

            Assert.Equal(Path.Combine(bin, "tool.exe"), WinCommandLine.Resolve("tool", cwd, path, null));
            Assert.Equal(Path.Combine(bin, "tool.exe"), WinCommandLine.Resolve("tool.exe", cwd, path, ".EXE;.CMD"));
            Assert.Null(WinCommandLine.Resolve("local", cwd, path, null)); // never the current directory implicitly
            Assert.Equal(Path.Combine(cwd, "local.exe"), WinCommandLine.Resolve("./local", cwd, path, null));
            Assert.Null(WinCommandLine.Resolve("shim", cwd, path, ".EXE")); // not in PATHEXT

            var (app, line) = WinCommandLine.Build(["tool", "a b", "c"], cwd, path, null);
            Assert.Equal(Path.Combine(bin, "tool.exe"), app);
            Assert.Equal("tool \"a b\" c", line);

            (app, line) = WinCommandLine.Build(["shim", "x&y", "z"], cwd, path, null);
            Assert.EndsWith("cmd.exe", app);
            Assert.Equal($"cmd.exe /d /e:on /v:off /s /c \"\"{Path.Combine(bin, "shim.cmd")}\" \"x&y\" z\"", line);

            var e = Assert.Throws<PtySpawnException>(() => WinCommandLine.Build(["missing"], cwd, path, null));
            Assert.Equal(127, e.CommandExitCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Environment_block_is_sorted_case_insensitively_and_double_terminated()
    {
        var block = WinCommandLine.EnvironmentBlock(new Dictionary<string, string?> { ["AMC_TERMINAL_ID"] = "T9", ["zz_amc_test"] = "1" });
        Assert.EndsWith("\0\0", block);
        var entries = block.TrimEnd('\0').Split('\0');
        Assert.Contains("AMC_TERMINAL_ID=T9", entries);
        var names = entries.Select(x => x[..x.IndexOf('=')]).ToList();
        Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
