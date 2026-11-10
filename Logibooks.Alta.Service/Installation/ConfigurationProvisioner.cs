// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

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
        var logDirectory = Directory.CreateDirectory(Path.Combine(directoryPath, ServiceConstants.LogDirectoryName));
        logDirectory.SetAccessControl(CreateLogDirectorySecurity());
    }

    internal static DirectorySecurity CreateDirectorySecurity() => CreateDirectorySecurity(FileSystemRights.ReadAndExecute);

    internal static DirectorySecurity CreateLogDirectorySecurity() => CreateDirectorySecurity(FileSystemRights.Modify);

    private static DirectorySecurity CreateDirectorySecurity(FileSystemRights localServiceRights)
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
            localServiceRights,
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
    string configurationPath,
    IFileAclProtector aclProtector) : IConfigurationProvisioner
{
    public void EnsureConfiguration()
    {
        var configurationDirectory = Path.GetDirectoryName(configurationPath)
            ?? throw new InvalidOperationException("The configuration path has no parent directory.");
        Directory.CreateDirectory(configurationDirectory);
        if (!File.Exists(configurationPath))
        {
            using var template = typeof(ConfigurationProvisioner).Assembly.GetManifestResourceStream(
                ServiceConstants.ConfigurationTemplateFileName)
                ?? throw new InvalidOperationException("The embedded configuration template is missing.");
            using var destination = new FileStream(configurationPath, FileMode.CreateNew, FileAccess.Write);
            template.CopyTo(destination);
        }

        aclProtector.Protect(configurationDirectory, configurationPath);
    }
}
