using Amc.Cli.Commands;
using Amc.Cli.Infrastructure;
using Amc.Tests.Fixtures;
using Amc.Tests.Terminals;

namespace Amc.Tests.Cli;

public sealed class DoctorPathTests : IDisposable
{
    private readonly string _a = Directory.CreateTempSubdirectory("amc-doctor-a").FullName;
    private readonly string _b = Directory.CreateTempSubdirectory("amc-doctor-b").FullName;

    public void Dispose() { TempDirs.Delete(_a); TempDirs.Delete(_b); }

    private CliContext Ctx(string path, string? pathext = null) =>
        new() { Env = k => k == "PATH" ? path : k == "PATHEXT" ? pathext : null };

    [Fact]
    public void Windows_lookup_tries_pathext_variants_in_path_order()
    {
        File.WriteAllText(Path.Combine(_b, "git.exe"), "");
        File.WriteAllText(Path.Combine(_a, "curl.cmd"), "");
        var ctx = Ctx($"{_a};{_b}", ".COM;.EXE;.CMD");
        Assert.Equal(Path.Combine(_b, "git.exe"), DoctorCommand.FindOnPath(ctx, "git", windows: true));
        Assert.Equal(Path.Combine(_a, "curl.cmd"), DoctorCommand.FindOnPath(ctx, "curl", windows: true));
        Assert.Null(DoctorCommand.FindOnPath(ctx, "missing", windows: true));
    }

    [Fact]
    public void Windows_lookup_uses_the_default_pathext_and_keeps_an_explicit_extension()
    {
        File.WriteAllText(Path.Combine(_a, "git.exe"), "");
        Assert.Equal(Path.Combine(_a, "git.exe"), DoctorCommand.FindOnPath(Ctx(_a), "git", windows: true));
        Assert.Equal(Path.Combine(_a, "git.exe"), DoctorCommand.FindOnPath(Ctx(_a), "git.exe", windows: true));
    }

    [UnixFact]
    public void Unix_lookup_needs_the_exact_name_and_an_execute_bit()
    {
        File.WriteAllText(Path.Combine(_a, "git.exe"), "");
        var plain = Path.Combine(_b, "git");
        File.WriteAllText(plain, "");
        var ctx = Ctx($"{_a}:{_b}");
        Assert.Null(DoctorCommand.FindOnPath(ctx, "git", windows: false));
        File.SetUnixFileMode(plain, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Assert.Equal(plain, DoctorCommand.FindOnPath(ctx, "git", windows: false));
    }
}
