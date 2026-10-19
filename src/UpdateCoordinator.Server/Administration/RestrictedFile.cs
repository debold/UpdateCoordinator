using System.Security.AccessControl;
using System.Security.Principal;

namespace UpdateCoordinator.Server.Administration;

/// <summary>Files readable only by SYSTEM, local administrators and the service account (setup token, agent logs).</summary>
internal static class RestrictedFile
{
    /// <summary>Creates a new file with the restricted ACL; an existing file is replaced.</summary>
    public static void Write(string path, ReadOnlySpan<byte> content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in new IdentityReference[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            WindowsIdentity.GetCurrent().User!,
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData, FileShare.None, 4096, FileOptions.None, security);
        stream.Write(content);
    }
}
