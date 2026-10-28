using Amc.Daemon.Tools;

namespace Amc.Tests.Tools;

public class GenericToolMatchTests
{
    [Theory]
    [InlineData("/usr/bin/node", false, "node")]
    [InlineData("codex", false, "codex")]
    [InlineData("/opt/tool.exe", false, "tool.exe")] // not Windows: ".exe" is part of the name
    [InlineData(@"C:\Program Files\nodejs\node.exe", true, "node")]
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\codex.EXE", true, "codex")]
    [InlineData("C:/tools/gemini.exe", true, "gemini")]
    [InlineData("aider", true, "aider")]
    public void Exe_name_drops_the_directory_and_on_windows_the_exe_suffix(string argv0, bool windows, string expected) =>
        Assert.Equal(expected, GenericToolCollector.ExeName(argv0, windows));
}
