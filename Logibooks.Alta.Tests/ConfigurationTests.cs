using Logibooks.Alta.Configuration;
using Logibooks.Alta.Monitoring;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class ConfigurationTests
{
    [Test]
    public void DisabledConfiguration_AllowsEmptyIntegrationSettings()
    {
        var provider = CreateProvider(new ServiceOptions(), new CoreOptions(), new DatabaseMonitoringOptions());

        var result = provider.GetValidated();

        Assert.Multiple(() =>
        {
            Assert.That(result.Service.Enabled, Is.False);
            Assert.That(result.Service.PollIntervalSeconds, Is.EqualTo(60));
            Assert.That(result.MaxConcurrentDatabaseProbes, Is.EqualTo(4));
            Assert.That(result.Databases, Is.Empty);
        });
    }

    [Test]
    public void EnabledConfiguration_ReportsAllRequiredFieldsWithoutValues()
    {
        var provider = CreateProvider(
            new ServiceOptions { Enabled = true, PollIntervalSeconds = 0 },
            new CoreOptions { RequestTimeoutSeconds = 0, JwtRefreshSkewMinutes = -1 },
            new DatabaseMonitoringOptions { MaxConcurrentProbes = 0 });

        var exception = Assert.Throws<RuntimeConfigurationException>(() => provider.GetValidated());

        Assert.That(exception!.Errors, Has.Count.EqualTo(8));
        Assert.That(exception.Message, Does.Contain("Databases.Targets"));
        Assert.That(exception.Message, Does.Contain("Core.BaseUrl"));
        Assert.That(exception.Message, Does.Not.Contain("Password="));
    }

    [TestCase("http://core.example")]
    [TestCase("not-a-url")]
    public void EnabledConfiguration_RequiresHttpsWhenCoreIsEnabled(string baseUrl)
    {
        var core = ValidCore();
        core.BaseUrl = baseUrl;
        var provider = CreateProvider(
            new ServiceOptions { Enabled = true },
            core,
            ValidDatabases());

        var exception = Assert.Throws<RuntimeConfigurationException>(() => provider.GetValidated());

        Assert.That(exception!.Message, Does.Contain("absolute HTTPS URL"));
    }

    [Test]
    public void EnabledConfiguration_AllowsCoreToBeDisabledWithoutCredentials()
    {
        var provider = CreateProvider(
            new ServiceOptions { Enabled = true },
            new CoreOptions { Enabled = false },
            ValidDatabases());

        var result = provider.GetValidated();

        Assert.That(result.Core.Enabled, Is.False);
        Assert.That(result.Databases, Has.Count.EqualTo(1));
    }

    [Test]
    public void EnabledConfiguration_RejectsDuplicateNamesAndInvalidConnections()
    {
        var databases = new DatabaseMonitoringOptions
        {
            Targets =
            [
                Target(" Primary ", "Server=db;Database=one;User Id=alta;Password=secret"),
                Target("primary", "not-a-connection-string")
            ]
        };
        var provider = CreateProvider(new ServiceOptions { Enabled = true }, ValidCore(), databases);

        var exception = Assert.Throws<RuntimeConfigurationException>(() => provider.GetValidated());

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("duplicate enabled name 'primary'"));
            Assert.That(exception.Message, Does.Contain("invalid ConnectionString"));
            Assert.That(exception.Message, Does.Not.Contain("not-a-connection-string"));
        });
    }

    [Test]
    public void EnabledConfiguration_RejectsMissingTargetName()
    {
        var databases = new DatabaseMonitoringOptions
        {
            Targets = [Target(" ", "Server=db;Database=one;Integrated Security=True")]
        };
        var provider = CreateProvider(new ServiceOptions { Enabled = true }, ValidCore(), databases);

        var exception = Assert.Throws<RuntimeConfigurationException>(() => provider.GetValidated());

        Assert.That(exception!.Message, Does.Contain("nonempty Name"));
    }

    [TestCase(0)]
    [TestCase(33)]
    public void Configuration_RejectsConcurrencyOutsideBounds(int concurrency)
    {
        var databases = ValidDatabases();
        databases.MaxConcurrentProbes = concurrency;
        var provider = CreateProvider(new ServiceOptions(), new CoreOptions(), databases);

        var exception = Assert.Throws<RuntimeConfigurationException>(() => provider.GetValidated());

        Assert.That(exception!.Message, Does.Contain("between 1 and 32"));
    }

    [Test]
    public void ValidConfiguration_ReturnsAllAndOnlyEnabledTargets()
    {
        var databases = new DatabaseMonitoringOptions
        {
            Targets =
            [
                Target("one", "Server=db1;Database=one;Integrated Security=True"),
                Target("two", "Server=db2;Database=two;Integrated Security=True"),
                new DatabaseTargetOptions
                {
                    Name = "disabled",
                    Enabled = false,
                    ConnectionString = string.Empty
                }
            ]
        };
        var provider = CreateProvider(new ServiceOptions { Enabled = true }, ValidCore(), databases);

        var result = provider.GetValidated();

        Assert.That(result.Databases.Select(target => target.Name), Is.EqualTo(new[] { "one", "two" }));
    }

    [Test]
    public void ValidConfiguration_IsClonedBeforeItIsReturned()
    {
        var service = new ServiceOptions { Enabled = true, PollIntervalSeconds = 15 };
        var core = ValidCore();
        var databases = ValidDatabases();
        var provider = CreateProvider(service, core, databases);

        var result = provider.GetValidated();
        service.Enabled = false;
        core.Password = "changed";
        databases.Targets[0].ConnectionString = "changed";

        Assert.Multiple(() =>
        {
            Assert.That(result.Service.Enabled, Is.True);
            Assert.That(result.Core.Password, Is.EqualTo("core-secret"));
            Assert.That(result.Databases[0].Name, Is.EqualTo("Primary"));
            Assert.That(result.Databases[0].ConnectionString, Does.Contain("Password=sql-secret"));
        });
    }

    [Test]
    public void Sanitizer_RemovesEveryConnectionStringAndIndividualPassword()
    {
        var first = new RuntimeDatabaseTarget("one", "Server=db1;User Id=alta;Password=sql-secret-one");
        var second = new RuntimeDatabaseTarget("two", "Server=db2;User Id=alta;Password=sql-secret-two");
        var configuration = new RuntimeConfiguration(
            new ServiceOptions { Enabled = true },
            ValidCore(),
            4,
            [first, second]);
        var message = $"Failure on DB1 and db2: {first.ConnectionString}; {second.ConnectionString}; " +
            "sql-secret-one; sql-secret-two; core-secret";

        var result = SensitiveDataSanitizer.Sanitize(message, configuration);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Not.Contain("sql-secret-one"));
            Assert.That(result, Does.Not.Contain("sql-secret-two"));
            Assert.That(result, Does.Not.Contain("core-secret"));
            Assert.That(result, Does.Not.Contain("db1").IgnoreCase);
            Assert.That(result, Does.Not.Contain("db2").IgnoreCase);
            Assert.That(result, Does.Not.Contain(first.ConnectionString));
            Assert.That(result, Does.Not.Contain(second.ConnectionString));
            Assert.That(result, Does.Contain("[REDACTED]"));
        });
    }

    private static RuntimeConfigurationProvider CreateProvider(
        ServiceOptions service,
        CoreOptions core,
        DatabaseMonitoringOptions databases) =>
        new(
            new TestOptionsMonitor<ServiceOptions>(service),
            new TestOptionsMonitor<CoreOptions>(core),
            new TestOptionsMonitor<DatabaseMonitoringOptions>(databases));

    private static DatabaseMonitoringOptions ValidDatabases() => new()
    {
        Targets = [Target(" Primary ", "Server=db;Database=alta;User Id=alta;Password=sql-secret")]
    };

    private static DatabaseTargetOptions Target(string name, string connectionString) => new()
    {
        Name = name,
        ConnectionString = connectionString
    };

    private static CoreOptions ValidCore() => new()
    {
        Enabled = true,
        BaseUrl = "https://core.example/",
        Email = "adapter-alta",
        Password = "core-secret",
        RequestTimeoutSeconds = 30,
        JwtRefreshSkewMinutes = 5
    };
}

[TestFixture]
[NonParallelizable]
public sealed class ConfigurationPathsTests
{
    [Test]
    public void EnvironmentOverride_SelectsContainerConfigurationPath()
    {
        var previous = Environment.GetEnvironmentVariable(
            ConfigurationPaths.ConfigurationPathEnvironmentVariable);
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "alta", "container.json"));
        try
        {
            Environment.SetEnvironmentVariable(
                ConfigurationPaths.ConfigurationPathEnvironmentVariable,
                expected);

            Assert.That(ConfigurationPaths.GetConfigurationPath(), Is.EqualTo(expected));
            Assert.That(ConfigurationPaths.GetDirectoryPath(), Is.EqualTo(Path.GetDirectoryName(expected)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ConfigurationPaths.ConfigurationPathEnvironmentVariable,
                previous);
        }
    }
}
