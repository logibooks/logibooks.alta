using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Logibooks.Alta.Database;

namespace Logibooks.Alta.Monitoring;

internal sealed class ProbeWorker(
    IRuntimeConfigurationProvider configurationProvider,
    IMssqlProbe mssqlProbe,
    ICoreApiProbe coreApiProbe,
    IConfigurationReloadSignal reloadSignal,
    ILogger<ProbeWorker> logger) : BackgroundService
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
                configuration = configurationProvider.GetValidated();
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
                await reloadSignal.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            LogStateChange(true);
            await RunProbeAsync("MSSQL", mssqlProbe.ProbeAsync, configuration, stoppingToken);
            await RunProbeAsync("Logibooks Core", coreApiProbe.ProbeAsync, configuration, stoppingToken);

            await reloadSignal.WaitAsync(
                TimeSpan.FromSeconds(configuration.Service.PollIntervalSeconds),
                stoppingToken);
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
