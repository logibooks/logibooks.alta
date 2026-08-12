using Logibooks.Alta.Installation;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class WindowsServiceInstallerTests
{
    [Test]
    public async Task InstallAsync_CreatesConfiguresAndStartsMissingService()
    {
        var provisioner = new TestProvisioner();
        var runner = new RecordingCommandRunner((arguments, _) =>
            arguments.First() == "query"
                ? new CommandResult(1060, string.Empty, string.Empty)
                : Success());
        var installer = new WindowsServiceInstaller(
            @"C:\Program Files\Logibooks\Alta\Logibooks.Alta.Adapter.exe",
            provisioner,
            runner,
            new RecordingServiceStopWaiter());

        await installer.InstallAsync(CancellationToken.None);
        var commands = runner.Commands.Select(command => command.Arguments.First()).ToArray();
        var create = runner.Commands.Single(command => command.Arguments.First() == "create");
        var failure = runner.Commands.Single(command => command.Arguments.First() == "failure");

        Assert.Multiple(() =>
        {
            Assert.That(provisioner.Calls, Is.EqualTo(1));
            Assert.That(commands, Is.EqualTo(new[]
            {
                "/L", "query", "create", "config", "description", "failure", "failureflag", "start"
            }));
            Assert.That(create.Arguments, Does.Contain("delayed-auto"));
            Assert.That(create.Arguments, Does.Contain("NT AUTHORITY\\LocalService"));
            Assert.That(create.Arguments, Does.Contain("\"C:\\Program Files\\Logibooks\\Alta\\Logibooks.Alta.Adapter.exe\""));
            Assert.That(runner.Commands[0].Executable, Is.EqualTo("eventcreate.exe"));
            Assert.That(runner.Commands[0].Arguments, Does.Contain("Logibooks Alta Adapter"));
            Assert.That(failure.Arguments, Does.Contain("86400"));
            Assert.That(failure.Arguments, Does.Contain("restart/5000/restart/15000/restart/60000"));
        });
    }

    [Test]
    public async Task InstallAsync_IsIdempotentForExistingService()
    {
        var runner = new RecordingCommandRunner((_, _) => Success());
        var installer = new WindowsServiceInstaller(
            @"C:\Alta\Logibooks.Alta.Adapter.exe",
            new TestProvisioner(),
            runner,
            new RecordingServiceStopWaiter());

        await installer.InstallAsync(CancellationToken.None);

        Assert.That(
            runner.Commands.Any(command => command.Arguments.First() == "create"),
            Is.False);
        Assert.That(
            runner.Commands.Any(command => command.Arguments.First() == "config"),
            Is.True);
    }

    [Test]
    public async Task InstallAsync_ToleratesAlreadyRunningService()
    {
        var runner = new RecordingCommandRunner((arguments, _) =>
            arguments.First() == "start"
                ? new CommandResult(1, "FAILED 1056", string.Empty)
                : Success());
        var installer = new WindowsServiceInstaller(
            @"C:\Alta\Logibooks.Alta.Adapter.exe",
            new TestProvisioner(),
            runner,
            new RecordingServiceStopWaiter());

        Assert.That(
            async () => await installer.InstallAsync(CancellationToken.None),
            Throws.Nothing);
    }

    [Test]
    public async Task UninstallAsync_StopsAndDeletesExistingService()
    {
        var runner = new RecordingCommandRunner((arguments, _) =>
            arguments.First() == "stop"
                ? new CommandResult(1, "FAILED 1062", string.Empty)
                : Success());
        var waiter = new RecordingServiceStopWaiter();
        var installer = new WindowsServiceInstaller(
            @"C:\Alta\Logibooks.Alta.Adapter.exe",
            new TestProvisioner(),
            runner,
            waiter);

        await installer.UninstallAsync(CancellationToken.None);

        Assert.That(
            runner.Commands.Select(command => command.Arguments.First()),
            Is.EqualTo(new[] { "query", "stop", "delete" }));
        Assert.That(waiter.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task UninstallAsync_DoesNothingWhenServiceIsMissing()
    {
        var runner = new RecordingCommandRunner((_, _) =>
            new CommandResult(1060, string.Empty, string.Empty));
        var installer = new WindowsServiceInstaller(
            @"C:\Alta\Logibooks.Alta.Adapter.exe",
            new TestProvisioner(),
            runner,
            new RecordingServiceStopWaiter());

        await installer.UninstallAsync(CancellationToken.None);

        Assert.That(runner.Commands, Has.Count.EqualTo(1));
        Assert.That(runner.Commands[0].Arguments.First(), Is.EqualTo("query"));
    }

    private static CommandResult Success() => new(0, string.Empty, string.Empty);

    private sealed class TestProvisioner : IConfigurationProvisioner
    {
        public int Calls { get; private set; }
        public void EnsureConfiguration() => Calls++;
    }

    private sealed class RecordingCommandRunner(
        Func<IReadOnlyCollection<string>, int, CommandResult> resultFactory) : ICommandRunner
    {
        public List<(string Executable, IReadOnlyCollection<string> Arguments)> Commands { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            IReadOnlyCollection<string> arguments,
            CancellationToken cancellationToken)
        {
            Commands.Add((executable, arguments));
            return Task.FromResult(resultFactory(arguments, Commands.Count));
        }
    }

    private sealed class RecordingServiceStopWaiter : IServiceStopWaiter
    {
        public int Calls { get; private set; }

        public void WaitForStopped(TimeSpan timeout)
        {
            Calls++;
            Assert.That(timeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
        }
    }
}

[TestFixture]
public sealed class ConfigurationProvisionerTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"logibooks-alta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void EnsureConfiguration_CreatesFileAndAppliesAcl()
    {
        var template = Path.Combine(_root, "template.json");
        var directory = Path.Combine(_root, "ProgramData");
        var configuration = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(template, "template-content");
        var acl = new RecordingAclProtector();
        var provisioner = new ConfigurationProvisioner(template, directory, configuration, acl);

        provisioner.EnsureConfiguration();

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(configuration), Is.EqualTo("template-content"));
            Assert.That(acl.Paths, Is.EqualTo(new[] { (directory, configuration) }));
        });
    }

    [Test]
    public void EnsureConfiguration_PreservesExistingFileDuringUpgrade()
    {
        var template = Path.Combine(_root, "template.json");
        var directory = Path.Combine(_root, "ProgramData");
        var configuration = Path.Combine(directory, "appsettings.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(template, "new-template");
        File.WriteAllText(configuration, "existing-settings");
        var acl = new RecordingAclProtector();
        var provisioner = new ConfigurationProvisioner(template, directory, configuration, acl);

        provisioner.EnsureConfiguration();

        Assert.That(File.ReadAllText(configuration), Is.EqualTo("existing-settings"));
        Assert.That(acl.Paths, Has.Count.EqualTo(1));
    }

    private sealed class RecordingAclProtector : IFileAclProtector
    {
        public List<(string Directory, string File)> Paths { get; } = [];
        public void Protect(string directoryPath, string filePath) => Paths.Add((directoryPath, filePath));
    }
}

[TestFixture]
[Platform(Include = "Win")]
[SupportedOSPlatform("windows")]
public sealed class WindowsFileAclProtectorTests
{
    [Test]
    public void DirectoryAcl_IsProtectedAndGrantsOnlyRequiredPrincipals()
    {
        var security = WindowsFileAclProtector.CreateDirectorySecurity();
        var rules = Rules(security);

        Assert.Multiple(() =>
        {
            Assert.That(security.AreAccessRulesProtected, Is.True);
            Assert.That(rules.Select(rule => rule.IdentityReference.Value), Is.EquivalentTo(new[]
            {
                Sid(WellKnownSidType.BuiltinAdministratorsSid),
                Sid(WellKnownSidType.LocalSystemSid),
                Sid(WellKnownSidType.LocalServiceSid)
            }));
            Assert.That(RuleFor(rules, WellKnownSidType.BuiltinAdministratorsSid)
                .FileSystemRights.HasFlag(FileSystemRights.FullControl), Is.True);
            Assert.That(RuleFor(rules, WellKnownSidType.LocalSystemSid)
                .FileSystemRights.HasFlag(FileSystemRights.FullControl), Is.True);
            Assert.That(RuleFor(rules, WellKnownSidType.LocalServiceSid)
                .FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute), Is.True);
        });
    }

    [Test]
    public void FileAcl_GrantsLocalServiceReadWithoutWrite()
    {
        var security = WindowsFileAclProtector.CreateFileSecurity();
        var rules = Rules(security);
        var localService = RuleFor(rules, WellKnownSidType.LocalServiceSid);

        Assert.Multiple(() =>
        {
            Assert.That(security.AreAccessRulesProtected, Is.True);
            Assert.That(localService.FileSystemRights.HasFlag(FileSystemRights.Read), Is.True);
            Assert.That(localService.FileSystemRights.HasFlag(FileSystemRights.Write), Is.False);
            Assert.That(localService.AccessControlType, Is.EqualTo(AccessControlType.Allow));
        });
    }

    private static IReadOnlyCollection<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

    private static FileSystemAccessRule RuleFor(
        IEnumerable<FileSystemAccessRule> rules,
        WellKnownSidType type) =>
        rules.Single(rule => rule.IdentityReference.Value == Sid(type));

    private static string Sid(WellKnownSidType type) =>
        new SecurityIdentifier(type, null).Value;
}
