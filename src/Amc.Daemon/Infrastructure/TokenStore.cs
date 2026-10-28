using System.Security.Cryptography;
using Amc.Core.Security;

namespace Amc.Daemon.Infrastructure;

/// <summary>
/// 256-bit API token in a 0600 file inside a 0700 directory (owner-only ACLs on Windows). Read by agentctl and the
/// Claude Code hook command; embedded into the UI page served to loopback clients.
/// </summary>
public static class TokenStore
{
    public static string LoadOrCreate(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows()) OwnerOnly.Restrict(dir);
        else File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return existing;
        }
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, token);
        if (OperatingSystem.IsWindows()) OwnerOnly.Restrict(tmp);
        else File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
        return token;
    }

    public static bool FixedTimeEquals(string? a, string b) =>
        a is not null && a.Length == b.Length &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
