using Amc.Core.Domain;
using Amc.Daemon.Collectors.Git;

namespace Amc.Tests.Core;

/// <summary>Both modes run on every OS: the internal overloads take the OS as a flag.</summary>
public class PathTextTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/a/b", "/a/b")]
    [InlineData("/a//b/./c/", "/a/b/c")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/../..", "/")]
    [InlineData("a/b", "/a/b")]
    [InlineData("/a\\b/c", "/a\\b/c")]          // a backslash is a file-name character on Unix
    [InlineData("C:\\x", "/C:\\x")]
    [InlineData("/c/Users", "/c/Users")]          // no Git Bash translation on Unix
    public void Normalize_unix(string path, string expected) => Assert.Equal(expected, PathText.Normalize(path, windows: false));

    [Theory]
    [InlineData("C:\\Repo\\a.cs", "C:/Repo/a.cs")]
    [InlineData("c:/repo/", "C:/repo")]
    [InlineData("c:", "C:/")]
    [InlineData("C:\\", "C:/")]
    [InlineData("C:\\a\\..\\..\\b", "C:/b")]      // ".." never climbs above the drive
    [InlineData("C:/a//b/./c", "C:/a/b/c")]
    [InlineData("/c/Users/me", "C:/Users/me")]    // Git Bash / MSYS
    [InlineData("/d", "D:/")]
    [InlineData("/work/repo", "/work/repo")]      // a multi-letter first segment is not a drive
    [InlineData("\\\\server\\share\\x\\y", "//server/share/x/y")]
    [InlineData("//server/share", "//server/share")]
    [InlineData("//server/share/a/../..", "//server/share")]
    [InlineData("\\\\?\\C:\\long\\path", "C:/long/path")]
    [InlineData("\\\\?\\UNC\\server\\share\\x", "//server/share/x")]
    [InlineData("\\\\.\\D:\\x", "D:/x")]
    public void Normalize_windows(string path, string expected) => Assert.Equal(expected, PathText.Normalize(path, windows: true));

    [Theory]
    [InlineData("/x", false, true)]
    [InlineData("x/y", false, false)]
    [InlineData("C:\\x", false, false)]
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    [InlineData("C:\\x", true, true)]
    [InlineData("c:/x", true, true)]
    [InlineData("C:", true, true)]
    [InlineData("\\\\srv\\share", true, true)]
    [InlineData("\\x", true, true)]
    [InlineData("/x", true, true)]
    [InlineData("C:x", true, false)]               // drive-relative
    [InlineData("src\\a.cs", true, false)]
    public void IsAbsolute(string? path, bool windows, bool expected) => Assert.Equal(expected, PathText.IsAbsolute(path, windows));

    [Fact]
    public void Combine_is_lexical_and_keeps_absolute_paths()
    {
        Assert.Equal("/work/repo/src/a.ts", PathText.Combine("/work/repo", "src/a.ts", windows: false));
        Assert.Equal("/etc/x", PathText.Combine("/work/repo", "/etc/x", windows: false));
        Assert.Equal("/work/a", PathText.Combine("/work/repo/", "../a", windows: false));
        Assert.Equal("/work/repo/src\\a.ts", PathText.Combine("/work/repo", "src\\a.ts", windows: false));
        Assert.Equal("C:/Repo/src/a.ts", PathText.Combine("C:\\Repo", "src\\a.ts", windows: true));
        Assert.Equal("D:/x", PathText.Combine("C:\\Repo", "d:\\x", windows: true));
        Assert.Equal("/work/repo/src/a.ts", PathText.Combine("/work/repo", "src\\a.ts", windows: true));
        Assert.Equal("C:/Users/me", PathText.Combine("C:\\Repo", "/c/Users/me", windows: true));
    }

    [Fact]
    public void Relative_unix_is_case_sensitive_and_segment_aware()
    {
        Assert.Equal("", PathText.Relative("/r", "/r/", windows: false));
        Assert.Equal("src/a.cs", PathText.Relative("/r", "/r/src/a.cs", windows: false));
        Assert.Equal("src/a.cs", PathText.Relative("/r/", "/r/x/../src//a.cs", windows: false));
        Assert.Equal("a", PathText.Relative("/", "/a", windows: false));
        Assert.Null(PathText.Relative("/r", "/repo/a", windows: false));
        Assert.Null(PathText.Relative("/r", "/R/a", windows: false));
        Assert.Null(PathText.Relative("/r", "/r/../etc", windows: false));
    }

    [Fact]
    public void Relative_windows_ignores_case_and_separator_style()
    {
        Assert.Equal("src/a.cs", PathText.Relative("C:/Repo", "c:\\repo\\src\\a.cs", windows: true));
        Assert.Equal("", PathText.Relative("C:\\Repo\\", "c:/REPO", windows: true));
        Assert.Equal("a", PathText.Relative("C:\\", "C:\\a", windows: true));
        Assert.Equal("x", PathText.Relative("/c/Repo", "C:\\Repo\\x", windows: true));
        Assert.Equal("x", PathText.Relative("\\\\srv\\share", "//SRV/share/x", windows: true));
        Assert.Null(PathText.Relative("C:/Repo", "D:/Repo/a", windows: true));
        Assert.Null(PathText.Relative("C:/Repo", "C:/Repository", windows: true));
        Assert.Null(PathText.Relative("//srv/share", "//srv/share2/x", windows: true));
    }

    [Fact]
    public void Parent_and_root()
    {
        Assert.Equal("/a", PathText.Parent("/a/b", windows: false));
        Assert.Equal("/", PathText.Parent("/a", windows: false));
        Assert.Null(PathText.Parent("/", windows: false));
        Assert.Equal("C:/a", PathText.Parent("C:\\a\\b", windows: true));
        Assert.Equal("C:/", PathText.Parent("C:\\a", windows: true));
        Assert.Null(PathText.Parent("C:\\", windows: true));
        Assert.Equal("//srv/share", PathText.Parent("//srv/share/x", windows: true));
        Assert.Null(PathText.Parent("//srv/share", windows: true));
        Assert.Equal("/", PathText.Root("/a/b", windows: false));
        Assert.Equal("C:/", PathText.Root("c:\\a", windows: true));
        Assert.Equal("//srv/share", PathText.Root("\\\\srv\\share\\a", windows: true));
    }

    [Fact]
    public void Slashes_and_comparison()
    {
        Assert.Equal("src\\a.ts", PathText.Slashes("src\\a.ts", windows: false));
        Assert.Equal("src/a.ts", PathText.Slashes("src\\a.ts", windows: true));
        Assert.Equal(StringComparison.Ordinal, PathText.ComparisonFor(false));
        Assert.Equal(StringComparison.OrdinalIgnoreCase, PathText.ComparisonFor(true));
    }

    [Fact]
    public void Public_api_matches_this_os()
    {
        Assert.Equal(OperatingSystem.IsWindows(), PathText.Windows);
        Assert.Equal(PathText.Normalize("/a//b/", PathText.Windows), PathText.Normalize("/a//b/"));
        Assert.True(PathText.IsUnder("/a", "/a/b"));
        Assert.False(PathText.IsUnder("/a", "/ab"));
    }
}

/// <summary>The lexical half of <see cref="GitPaths"/> in both modes; the filesystem half is covered by the Git tests.</summary>
public class GitPathsLexicalTests
{
    [Theory]
    [InlineData("src/a.cs", "src/a.cs")]
    [InlineData("./src//a.cs", "src/a.cs")]
    [InlineData("src\\a.cs", "src\\a.cs")]            // one file name on Unix
    [InlineData("a:b", "a:b")]
    [InlineData("name.", "name.")]
    [InlineData("GIT~1/config", "GIT~1/config")]
    public void Unix_accepts(string path, string expected)
    {
        Assert.True(GitPaths.TrySegments(path, windows: false, out var segs, out var error), error);
        Assert.Equal(expected, string.Join('/', segs));
    }

    [Theory]
    [InlineData("", "path is required")]
    [InlineData("a\0b", "path contains NUL")]
    [InlineData("/etc/passwd", "path must be relative to the worktree root")]
    [InlineData("-p", "path must not start with '-'")]
    [InlineData("./-p", "path must not start with '-'")]
    [InlineData("./", "path is empty")]
    [InlineData("a/../../x", "path must not contain '..'")]
    [InlineData("src/.GIT/config", "paths inside .git are not served")]
    public void Unix_rejects(string path, string expected)
    {
        Assert.False(GitPaths.TrySegments(path, windows: false, out _, out var error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("src\\a.cs", "src/a.cs")]
    [InlineData(".\\src/a.cs", "src/a.cs")]
    [InlineData("my~file.txt", "my~file.txt")]
    [InlineData("git~x/a", "git~x/a")]
    public void Windows_accepts(string path, string expected)
    {
        Assert.True(GitPaths.TrySegments(path, windows: true, out var segs, out var error), error);
        Assert.Equal(expected, string.Join('/', segs));
    }

    [Theory]
    [InlineData("C:\\Windows\\win.ini", "path must be relative to the worktree root")]
    [InlineData("C:win.ini", "path must be relative to the worktree root")]
    [InlineData("\\\\srv\\share\\x", "path must be relative to the worktree root")]
    [InlineData("\\x", "path must be relative to the worktree root")]
    [InlineData("src\\..\\..\\x", "path must not contain '..'")]
    [InlineData(".git\\config", "paths inside .git are not served")]
    [InlineData(".git.\\config", "paths inside .git are not served")]
    [InlineData(".git \\config", "paths inside .git are not served")]
    [InlineData(".git::$INDEX_ALLOCATION\\config", "paths inside .git are not served")]
    [InlineData("GIT~1\\config", "paths inside .git are not served")]
    [InlineData("a\\git~12\\config", "paths inside .git are not served")]
    [InlineData("a.txt:secret", "path is not a valid Windows file name")]
    [InlineData("a*.txt", "path is not a valid Windows file name")]
    [InlineData("...\\x", "path is not a valid Windows file name")]
    [InlineData("name.", "path is not a valid Windows file name")]
    [InlineData("name ", "path is not a valid Windows file name")]
    public void Windows_rejects(string path, string expected)
    {
        Assert.False(GitPaths.TrySegments(path, windows: true, out _, out var error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("C:\\Repo\\..\\secret", "C:/", "C:/", "secret")]
    [InlineData("c:/Repo", "C:/", "C:/", "Repo")]
    [InlineData("\\??\\D:\\target", "D:/", "D:/", "target")]                 // junction substitute name
    [InlineData("\\\\?\\UNC\\srv\\share\\x", "//srv/share", "//srv/share", "x")]
    [InlineData("\\elsewhere", "C:/", "C:/", "elsewhere")]                     // root-relative: the link's drive
    [InlineData("..\\.git", "C:/", "C:/repo", "../.git")]                      // relative: from the link's directory
    public void Windows_link_targets(string target, string root, string current, string pushed)
    {
        var r = "C:/";
        var c = "C:/repo";
        var pending = new Stack<string>();
        Assert.True(GitPaths.Follow(target, windows: true, ref r, ref c, pending));
        Assert.Equal((root, current, pushed), (r, c, string.Join('/', pending)));
    }

    [Fact]
    public void Drive_relative_link_targets_are_refused()
    {
        var root = "C:/";
        var current = "C:/repo";
        Assert.False(GitPaths.Follow("D:x", windows: true, ref root, ref current, new Stack<string>()));
    }

    [Fact]
    public void Unix_link_targets()
    {
        var root = "/";
        var current = "/repo";
        var pending = new Stack<string>();
        Assert.True(GitPaths.Follow("../x\\y", windows: false, ref root, ref current, pending));
        Assert.Equal(("/", "/repo", "../x\\y"), (root, current, string.Join('/', pending)));
        pending.Clear();
        Assert.True(GitPaths.Follow("/abs/t", windows: false, ref root, ref current, pending));
        Assert.Equal(("/", "/", "abs/t"), (root, current, string.Join('/', pending)));
    }
}
