// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Collections.Concurrent;
using System.Net;
using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Logibooks.Alta.Database;
using Logibooks.Alta.Monitoring;
using Microsoft.Extensions.Logging;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class ProbeWorkerTests
{
    [Test]
    public async Task DisabledWorker_WaitsWithoutRunningProbes()
    {
        var databaseProbe = new TestMssqlProbe();
        var coreProbe = new TestCoreProbe();
        var signal = new BlockingReloadSignal();
        var logger = new ListLogger<ProbeWorker>();
        var worker = CreateWorker(ValidConfiguration(enabled: false), databaseProbe, coreProbe, signal, logger);

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
    public async Task ExpiredScheduleIsRejectedBeforeStartupRunsAnyProbes()
    {
        var settings = new ServiceOptions { Enabled = true, MonitoringCron = "0 0 8 1 1 ? 2026", MonitoringTimeZone = "UTC" };
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        using var provider = new RuntimeConfigurationProvider(new TestOptionsMonitor<ServiceOptions>(settings),
            new TestOptionsMonitor<CoreOptions>(ValidConfiguration(enabled: true).Core),
            new TestOptionsMonitor<DatabaseMonitoringOptions>(new()
            {
                Targets = [new DatabaseTargetOptions { Enabled = true, Name = "Alta", ConnectionString = "Server=db" }]
            }), new CoreUris(false));
        var databaseProbe = new TestMssqlProbe();
        var coreProbe = new TestCoreProbe();
        var signal = new BlockingReloadSignal();
        var logger = new ListLogger<ProbeWorker>();
        using var worker = new ProbeWorker(provider, databaseProbe, coreProbe, signal, logger, clock: clock);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await signal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Multiple(() =>
        {
            Assert.That(databaseProbe.Calls, Is.Zero);
            Assert.That(coreProbe.Calls, Is.Zero);
            Assert.That(logger.Entries.Any(entry => entry.Level == LogLevel.Error &&
                entry.Message.Contains("must schedule a future firing", StringComparison.Ordinal)), Is.True);
        });
    }

    [Test]
    public async Task ConfigurationReload_EnablesAllDatabaseProbesImmediately()
    {
        var provider = new SequenceConfigurationProvider(
            ValidConfiguration(enabled: false),
            ValidConfiguration(enabled: true, databaseCount: 2));
        var databaseProbe = new TestMssqlProbe();
        var coreProbe = new TestCoreProbe();
        var signal = new FirstImmediateReloadSignal();
        var worker = new ProbeWorker(provider, databaseProbe, coreProbe, signal, new ListLogger<ProbeWorker>());

        await worker.StartAsync(CancellationToken.None);
        await coreProbe.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.That(databaseProbe.Calls, Is.EqualTo(2));
        Assert.That(databaseProbe.TargetNames, Is.EquivalentTo(new[] { "database-1", "database-2" }));
        Assert.That(coreProbe.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task FailedDatabaseProbe_DoesNotPreventOtherDatabasesOrCoreAndRedactsSecrets()
    {
        var configuration = ValidConfiguration(enabled: true, databaseCount: 2);
        var failingDatabase = configuration.Databases[0];
        var databaseProbe = new TestMssqlProbe((database, _) =>
            database.Name == failingDatabase.Name
                ? Task.FromException(new InvalidOperationException(
                    $"Database failed: {database.ConnectionString}; sql-secret-1; core-secret"))
                : Task.CompletedTask);
        var coreProbe = new TestCoreProbe();
        var signal = new BlockingReloadSignal();
        var logger = new ListLogger<ProbeWorker>();
        var worker = CreateWorker(configuration, databaseProbe, coreProbe, signal, logger);

        await worker.StartAsync(CancellationToken.None);
        await signal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);
        var failure = logger.Entries.Single(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains(failingDatabase.Name, StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(databaseProbe.Calls, Is.EqualTo(2));
            Assert.That(coreProbe.Calls, Is.EqualTo(1));
            Assert.That(failure.Message, Does.Not.Contain("sql-secret-1"));
            Assert.That(failure.Message, Does.Not.Contain("core-secret"));
            Assert.That(failure.Message, Does.Contain("[REDACTED]"));
        });
    }

    [Test]
    public async Task CoreDisabled_RunsDatabaseProbesWithoutCoreRequest()
    {
        var configuration = ValidConfiguration(enabled: true);
        configuration.Core.Enabled = false;
        var databaseProbe = new TestMssqlProbe();
        var coreProbe = new TestCoreProbe();
        var signal = new BlockingReloadSignal();
        var worker = CreateWorker(
            configuration,
            databaseProbe,
            coreProbe,
            signal,
            new ListLogger<ProbeWorker>());

        await worker.StartAsync(CancellationToken.None);
        await signal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.That(databaseProbe.Calls, Is.EqualTo(1));
        Assert.That(coreProbe.Calls, Is.Zero);
    }

    [Test]
    public async Task DatabaseProbes_RespectConfiguredConcurrencyBound()
    {
        var configuration = ValidConfiguration(enabled: true, databaseCount: 8, concurrency: 2);
        var databaseProbe = new ConcurrentMssqlProbe();
        var signal = new BlockingReloadSignal();
        var worker = CreateWorker(
            configuration,
            databaseProbe,
            new TestCoreProbe(),
            signal,
            new ListLogger<ProbeWorker>());

        await worker.StartAsync(CancellationToken.None);
        await signal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await worker.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(databaseProbe.Calls, Is.EqualTo(8));
            Assert.That(databaseProbe.MaximumConcurrency, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task CoreProbe_StartsWhileDatabaseProbeIsStillRunning()
    {
        var coreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var databaseProbe = new TestMssqlProbe(async (_, cancellationToken) =>
            await coreStarted.Task.WaitAsync(cancellationToken));
        var coreProbe = new TestCoreProbe(onProbe: () => coreStarted.TrySetResult());
        var signal = new BlockingReloadSignal();
        var worker = CreateWorker(
            ValidConfiguration(enabled: true),
            databaseProbe,
            coreProbe,
            signal,
            new ListLogger<ProbeWorker>());

        await worker.StartAsync(CancellationToken.None);
        await signal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.That(databaseProbe.Calls, Is.EqualTo(1));
        Assert.That(coreProbe.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task ReloadDuringStartup_AppliesTheNewSchedule()
    {
        using var signal = ConfigurationReloadSignalTests.CreateSignal(out var reload);
        var firstProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var databaseProbe = new TestMssqlProbe(async (_, cancellationToken) =>
        {
            if (firstProbeStarted.TrySetResult())
            {
                await releaseFirstProbe.Task.WaitAsync(cancellationToken);
            }
            else
            {
                secondProbeStarted.TrySetResult();
            }
        });
        var configuration = ValidConfiguration(enabled: true);
        configuration.Core.Enabled = false;
        var worker = CreateWorker(
            configuration,
            databaseProbe,
            new TestCoreProbe(),
            signal,
            new ListLogger<ProbeWorker>());

        await worker.StartAsync(CancellationToken.None);
        await firstProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        configuration.Service.MonitoringCron = "0/1 * * * * ?";
        reload();
        releaseFirstProbe.TrySetResult();
        await secondProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.That(databaseProbe.Calls, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task ReloadSwitchesModesOnlyAfterThePreviousModeHasFinished()
    {
        using var signal = ConfigurationReloadSignalTests.CreateSignal(out var reload);
        var disabled = ValidConfiguration(enabled: false);
        var enabled = ValidConfiguration(enabled: true);
        enabled.Core.Enabled = false;
        var provider = new SequenceConfigurationProvider(disabled, enabled, disabled);
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var databaseProbe = new TestMssqlProbe(async (_, token) =>
        {
            probeStarted.TrySetResult();
            await releaseProbe.Task.WaitAsync(token);
        });
        var console = new SwitchingConsole();
        using var worker = new ProbeWorker(provider, databaseProbe, new TestCoreProbe(), signal,
            new ListLogger<ProbeWorker>(), console: console);
        var deadline = TimeSpan.FromSeconds(3);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await console.Entered.Task.WaitAsync(deadline);
            reload();
            await console.Cancelled.Task.WaitAsync(deadline);
            Assert.That(probeStarted.Task.IsCompleted, Is.False);
            console.FinishSubmission.TrySetResult();
            await probeStarted.Task.WaitAsync(deadline);

            reload();
            Assert.That(console.Reopened.Task.IsCompleted, Is.False);
            releaseProbe.TrySetResult();
            await console.Reopened.Task.WaitAsync(deadline);
            Assert.That(databaseProbe.Calls, Is.EqualTo(1));
        }
        finally
        {
            console.FinishSubmission.TrySetResult();
            releaseProbe.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class SwitchingConsole : IAltaConsole
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishSubmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reopened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(CancellationToken modeToken, CancellationToken stoppingToken)
        {
            if (!Entered.TrySetResult())
            {
                Reopened.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, modeToken);
                return;
            }

            using var registration = modeToken.Register(() => Cancelled.TrySetResult());
            await FinishSubmission.Task.WaitAsync(stoppingToken);
        }
    }

    private static ProbeWorker CreateWorker(
        RuntimeConfiguration configuration,
        IMssqlProbe databaseProbe,
        ICoreApiProbe coreProbe,
        IConfigurationReloadSignal signal,
        ILogger<ProbeWorker> logger) =>
        new(new StaticConfigurationProvider(configuration), databaseProbe, coreProbe, signal, logger);

    private static RuntimeConfiguration ValidConfiguration(
        bool enabled,
        int databaseCount = 1,
        int concurrency = 4) => new(
        new ServiceOptions { Enabled = enabled },
        new CoreOptions
        {
            Enabled = true,
            BaseUrl = "https://core.example",
            Email = "adapter-alta",
            Password = "core-secret",
            RequestTimeoutSeconds = 30,
            JwtRefreshSkewMinutes = 5
        },
        concurrency,
        Enumerable.Range(1, databaseCount)
            .Select(index => new RuntimeDatabaseTarget(
                $"database-{index}",
                $"Server=db{index};User Id=alta;Password=sql-secret-{index}"))
            .ToArray());

    private sealed class StaticConfigurationProvider(RuntimeConfiguration configuration)
        : IRuntimeConfigurationProvider
    {
        public RuntimeConfiguration GetValidated(bool allowDisabledConsole = false, DateTimeOffset? scheduleFrom = null) => configuration;
    }

    private sealed class SequenceConfigurationProvider(params RuntimeConfiguration[] configurations)
        : IRuntimeConfigurationProvider
    {
        private int _index;

        public RuntimeConfiguration GetValidated(bool allowDisabledConsole = false, DateTimeOffset? scheduleFrom = null)
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, configurations.Length - 1);
            return configurations[index];
        }
    }

    private sealed class TestMssqlProbe(
        Func<RuntimeDatabaseTarget, CancellationToken, Task>? probe = null) : IMssqlProbe
    {
        private int _calls;
        public int Calls => _calls;
        public ConcurrentQueue<string> TargetNames { get; } = new();

        public async Task ProbeAsync(RuntimeDatabaseTarget database, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            TargetNames.Enqueue(database.Name);
            if (probe is not null)
            {
                await probe(database, cancellationToken);
            }
        }
    }

    private sealed class ConcurrentMssqlProbe : IMssqlProbe
    {
        private int _active;
        private int _calls;
        private int _maximumConcurrency;
        public int Calls => _calls;
        public int MaximumConcurrency => _maximumConcurrency;

        public async Task ProbeAsync(RuntimeDatabaseTarget database, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            int observed;
            do
            {
                observed = _maximumConcurrency;
            }
            while (active > observed &&
                   Interlocked.CompareExchange(ref _maximumConcurrency, active, observed) != observed);

            try
            {
                await Task.Delay(40, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class TestCoreProbe(
        Exception? exception = null,
        Action? onProbe = null) : ICoreApiProbe
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProbeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Called.TrySetResult();
            onProbe?.Invoke();
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
    internal static ConfigurationReloadSignal CreateSignal(out Action reload)
    {
        var options = new TestOptionsMonitor<ServiceOptions>(new());
        reload = () => options.Set(new());
        return new ConfigurationReloadSignal(options, new TestOptionsMonitor<CoreOptions>(new()),
            new TestOptionsMonitor<DatabaseMonitoringOptions>(new()));
    }

    [Test]
    public async Task WaitAsync_WakesWhenNewOptionsArePublished()
    {
        using var signal = CreateSignal(out var reload);

        var wait = signal.WaitAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);
        reload();

        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(wait.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task ReloadBeforeWait_RemainsPendingUntilConsumed()
    {
        using var signal = CreateSignal(out var reload);

        reload();
        await signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }
}
