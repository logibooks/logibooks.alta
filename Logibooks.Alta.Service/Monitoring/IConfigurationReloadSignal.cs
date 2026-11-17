// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Logibooks.Alta.Configuration;
using Microsoft.Extensions.Options;

namespace Logibooks.Alta.Monitoring;

internal interface IConfigurationReloadSignal
{
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class ConfigurationReloadSignal : IConfigurationReloadSignal, IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly IDisposable?[] _registrations;

    public ConfigurationReloadSignal(IOptionsMonitor<ServiceOptions> service,
        IOptionsMonitor<CoreOptions> core, IOptionsMonitor<DatabaseMonitoringOptions> databases)
    {
        // Use option publication callbacks so the worker can see the newly bound values when it wakes.
        _registrations =
        [
            service.OnChange((_, _) => Pulse()),
            core.OnChange((_, _) => Pulse()),
            databases.OnChange((_, _) => Pulse())
        ];
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signalTask = _signal.WaitAsync(linkedSource.Token);
        var delayTask = Task.Delay(timeout, linkedSource.Token);
        var completedTask = await Task.WhenAny(signalTask, delayTask);

        linkedSource.Cancel();
        await completedTask;
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
            registration?.Dispose();
        _signal.Dispose();
    }

    private void Pulse()
    {
        try
        {
            _signal.Release();
        }
        catch (Exception exception) when (exception is SemaphoreFullException or ObjectDisposedException)
        {
            // Multiple configuration updates collapse into one wake-up.
        }
    }
}
