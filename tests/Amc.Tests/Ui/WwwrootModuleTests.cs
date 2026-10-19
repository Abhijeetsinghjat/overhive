using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Amc.Tests.Ui;

/// <summary>
/// The dashboard has no bundler and its CSP allows no inline scripts (so no import map either): every ES
/// module specifier must be a path that exists, and the vendored three.js must stay off the 2D path.
/// </summary>
public sealed partial class WwwrootModuleTests
{
    [GeneratedRegex("""(?:\bfrom\s*|\bimport\s*\(\s*|\bimport\s*)["']([^"'\n]+)["']""")]
    private static partial Regex ImportSpec();

    [GeneratedRegex("""<script\b[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    private static string Wwwroot([CallerFilePath] string source = "")
    {
        // From this source file (works wherever the build output lives), else from the test binaries.
        var dir = File.Exists(source) ? new DirectoryInfo(Path.GetDirectoryName(source)!) : new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Amc.Daemon", "wwwroot"))) dir = dir.Parent;
        return dir is null ? throw new DirectoryNotFoundException("src/Amc.Daemon/wwwroot not found")
            : Path.Combine(dir.FullName, "src", "Amc.Daemon", "wwwroot");
    }

    private static IEnumerable<(string File, string Spec)> Imports(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".js", StringComparison.Ordinal) || f.EndsWith(".mjs", StringComparison.Ordinal))
            .SelectMany(f => ImportSpec().Matches(File.ReadAllText(f)).Select(m => (f, m.Groups[1].Value)));

    [Fact]
    public void EveryModuleImportResolvesToAFile()
    {
        var root = Wwwroot();
        var imports = Imports(root).ToList();
        Assert.Contains(imports, i => i.Spec.Contains("three.module.min.js"));
        foreach (var (file, spec) in imports)
        {
            Assert.True(spec.StartsWith('.') || spec.StartsWith('/'), $"bare specifier '{spec}' in {Path.GetRelativePath(root, file)} (no import map: rewrite it to a relative path)");
            var target = spec.StartsWith('/') ? Path.Combine(root, spec.TrimStart('/')) : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, spec));
            Assert.True(File.Exists(target), $"{Path.GetRelativePath(root, file)} imports missing '{spec}'");
        }
    }

    [Fact]
    public void IndexHasNoInlineScripts()
    {
        var html = File.ReadAllText(Path.Combine(Wwwroot(), "index.html"));
        Assert.All(ScriptTag().Matches(html), m => Assert.Contains("src=", m.Value));
    }

    [Fact]
    public void ThreeJsIsOnlyLoadedLazilyByTheHiveScene()
    {
        var root = Wwwroot();
        Assert.True(File.Exists(Path.Combine(root, "vendor", "three", "LICENSE")));
        var users = Imports(root)
            .Where(i => i.Spec.Contains("vendor/three/", StringComparison.Ordinal))
            .Select(i => Path.GetRelativePath(root, i.File).Replace('\\', '/'))
            .Distinct().ToList();
        Assert.Equal(["js/views/hive-scene.js"], users);
        // hive-scene.js (and with it three.js) is only ever reached through a dynamic import().
        foreach (var (file, spec) in Imports(root).Where(i => i.Spec.EndsWith("hive-scene.js", StringComparison.Ordinal)))
            Assert.Contains($"import('{spec}')", File.ReadAllText(file));
    }
}
