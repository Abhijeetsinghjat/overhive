using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Amc.Core.Security;

/// <summary>
/// Owner-only protection on Windows, the counterpart of umask 077 / chmod 600 / chmod 700 on Unix: a protected DACL
/// (nothing inherited from the parent) granting full control to the current user and SYSTEM only. Administrators,
/// Users and Everyone get no entry. On a directory the entries are inheritable, so files created in it later are
/// owner-only too.
/// </summary>
[SupportedOSPlatform("windows")]
public static class OwnerOnly
{
    private const InheritanceFlags ToChildren = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    private static readonly SecurityIdentifier Me = CurrentUser();
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>The SID of the user this process runs as.</summary>
    public static SecurityIdentifier CurrentUserSid => Me;

    /// <summary>Replaces the DACL of an existing file or directory with the owner-only one.</summary>
    public static void Restrict(string path)
    {
        if (Directory.Exists(path))
        {
            var s = new DirectorySecurity();
            s.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { Me, SystemSid })
                s.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, ToChildren, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(s);
        }
        else
        {
            var s = new FileSecurity();
            s.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { Me, SystemSid })
                s.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(s);
        }
    }

    /// <summary>
    /// SIDs other than the current user and SYSTEM that an allow entry (explicit or inherited) lets read the file or
    /// directory. Empty means owner-only.
    /// </summary>
    public static IReadOnlyList<SecurityIdentifier> OthersWithAccess(string path)
    {
        FileSystemSecurity s = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        return s.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & FileSystemRights.ReadData) != 0)
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .Where(sid => sid != Me && sid != SystemSid)
            .Distinct().ToList();
    }

    /// <summary>True when the DACL is protected (inheritance disabled) and only the current user and SYSTEM have entries.</summary>
    public static bool IsOwnerOnly(string path)
    {
        FileSystemSecurity s = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        return s.AreAccessRulesProtected && s.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .All(r => (SecurityIdentifier)r.IdentityReference is var sid && (sid == Me || sid == SystemSid));
    }

    /// <summary>The file's explicit access entries and inheritance setting, as SDDL, for <see cref="CopyAccess"/>.</summary>
    public static string CaptureAccess(string path) =>
        new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    /// <summary>Gives a file the access entries captured from another (the Windows form of preserving its mode).</summary>
    public static void CopyAccess(string path, string sddl)
    {
        var s = new FileSecurity();
        s.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
        new FileInfo(path).SetAccessControl(s);
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User!;
    }

    /// <summary>A display name for a SID ("BUILTIN\Users"), or the SID itself when it cannot be resolved.</summary>
    public static string Name(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return sid.Value; }
        catch (SystemException) { return sid.Value; }
    }
}
