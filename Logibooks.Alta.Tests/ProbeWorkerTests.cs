using System.Net;
using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Logibooks.Alta.Database;
using Logibooks.Alta.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class ProbeWorkerTests
{
    [Test]
    public async Task DisabledWorker_WaitsWithoutRunningProbes()
    {
        var configuration = ValidConfiguration(enabled: false);
        var databaseProbe = new TestMssqlProbe();
        var coreProbe = new TestCoreProbe();
        var signal = new BlockingReloadSignal();
        var logger = new ListLogger<ProbeWorker>();
        var worker = new ProbeWorker(
            new StaticConfigurationProvider(configuration),
            databaseProbe,
            coreProbe,
            signal,
            logger);

        await worker.StartAsync(CancellationToken.None);
        await signal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(databaseProbe.Calls, Is.Zero);
            Assert.That(coreProbe.Calls, Is.Zero);
            Assert.That(signal.Timeouts.Single(), Is.EqualTo(Timeout.InfiniteTimeSpan));
            Assert.That(logger.Entries.Any(entry => entry.Message.Contains("disabled", StringComparison.Ordinal)), Is.True);
        });
    }

    [Test]
    public async Task ConfigurationReload_EnablesProbesImmediately()
    {
        var provider = new SequenceConfigurationProvider(
            ValidConfiguration(enabled: false),
            ValidConfiguration(enabled: true));
        var databaseProbe = new TestMssqlProbe();
        var coreProbe = new TestCoreProbe();
        var signal = new FirstImmediateReloadSignal();
        var worker = new ProbeWorker(
            provider,
            databaseProbe,
            coreProbe,
            signal,
            new ListLogger<ProbeWorker>());

        await worker.StartAsync(CancellationToken.None);
        await coreProbe.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.That(databaseProbe.Calls, Is.EqualTo(1));
        Assert.That(coreProbe.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task FailedDatabaseProbe_DoesNotPreventCoreProbeAndRedactsSecrets()
    {
        var configuration = ValidConfiguration(enabled: true);
        var databaseProbe = new TestMssqlProbe(new InvalidOperationException(
            $"Database failed: {configuration.DatabaseConnectionString}; sql-secret; core-secret"));
        var coreProbe = new TestCoreProbe();
        var signal = new BlockingReloadSignal();
        var logger = new ListLogger<ProbeWorker>();
        var worker = new ProbeWorker(
            new StaticConfigurationProvider(configuration),
            databaseProbe,
            coreProbe,
            signal,
            logger);

        await worker.StartAsync(CancellationToken.None);
        await coreProbe.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);
        var failure = logger.Entries.Single(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("MSSQL", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(coreProbe.Calls, Is.EqualTo(1));
            Assert.That(failure.Message, Does.Not.Contain("sql-secret"));
            Assert.That(failure.Message, Does.Not.Contain("core-secret"));
            Assert.That(failure.Message, Does.Contain("[REDACTED]"));
        });
    }

    [Test]
    public async Task FailedCoreProbe_DoesNotStopWorker()
    {
        var databaseProbe = new TestMssqlProbe();
        var coreProbe = new TestCoreProbe(new CoreRequestException(HttpStatusCode.BadGateway));
        var signal = new BlockingReloadSignal();
        var logger = new ListLogger<ProbeWorker>();
        var worker = new ProbeWorker(
            new StaticConfigurationProvider(ValidConfiguration(enabled: true)),
            databaseProbe,
            coreProbe,
            signal,
            logger);

        await worker.StartAsync(CancellationToken.None);
        await signal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.That(databaseProbe.Calls, Is.EqualTo(1));
        Assert.That(coreProbe.Calls, Is.EqualTo(1));
        Assert.That(logger.Entries.Any(entry => entry.Level == LogLevel.Warning), Is.True);
        Assert.That(signal.Timeouts.Single(), Is.EqualTo(TimeSpan.FromSeconds(60)));
    }

    private static RuntimeConfiguration ValidConfiguration(bool enabled) => new(
        new ServiceOptions { Enabled = enabled, PollIntervalSeconds = 60 },
        new CoreOptions
        {
            BaseUrl = "https://core.example",
            Email = "adapter-alta",
            Password = "core-secret",
            RequestTimeoutSeconds = 30,
            JwtRefreshSkewMinutes = 5
        },
        "Server=db;User Id=alta;Password=sql-secret");

    private sealed class StaticConfigurationProvider(RuntimeConfiguration configuration)
        : IRuntimeConfigurationProvider
    {
        public RuntimeConfiguration GetValidated() => configuration;
    }

    private sealed class SequenceConfigurationProvider(params RuntimeConfiguration[] configurations)
        : IRuntimeConfigurationProvider
    {
        private int _index;

        public RuntimeConfiguration GetValidated()
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, configurations.Length - 1);
            return configurations[index];
        }
    }

    private sealed class TestMssqlProbe(Exception? exception = null) : IMssqlProbe
    {
        public int Calls { get; private set; }

        public Task ProbeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return exception is null ? Task.CompletedTask : Task.FromException(exception);
        }
    }

    private sealed class TestCoreProbe(Exception? exception = null) : ICoreApiProbe
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProbeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Called.TrySetResult();
            return exception is null ? Task.CompletedTask : Task.FromException(exception);
        }
    }

    private sealed class BlockingReloadSignal : IConfigurationReloadSignal
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<TimeSpan> Timeouts { get; } = [];

        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Timeouts.Add(timeout);
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FirstImmediateReloadSignal : IConfigurationReloadSignal
    {
        private int _calls;

        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}

[TestFixture]
public sealed class ConfigurationReloadSignalTests
{
    [Test]
    public async Task WaitAsync_WakesWhenConfigurationReloads()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();
        using var signal = new ConfigurationReloadSignal(configuration);

        var wait = signal.WaitAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);
        configuration.Reload();

        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(wait.IsCompletedSuccessfully, Is.True);
    }
}
