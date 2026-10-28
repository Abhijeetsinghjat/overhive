using System.Security.AccessControl;
using System.Security.Principal;
using Amc.Core.Security;
using Amc.Tests.Fixtures;

namespace Amc.Tests.Core;

/// <summary>The Windows owner-only ACL: inheritance disabled, full control for the current user and SYSTEM, nobody else.</summary>
public class OwnerOnlyTests
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    [Fact]
    public void File_and_directory_get_a_protected_acl_for_the_user_and_system_only()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("amc-acl-").FullName;
        try
        {
            var file = Path.Combine(dir, "token");
            File.WriteAllText(file, "x");
            Assert.NotEmpty(OwnerOnly.OthersWithAccess(file)); // the temp dir's inherited ACL lets others in (at least Administrators)

            OwnerOnly.Restrict(file);
            var fs = new FileInfo(file).GetAccessControl();
            Assert.True(fs.AreAccessRulesProtected);
            var rules = fs.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Assert.All(rules, r => Assert.False(r.IsInherited));
            Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
            Assert.Equal(new[] { OwnerOnly.CurrentUserSid.Value, SystemSid.Value }.Order(),
                rules.Select(r => r.IdentityReference.Value).Order());
            Assert.All(rules, r => Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights & FileSystemRights.FullControl));
            Assert.True(OwnerOnly.IsOwnerOnly(file));
            Assert.Empty(OwnerOnly.OthersWithAccess(file));
            Assert.Equal("x", File.ReadAllText(file)); // still usable by us

            // A restricted directory hands the same entries to files created in it later.
            var sub = Path.Combine(dir, "data");
            Directory.CreateDirectory(sub);
            OwnerOnly.Restrict(sub);
            Assert.True(OwnerOnly.IsOwnerOnly(sub));
            var child = Path.Combine(sub, "amc.db");
            File.WriteAllText(child, "y");
            Assert.Empty(OwnerOnly.OthersWithAccess(child));
        }
        finally { TempDirs.Delete(dir); }
    }

    [Fact]
    public void Captured_access_is_copied_to_another_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("amc-acl-").FullName;
        try
        {
            var a = Path.Combine(dir, "a");
            var b = Path.Combine(dir, "b");
            File.WriteAllText(a, "");
            File.WriteAllText(b, "");
            OwnerOnly.Restrict(a);
            Assert.False(OwnerOnly.IsOwnerOnly(b));
            OwnerOnly.CopyAccess(b, OwnerOnly.CaptureAccess(a));
            Assert.True(OwnerOnly.IsOwnerOnly(b));
            Assert.Equal(OwnerOnly.CaptureAccess(a), OwnerOnly.CaptureAccess(b));
        }
        finally { TempDirs.Delete(dir); }
    }
}
