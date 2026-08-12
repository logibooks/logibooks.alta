using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace Logibooks.Alta.Installation;

internal interface IFileAclProtector
{
    void Protect(string directoryPath, string filePath);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsFileAclProtector : IFileAclProtector
{
    public void Protect(string directoryPath, string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Configuration ACLs require Windows.");
        }

        new DirectoryInfo(directoryPath).SetAccessControl(CreateDirectorySecurity());
        new FileInfo(filePath).SetAccessControl(CreateFileSecurity());
    }

    internal static DirectorySecurity CreateDirectorySecurity()
    {
        var directorySecurity = new DirectorySecurity();
        directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            Sid(WellKnownSidType.BuiltinAdministratorsSid),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            Sid(WellKnownSidType.LocalSystemSid),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            Sid(WellKnownSidType.LocalServiceSid),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return directorySecurity;
    }

    internal static FileSecurity CreateFileSecurity()
    {
        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            Sid(WellKnownSidType.BuiltinAdministratorsSid),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            Sid(WellKnownSidType.LocalSystemSid),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            Sid(WellKnownSidType.LocalServiceSid),
            FileSystemRights.Read,
            AccessControlType.Allow));
        return fileSecurity;
    }

    private static SecurityIdentifier Sid(WellKnownSidType type) => new(type, null);
}

internal interface IConfigurationProvisioner
{
    void EnsureConfiguration();
}

internal sealed class ConfigurationProvisioner(
    string templatePath,
    string configurationDirectory,
    string configurationPath,
    IFileAclProtector aclProtector) : IConfigurationProvisioner
{
    public void EnsureConfiguration()
    {
        Directory.CreateDirectory(configurationDirectory);
        if (!File.Exists(configurationPath))
        {
            File.Copy(templatePath, configurationPath, overwrite: false);
        }

        aclProtector.Protect(configurationDirectory, configurationPath);
    }
}
