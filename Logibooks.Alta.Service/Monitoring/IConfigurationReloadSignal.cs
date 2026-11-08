// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Microsoft.Extensions.Primitives;

namespace Logibooks.Alta.Monitoring;

internal interface IConfigurationReloadSignal
{
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class ConfigurationReloadSignal : IConfigurationReloadSignal, IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly IDisposable _registration;

    public ConfigurationReloadSignal(IConfiguration configuration)
    {
        _registration = ChangeToken.OnChange(configuration.GetReloadToken, Pulse);
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
        _registration.Dispose();
        _signal.Dispose();
    }

    private void Pulse()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Multiple configuration updates collapse into one wake-up.
        }
    }
}
