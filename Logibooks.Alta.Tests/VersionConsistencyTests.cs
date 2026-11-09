// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class VersionConsistencyTests
{
    [Test]
    public void ProjectVersion_MatchesServiceAssembly()
    {
        var root = FindRepositoryRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var projectVersion = props.Descendants("Version").Single().Value;
        var serviceAssembly = typeof(VersionInfo).Assembly;
        var assemblyVersion = serviceAssembly.GetName().Version;
        var assemblyInformationalVersion = serviceAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        var fileVersion = FileVersionInfo.GetVersionInfo(serviceAssembly.Location).FileVersion;

        Assert.Multiple(() =>
        {
            Assert.That(VersionInfo.AppVersion, Is.EqualTo(projectVersion));
            Assert.That(assemblyVersion?.ToString(), Is.EqualTo($"{projectVersion}.0"));
            Assert.That(assemblyInformationalVersion, Is.EqualTo(projectVersion));
            Assert.That(fileVersion, Is.EqualTo($"{projectVersion}.0"));
        });
    }

    [Test]
    public void ReleaseTag_WhenPresent_MatchesCheckedInVersion()
    {
        var tag = Environment.GetEnvironmentVariable("GITHUB_REF_TYPE") == "tag"
            ? Environment.GetEnvironmentVariable("GITHUB_REF_NAME")
            : null;

        if (tag is null)
        {
            Assert.Pass("Not running for a GitHub release tag.");
        }

        Assert.That(tag, Is.EqualTo($"v{VersionInfo.AppVersion}"));
    }

    [Test]
    public void Installer_UsesStableIdentityAndOffersBothInstallModes()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(
            root,
            "Logibooks.Alta.Setup",
            "Logibooks.Alta.Setup.iss"));

        Assert.Multiple(() =>
        {
            Assert.That(installer, Does.Contain("AppId=Logibooks.Alta.Adapter"));
            Assert.That(installer, Does.Contain("AppVersion={#AppVersion}"));
            Assert.That(installer, Does.Contain("PrivilegesRequired=lowest"));
            Assert.That(installer, Does.Contain("PrivilegesRequiredOverridesAllowed=dialog"));
            Assert.That(installer, Does.Contain("if (CurStep <> ssPostInstall) or not IsAdminInstallMode then"));
            Assert.That(installer, Does.Contain("onlyifdoesntexist uninsneveruninstall"));
        });
    }

    [Test]
    public void ProjectsAndPublishProfile_SupportLinuxAndPreserveWindowsInstallerPublish()
    {
        var root = FindRepositoryRoot();
        var serviceProject = XDocument.Load(Path.Combine(
            root,
            "Logibooks.Alta.Service",
            "Logibooks.Alta.Service.csproj"));
        var testsProject = XDocument.Load(Path.Combine(
            root,
            "Logibooks.Alta.Tests",
            "Logibooks.Alta.Tests.csproj"));
        var publishProfile = XDocument.Load(Path.Combine(
            root,
            "Logibooks.Alta.Service",
            "Properties",
            "PublishProfiles",
            "win-x64.pubxml"));

        Assert.Multiple(() =>
        {
            Assert.That(serviceProject.Descendants("TargetFramework").Single().Value, Is.EqualTo("net10.0"));
            Assert.That(serviceProject.Descendants("RuntimeIdentifiers").Single().Value.Split(';'),
                Is.EquivalentTo(new[] { "win-x64", "linux-x64" }));
            Assert.That(testsProject.Descendants("TargetFramework").Single().Value, Is.EqualTo("net10.0"));
            Assert.That(publishProfile.Descendants("RuntimeIdentifier").Single().Value, Is.EqualTo("win-x64"));
            Assert.That(publishProfile.Descendants("SelfContained").Single().Value, Is.EqualTo("true"));
        });
    }

    [Test]
    public void Program_RegistersWindowsIntegrationOnlyOnWindows()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "Logibooks.Alta.Service", "Program.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(program, Does.Contain("if (OperatingSystem.IsWindows())"));
            Assert.That(program, Does.Contain("AddWindowsService"));
            Assert.That(program, Does.Contain("AddEventLog"));
            Assert.That(program, Does.Contain("Service installation commands are supported only on Windows."));
        });
    }

    [Test]
    public void Branding_UsesAdapterNameAndSuppliedIcon()
    {
        var root = FindRepositoryRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var serviceProject = XDocument.Load(Path.Combine(
            root,
            "Logibooks.Alta.Service",
            "Logibooks.Alta.Service.csproj"));
        var installer = File.ReadAllText(Path.Combine(
            root,
            "Logibooks.Alta.Setup",
            "Logibooks.Alta.Setup.iss"));
        var assets = Path.Combine(root, "Logibooks.Alta.Service", "Assets");

        Assert.Multiple(() =>
        {
            Assert.That(props.Descendants("Product").Single().Value, Is.EqualTo("Logibooks Alta Adapter"));
            Assert.That(serviceProject.Descendants("AssemblyName").Single().Value, Is.EqualTo("Logibooks.Alta.Adapter"));
            Assert.That(
                serviceProject.Descendants("ApplicationIcon").Single().Value,
                Is.EqualTo(@"Assets\Logibooks.Alta.Adapter.ico"));
            Assert.That(ServiceConstants.ServiceName, Is.EqualTo("LogibooksAltaAdapter"));
            Assert.That(ServiceConstants.DisplayName, Is.EqualTo("Logibooks Alta Adapter"));
            Assert.That(File.Exists(Path.Combine(assets, "Logibooks.Alta.Adapter.ico")), Is.True);
            Assert.That(File.Exists(Path.Combine(assets, "Logibooks.Alta.Adapter.png")), Is.True);
            Assert.That(installer, Does.Contain("AppName=Logibooks Alta Adapter"));
            Assert.That(installer, Does.Contain(@"SetupIconFile=..\Logibooks.Alta.Service\Assets\Logibooks.Alta.Adapter.ico"));
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
