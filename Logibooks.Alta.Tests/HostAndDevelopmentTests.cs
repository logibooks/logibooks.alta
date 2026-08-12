using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Xml.Linq;

namespace Logibooks.Alta.Tests;

[TestFixture]
[NonParallelizable]
public sealed class HostConstructionTests
{
    [Test]
    public void BuildHost_UsesConsoleEverywhereAndEventLogOnlyOnWindows()
    {
        var configurationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"logibooks-alta-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configurationDirectory);
        var configurationPath = Path.Combine(configurationDirectory, "appsettings.json");
        File.WriteAllText(configurationPath, """
            {
              "Service": { "Enabled": false, "PollIntervalSeconds": 60 },
              "Databases": { "MaxConcurrentProbes": 4, "Targets": [] },
              "Core": { "Enabled": true, "RequestTimeoutSeconds": 30, "JwtRefreshSkewMinutes": 5 }
            }
            """);
        var originalPath = Environment.GetEnvironmentVariable(
            Configuration.ConfigurationPaths.ConfigurationPathEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                Configuration.ConfigurationPaths.ConfigurationPathEnvironmentVariable,
                configurationPath);
            using var host = ProgramEntry.BuildHost([]);
            var providers = host.Services.GetServices<ILoggerProvider>()
                .Select(provider => provider.GetType().FullName ?? provider.GetType().Name)
                .ToArray();

            Assert.That(providers, Has.Some.Contains("ConsoleLoggerProvider"));
            if (OperatingSystem.IsWindows())
            {
                Assert.That(providers, Has.Some.Contains("EventLogLoggerProvider"));
            }
            else
            {
                Assert.That(providers, Has.None.Contains("EventLogLoggerProvider"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Configuration.ConfigurationPaths.ConfigurationPathEnvironmentVariable,
                originalPath);
            Directory.Delete(configurationDirectory, recursive: true);
        }
    }

    [Test]
    public void BuildHost_ContainerEnvironmentOverridesMountedJsonConfiguration()
    {
        var configurationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"logibooks-alta-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configurationDirectory);
        var configurationPath = Path.Combine(configurationDirectory, "appsettings.json");
        File.WriteAllText(configurationPath, """
            {
              "Service": { "Enabled": true, "PollIntervalSeconds": 60 },
              "Databases": {
                "MaxConcurrentProbes": 4,
                "Targets": [
                  { "Name": "AltaInstance3", "Enabled": true, "ConnectionString": "" }
                ]
              },
              "Core": { "Enabled": false, "RequestTimeoutSeconds": 30, "JwtRefreshSkewMinutes": 5 }
            }
            """);
        var configurationVariable = Configuration.ConfigurationPaths.ConfigurationPathEnvironmentVariable;
        const string connectionVariable = "Databases__Targets__0__ConnectionString";
        var originalPath = Environment.GetEnvironmentVariable(configurationVariable);
        var originalConnection = Environment.GetEnvironmentVariable(connectionVariable);
        const string expectedConnection =
            "Server=mssql,1433;Database=gtk;User ID=alta_adapter;Password=test-secret;Encrypt=True;TrustServerCertificate=True";

        try
        {
            Environment.SetEnvironmentVariable(configurationVariable, configurationPath);
            Environment.SetEnvironmentVariable(connectionVariable, expectedConnection);
            using var host = ProgramEntry.BuildHost([]);
            var configuration = host.Services
                .GetRequiredService<Configuration.IRuntimeConfigurationProvider>()
                .GetValidated();

            Assert.That(configuration.Databases.Single().ConnectionString, Is.EqualTo(expectedConnection));
        }
        finally
        {
            Environment.SetEnvironmentVariable(configurationVariable, originalPath);
            Environment.SetEnvironmentVariable(connectionVariable, originalConnection);
            Directory.Delete(configurationDirectory, recursive: true);
        }
    }

    [Test]
    [Platform(Exclude = "Win")]
    public async Task InstallerCommand_ReturnsClearFailureOnNonWindows()
    {
        var exitCode = await ProgramEntry.RunAsync(["/Install"]);

        Assert.That(exitCode, Is.EqualTo(1));
    }
}

[TestFixture]
public sealed class DevelopmentArtifactTests
{
    [Test]
    public void ComposeStack_UsesPinnedServicesAndSafeDevelopmentDefaults()
    {
        var root = FindRepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        var config = File.ReadAllText(Path.Combine(root, "docker", "config", "appsettings.json"));
        var restore = File.ReadAllText(Path.Combine(root, "docker", "mssql", "restore-gtk.sh"));
        var adminer = File.ReadAllText(Path.Combine(root, "docker", "adminer", "Dockerfile"));

        Assert.Multiple(() =>
        {
            Assert.That(compose, Does.Contain("mcr.microsoft.com/mssql/server:2025-CU7-ubuntu-24.04"));
            Assert.That(compose, Does.Contain("127.0.0.1:${MSSQL_PORT:-1433}:1433"));
            Assert.That(compose, Does.Contain("127.0.0.1:${ADMINER_PORT:-8083}:8080"));
            Assert.That(compose, Does.Contain("condition: service_completed_successfully"));
            Assert.That(compose, Does.Contain("ALTA_DB_PASSWORD"));
            Assert.That(config, Does.Contain("\"Core\": {"));
            Assert.That(config, Does.Contain("\"Enabled\": false"));
            Assert.That(restore, Does.Contain("RESTORE VERIFYONLY"));
            Assert.That(restore, Does.Contain("RESTORE FILELISTONLY"));
            Assert.That(restore, Does.Contain("DB_ID(N'gtk')"));
            Assert.That(restore, Does.Contain("DATABASEPROPERTYEX(N'gtk', N'Status')"));
            Assert.That(restore, Does.Contain("did not become ONLINE within 120 seconds"));
            Assert.That(restore, Does.Contain("ALTER ROLE [db_datareader] ADD MEMBER [alta_adapter]"));
            Assert.That(restore, Does.Contain("must not be a sysadmin"));
            Assert.That(restore, Does.Not.Contain("ALTER SERVER ROLE"));
            Assert.That(adminer, Does.Contain("FROM adminer:5.5.0-standalone"));
            Assert.That(adminer, Does.Contain("pdo_dblib"));
        });
    }

    [Test]
    public void ConfigurationTemplates_DoNotRetainLegacySingleDatabaseContract()
    {
        var root = FindRepositoryRoot();
        var production = File.ReadAllText(Path.Combine(
            root,
            "Logibooks.Alta.Service",
            "appsettings.template.json"));
        var development = File.ReadAllText(Path.Combine(
            root,
            "docker",
            "config",
            "appsettings.json"));

        Assert.Multiple(() =>
        {
            Assert.That(production, Does.Contain("\"Databases\""));
            Assert.That(development, Does.Contain("\"Databases\""));
            Assert.That(production, Does.Not.Contain("ConnectionStrings"));
            Assert.That(development, Does.Not.Contain("AltaDatabase"));
        });
    }

    [Test]
    public void ResetHelper_RequiresExplicitConfirmationAndGuardsDestructivePath()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "Reset-Development.ps1"));

        Assert.Multiple(() =>
        {
            Assert.That(script, Does.Contain("Type RESET"));
            Assert.That(script, Does.Contain("GetPathRoot"));
            Assert.That(script, Does.Contain("GetFileName($dataDirectory) -ne 'AltaInstance3'"));
            Assert.That(script, Does.Contain("Test-PathWithin $dataDirectory $repositoryRoot"));
            Assert.That(script, Does.Contain("Test-PathWithin $dataDirectory $backupDirectory"));
            Assert.That(script, Does.Contain("Get-ChildItem -LiteralPath $dataDirectory -Force | Remove-Item"));
        });
    }

    [Test]
    public void VisualStudioComposeProject_MirrorsCoreLaunchStructure()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "docker-compose.dcproj"));
        var solution = File.ReadAllText(Path.Combine(root, "Logibooks.Alta.sln"));
        var serviceProject = XDocument.Load(Path.Combine(
            root,
            "Logibooks.Alta.Service",
            "Logibooks.Alta.Service.csproj"));
        using var launchDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "launchSettings.json")));
        var profiles = launchDocument.RootElement.GetProperty("profiles");
        var debugProfile = profiles.GetProperty("Docker Compose");

        Assert.Multiple(() =>
        {
            Assert.That(project.Root!.Attribute("Sdk")?.Value, Is.EqualTo("Microsoft.Docker.Sdk"));
            Assert.That(project.Descendants("DockerTargetOS").Single().Value, Is.EqualTo("Linux"));
            Assert.That(project.Descendants("DependencyAwareStart").Single().Value, Is.EqualTo("True"));
            var environmentFile = project.Descendants("DockerComposeEnvFilePath").Single();
            Assert.That(environmentFile.Value, Is.EqualTo(".env"));
            Assert.That(
                environmentFile.Attribute("Condition")?.Value,
                Is.EqualTo("Exists('$(MSBuildProjectDirectory)\\.env')"));
            Assert.That(project.Descendants("DockerDevelopmentMode").Single().Value, Is.EqualTo("Regular"));
            Assert.That(solution, Does.Contain("docker-compose.dcproj"));
            Assert.That(
                serviceProject.Descendants("DockerComposeProjectPath").Single().Value,
                Is.EqualTo(@"..\docker-compose.dcproj"));
            Assert.That(debugProfile.GetProperty("commandName").GetString(), Is.EqualTo("DockerCompose"));
            Assert.That(
                debugProfile.GetProperty("serviceActions").GetProperty("adapter").GetString(),
                Is.EqualTo("StartDebugging"));
            Assert.That(profiles.TryGetProperty("Docker Compose (Adminer)", out _), Is.True);
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose.yml")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
