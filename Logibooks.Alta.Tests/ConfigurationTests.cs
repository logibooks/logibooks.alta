using Logibooks.Alta.Configuration;
using Logibooks.Alta.Monitoring;
using Microsoft.Extensions.Configuration;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class ConfigurationTests
{
    [Test]
    public void DisabledConfiguration_AllowsEmptyIntegrationSettings()
    {
        var provider = CreateProvider(new ServiceOptions(), new CoreOptions(), null);

        var result = provider.GetValidated();

        Assert.That(result.Service.Enabled, Is.False);
        Assert.That(result.Service.PollIntervalSeconds, Is.EqualTo(60));
    }

    [Test]
    public void EnabledConfiguration_ReportsAllRequiredFieldsWithoutValues()
    {
        var provider = CreateProvider(
            new ServiceOptions { Enabled = true, PollIntervalSeconds = 0 },
            new CoreOptions { RequestTimeoutSeconds = 0, JwtRefreshSkewMinutes = -1 },
            null);

        var exception = Assert.Throws<RuntimeConfigurationException>(() => provider.GetValidated());

        Assert.That(exception!.Errors, Has.Count.EqualTo(7));
        Assert.That(exception.Message, Does.Contain("ConnectionStrings.AltaDatabase"));
        Assert.That(exception.Message, Does.Contain("Core.BaseUrl"));
        Assert.That(exception.Message, Does.Not.Contain("Password="));
    }

    [TestCase("http://core.example")]
    [TestCase("not-a-url")]
    public void EnabledConfiguration_RequiresHttps(string baseUrl)
    {
        var core = ValidCore();
        core.BaseUrl = baseUrl;
        var provider = CreateProvider(
            new ServiceOptions { Enabled = true },
            core,
            "Server=db;User Id=alta;Password=secret;Encrypt=True");

        var exception = Assert.Throws<RuntimeConfigurationException>(() => provider.GetValidated());

        Assert.That(exception!.Message, Does.Contain("absolute HTTPS URL"));
    }

    [Test]
    public void ValidConfiguration_IsClonedBeforeItIsReturned()
    {
        var service = new ServiceOptions { Enabled = true, PollIntervalSeconds = 15 };
        var core = ValidCore();
        var provider = CreateProvider(service, core, "Server=db;Password=secret");

        var result = provider.GetValidated();
        service.Enabled = false;
        core.Password = "changed";

        Assert.Multiple(() =>
        {
            Assert.That(result.Service.Enabled, Is.True);
            Assert.That(result.Core.Password, Is.EqualTo("core-secret"));
            Assert.That(result.DatabaseConnectionString, Does.Contain("Password=secret"));
        });
    }

    [Test]
    public void Sanitizer_RemovesConnectionStringAndIndividualPasswords()
    {
        var configuration = new RuntimeConfiguration(
            new ServiceOptions { Enabled = true },
            ValidCore(),
            "Server=db;User Id=alta;Password=sql-secret");
        var message = $"Failure: {configuration.DatabaseConnectionString}; sql-secret; core-secret";

        var result = SensitiveDataSanitizer.Sanitize(message, configuration);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Not.Contain("sql-secret"));
            Assert.That(result, Does.Not.Contain("core-secret"));
            Assert.That(result, Does.Not.Contain(configuration.DatabaseConnectionString));
            Assert.That(result, Does.Contain("[REDACTED]"));
        });
    }

    private static RuntimeConfigurationProvider CreateProvider(
        ServiceOptions service,
        CoreOptions core,
        string? connectionString)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:AltaDatabase"] = connectionString
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new RuntimeConfigurationProvider(
            new TestOptionsMonitor<ServiceOptions>(service),
            new TestOptionsMonitor<CoreOptions>(core),
            configuration);
    }

    private static CoreOptions ValidCore() => new()
    {
        BaseUrl = "https://core.example/",
        Email = "adapter-alta",
        Password = "core-secret",
        RequestTimeoutSeconds = 30,
        JwtRefreshSkewMinutes = 5
    };
}
