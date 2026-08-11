using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class VersionConsistencyTests
{
    [Test]
    public void CheckedInVersion_IsConsistentAcrossCodeProjectAndInstaller()
    {
        var root = FindRepositoryRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var projectVersion = props.Descendants("Version").Single().Value;
        var informationalVersion = props.Descendants("InformationalVersion").Single().Value;
        var installer = File.ReadAllText(Path.Combine(
            root,
            "Logibooks.Alta.Setup",
            "Logibooks.Alta.Setup.vdproj"));
        var installerVersion = ReadGroup(installer, "\"ProductVersion\" = \"8:([^\"]+)\"");
        var serviceAssembly = typeof(VersionInfo).Assembly;
        var assemblyVersion = serviceAssembly.GetName().Version;
        var assemblyInformationalVersion = serviceAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        var fileVersion = FileVersionInfo.GetVersionInfo(serviceAssembly.Location).FileVersion;

        Assert.Multiple(() =>
        {
            Assert.That(VersionInfo.AppVersion, Is.EqualTo(projectVersion));
            Assert.That(informationalVersion, Is.EqualTo(projectVersion));
            Assert.That(installerVersion, Is.EqualTo(projectVersion));
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
    public void Installer_UsesStableUpgradeIdentityAndPublishItems()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(
            root,
            "Logibooks.Alta.Setup",
            "Logibooks.Alta.Setup.vdproj"));

        Assert.Multiple(() =>
        {
            Assert.That(installer, Does.Contain("\"UpgradeCode\" = \"8:{36E2F8BB-D73C-47F2-A624-371518531952}\""));
            Assert.That(installer, Does.Contain("\"OutputGroupCanonicalName\" = \"8:PublishItems\""));
            Assert.That(installer, Does.Contain("\"PublishProfilePath\" = \"8:Properties\\\\PublishProfiles\\\\win-x64.pubxml\""));
            Assert.That(installer, Does.Contain("\"Arguments\" = \"8:/Install\""));
            Assert.That(installer, Does.Contain("\"Arguments\" = \"8:/Rollback\""));
            Assert.That(installer, Does.Contain("\"Arguments\" = \"8:/Uninstall\""));
            Assert.That(installer, Does.Contain("\"InstallAllUsers\" = \"11:TRUE\""));
            Assert.That(installer, Does.Contain("\"TargetPlatform\" = \"3:1\""));
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
            "Logibooks.Alta.Setup.vdproj"));
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
            Assert.That(installer, Does.Contain("\"ProductName\" = \"8:Logibooks Alta Adapter\""));
            Assert.That(installer, Does.Contain("\"ARPPRODUCTICON\" = \"8:_ALTA_PRODUCT_ICON\""));
        });
    }

    private static string ReadGroup(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        Assert.That(match.Success, Is.True, $"Pattern was not found: {pattern}");
        return match.Groups[1].Value;
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
