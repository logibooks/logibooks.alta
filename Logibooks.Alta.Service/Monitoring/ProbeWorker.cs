// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Logibooks.Alta.Database;
using Quartz;

namespace Logibooks.Alta.Monitoring;

internal sealed class ProbeWorker(
    IRuntimeConfigurationProvider configurationProvider,
    IMssqlProbe mssqlProbe,
    ICoreApiProbe coreApiProbe,
    IConfigurationReloadSignal reloadSignal,
    ILogger<ProbeWorker> logger,
    AltaMonitor? monitoring = null,
    TimeProvider? clock = null) : BackgroundService
{
    private bool? _lastEnabledState;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "{ServiceName} version {Version} started",
            ServiceConstants.DisplayName,
            VersionInfo.AppVersion);

        var schedulingClock = clock ?? TimeProvider.System;
        RuntimeConfiguration? previous = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            RuntimeConfiguration configuration;
            try
            {
                // Only schedule admission requires another firing; an active cycle can be the last one.
                configuration = configurationProvider.GetValidated(scheduleFrom: schedulingClock.GetUtcNow());
            }
            catch (RuntimeConfigurationException exception)
            {
                previous = null;
                logger.LogError("Configuration is invalid: {Errors}", string.Join("; ", exception.Errors));
                await reloadSignal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            if (!configuration.Service.Enabled)
            {
                previous = null;
                monitoring?.Clear();
                LogStateChange(false);
                await reloadSignal.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            LogStateChange(true);
            if (previous is null || !AltaMonitor.SameRuntime(previous, configuration))
                await RunCycleAsync(configuration, stoppingToken);

            previous = configuration;
            await using var schedule = new QuartzMonitoringSchedule(
                schedulingClock, token => RunCycleAsync(configuration, token), stoppingToken);
            try
            {
                await schedule.StartAsync(configuration.Service);
            }
            catch (SchedulerException exception)
            {
                logger.LogError("Could not schedule Alta monitoring: {Failure}",
                    SensitiveDataSanitizer.Sanitize(exception.Message, configuration));
                await reloadSignal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }
            await reloadSignal.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
        }
    }

    private async Task RunCycleAsync(RuntimeConfiguration configuration, CancellationToken token)
    {
        var checks = new List<Task>();
        // A monitoring read provides its own database health check.
        if (!configuration.Core.Enabled || monitoring is null)
            checks.Add(RunDatabaseProbesAsync(configuration, token));

        if (configuration.Core.Enabled)
        {
            checks.Add(RunProbeAsync("Logibooks Core", coreApiProbe.ProbeAsync, configuration, token));
            if (monitoring is not null)
                checks.Add(monitoring.RunAsync(configuration, token));
        }
        else
            monitoring?.Clear();

        await Task.WhenAll(checks);
    }

    private async Task RunDatabaseProbesAsync(
        RuntimeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using var limiter = new SemaphoreSlim(configuration.MaxConcurrentDatabaseProbes);
        var probes = configuration.Databases.Select(async database =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                await RunDatabaseProbeAsync(database, configuration, cancellationToken);
            }
            finally
            {
                limiter.Release();
            }
        });

        await Task.WhenAll(probes);
    }

    private async Task RunDatabaseProbeAsync(
        RuntimeDatabaseTarget database,
        RuntimeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            await mssqlProbe.ProbeAsync(database, cancellationToken);
            logger.LogInformation(
                "MSSQL probe succeeded for database target {DatabaseTarget}",
                database.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "MSSQL probe failed for database target {DatabaseTarget}: {Failure}",
                database.Name,
                SensitiveDataSanitizer.Sanitize(exception.Message, configuration));
        }
    }

    private void LogStateChange(bool enabled)
    {
        if (_lastEnabledState == enabled)
        {
            return;
        }

        _lastEnabledState = enabled;
        logger.LogInformation(
            enabled
                ? "Integration monitoring is enabled"
                : "Integration monitoring is disabled; waiting for configuration");
    }

    private async Task RunProbeAsync(
        string name,
        Func<CancellationToken, Task> probe,
        RuntimeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            await probe(cancellationToken);
            logger.LogInformation("{ProbeName} probe succeeded", name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "{ProbeName} probe failed: {Failure}",
                name,
                SensitiveDataSanitizer.Sanitize(exception.Message, configuration));
        }
    }
}
