// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

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
    public void BuildHost_InForeground_UsesConsoleAndRollingFile()
    {
        var configurationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"logibooks-alta-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configurationDirectory);
        var configurationPath = Path.Combine(configurationDirectory, "appsettings.json");
        File.WriteAllText(configurationPath, """
            {
              "Logging": { "File": { "Enabled": true } },
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

            Assert.Multiple(() =>
            {
                Assert.That(providers, Has.Some.Contains("ConsoleLoggerProvider"));
                Assert.That(providers, Has.Some.Contains("SerilogLoggerProvider"));
                Assert.That(providers, Has.None.Contains("EventLogLoggerProvider"));
            });
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
    public void BuildHost_EnvironmentOverridesExternalJsonConfiguration()
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
                  { "Name": "reestr_1_1", "Enabled": true, "ConnectionString": "" }
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
            "Server=tcp:127.0.0.1,1433;Database=reestr_1_1;Password=test-secret;Encrypt=True;TrustServerCertificate=True";

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
    public void ComposeStack_UsesRestoredDatabaseAndLoopbackPorts()
    {
        var root = FindRepositoryRoot();
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));

        Assert.Multiple(() =>
        {
            Assert.That(compose, Does.Contain("mcr.microsoft.com/mssql/server:2025-CU7-ubuntu-24.04"));
            Assert.That(compose, Does.Contain("127.0.0.1:${MSSQL_PORT:-1433}:1433"));
            Assert.That(compose, Does.Contain("127.0.0.1:${ADMINER_PORT:-8083}:8080"));
            Assert.That(compose, Does.Contain("- sql-state:/var/opt/mssql"));
            Assert.That(compose, Does.Contain("external: true"));
            Assert.That(compose, Does.Contain("target: /database"));
            Assert.That(compose, Does.Contain("create_host_path: false"));
            Assert.That(compose, Does.Contain("PATH: /opt/mssql-tools18/bin:"));
            Assert.That(compose, Does.Match(@"sqlcmd -S localhost -U sa -d reestr_1\s"));
            Assert.That(compose, Does.Contain("condition: service_healthy"));
            Assert.That(compose, Does.Contain("image: adminer:5.5.0-standalone"));
            Assert.That(compose, Does.Not.Contain("build:"));
        });
    }

    [Test]
    public void ConfigurationTemplate_UsesDatabaseTargets()
    {
        var production = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Logibooks.Alta.Service", "appsettings.template.json"));
        Assert.That(production, Does.Contain("\"Databases\""));
        Assert.That(production, Does.Not.Contain("ConnectionStrings"));
    }

    [Test]
    public void VisualStudioComposeProject_LaunchesInfrastructureAlongsideWindowsAdapter()
    {
        var root = FindRepositoryRoot();
        var composeProject = XDocument.Load(Path.Combine(root, "docker-compose.dcproj"));
        var solution = File.ReadAllText(Path.Combine(root, "Logibooks.Alta.sln"));
        var serviceDirectory = Path.Combine(root, "Logibooks.Alta.Service");
        var serviceProject = XDocument.Load(Path.Combine(serviceDirectory, "Logibooks.Alta.Service.csproj"));
        using var composeLaunch = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "launchSettings.json")));
        using var serviceLaunch = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            serviceDirectory, "Properties", "launchSettings.json")));
        var profiles = composeLaunch.RootElement.GetProperty("profiles");
        var windowsProfile = serviceLaunch.RootElement.GetProperty("profiles")
            .GetProperty("Alta (Windows development)");

        Assert.Multiple(() =>
        {
            Assert.That(composeProject.Root!.Attribute("Sdk")?.Value, Is.EqualTo("Microsoft.Docker.Sdk"));
            Assert.That(composeProject.Descendants("DockerTargetOS").Single().Value, Is.EqualTo("Linux"));
            Assert.That(composeProject.Descendants("DependencyAwareStart").Single().Value, Is.EqualTo("True"));
            Assert.That(composeProject.Descendants("DockerComposeEnvFilePath").Single().Value, Is.EqualTo(".env"));
            Assert.That(solution, Does.Contain("docker-compose.dcproj"));
            Assert.That(serviceProject.Descendants("DockerComposeProjectPath"), Is.Empty);
            foreach (var profile in profiles.EnumerateObject())
            {
                Assert.That(profile.Value.GetProperty("commandName").GetString(), Is.EqualTo("DockerCompose"));
                var actions = profile.Value.GetProperty("serviceActions").EnumerateObject().ToArray();
                Assert.That(actions.Select(action => action.Name), Is.EquivalentTo(new[] { "mssql", "adminer" }));
                Assert.That(actions.Select(action => action.Value.GetString()), Is.All.EqualTo("StartWithoutDebugging"));
            }
            Assert.That(windowsProfile.GetProperty("commandName").GetString(), Is.EqualTo("Project"));
            Assert.That(windowsProfile.GetProperty("environmentVariables").GetProperty("DOTNET_ENVIRONMENT").GetString(),
                Is.EqualTo(Environments.Development));
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
