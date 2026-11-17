// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Logibooks.Alta.Database;

namespace Logibooks.Alta.Monitoring;

internal sealed class ProbeWorker(
    IRuntimeConfigurationProvider configurationProvider,
    IMssqlProbe mssqlProbe,
    ICoreApiProbe coreApiProbe,
    IConfigurationReloadSignal reloadSignal,
    ILogger<ProbeWorker> logger,
    AltaCoreClient? altaCore = null,
    IAltaConsole? console = null) : BackgroundService
{
    private bool? _lastEnabledState;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "{ServiceName} version {Version} started",
            ServiceConstants.DisplayName,
            VersionInfo.AppVersion);

        while (!stoppingToken.IsCancellationRequested)
        {
            RuntimeConfiguration configuration;
            try
            {
                configuration = configurationProvider.GetValidated(allowDisabledConsole: console is not null);
            }
            catch (RuntimeConfigurationException exception)
            {
                logger.LogError("Configuration is invalid: {Errors}", string.Join("; ", exception.Errors));
                await reloadSignal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            if (!configuration.Service.Enabled)
            {
                LogStateChange(false);
                if (console is null)
                    await reloadSignal.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                else
                    await RunConsoleUntilReloadAsync(console, stoppingToken);
                continue;
            }

            LogStateChange(true);
            var cycleTasks = new List<Task>
            {
                RunDatabaseProbesAsync(configuration, stoppingToken)
            };
            if (configuration.Core.Enabled)
            {
                cycleTasks.Add(RunProbeAsync(
                    "Logibooks Core",
                    coreApiProbe.ProbeAsync,
                    configuration,
                    stoppingToken));
                if (altaCore is not null)
                    cycleTasks.Add(RefreshWorklistsAsync(altaCore, configuration, stoppingToken));
            }

            await Task.WhenAll(cycleTasks);

            await reloadSignal.WaitAsync(
                TimeSpan.FromSeconds(configuration.Service.PollIntervalSeconds),
                stoppingToken);
        }
    }

    private async Task RunConsoleUntilReloadAsync(IAltaConsole interactiveConsole, CancellationToken stoppingToken)
    {
        using var mode = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var reload = reloadSignal.WaitAsync(Timeout.InfiniteTimeSpan, mode.Token);
        var session = interactiveConsole.RunAsync(mode.Token, stoppingToken);
        try
        {
            await Task.WhenAny(reload, session);
        }
        finally
        {
            await mode.CancelAsync();
            try
            {
                await Task.WhenAll(reload, session);
            }
            catch (OperationCanceledException) when (mode.IsCancellationRequested)
            {
                // Reload or shutdown interrupted console input or a preflight read.
            }
        }
    }

    private async Task RefreshWorklistsAsync(AltaCoreClient client, RuntimeConfiguration configuration, CancellationToken token)
    {
        CoreOptions scope;
        try
        {
            scope = client.CaptureConfiguration();
        }
        catch (CoreConfigurationException ex)
        {
            logger.LogWarning("Could not refresh Core worklists: {Failure}",
                SensitiveDataSanitizer.Sanitize(ex.Message, configuration));
            return;
        }

        await Parallel.ForEachAsync(configuration.Databases,
            new ParallelOptions { MaxDegreeOfParallelism = configuration.MaxConcurrentDatabaseProbes, CancellationToken = token },
            async (database, cancellationToken) =>
            {
                try
                {
                    var cmrs = await client.RefreshWorklistAsync(scope, database.Name, cancellationToken);
                    logger.LogInformation("Core worklist for {DatabaseTarget} contains {Count} CMRs", database.Name, cmrs.Count);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Could not refresh Core worklist for {DatabaseTarget}: {Failure}", database.Name,
                        SensitiveDataSanitizer.Sanitize(AltaCoreClient.DescribeReadFailure(ex), configuration));
                }
            });
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
