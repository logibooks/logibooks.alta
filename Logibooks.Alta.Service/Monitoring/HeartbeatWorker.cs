// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Net;
using System.Text.Json;
using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Microsoft.Extensions.Options;

namespace Logibooks.Alta.Monitoring;

internal sealed class HeartbeatWorker(
    IOptionsMonitor<ServiceOptions> serviceOptions,
    IOptionsMonitor<CoreOptions> coreOptions,
    ICoreSessionManager sessions,
    AltaCoreClient core,
    TimeProvider clock,
    ILogger<HeartbeatWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromMinutes(1);
    private long? _lastWarning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var serviceSubscription = serviceOptions.OnChange((_, _) => reloaded.TrySetResult());
            using var coreSubscription = coreOptions.OnChange((_, _) => reloaded.TrySetResult());
            using var configurationLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            var run = RunConfigurationAsync(configurationLifetime.Token);
            if (await Task.WhenAny(run, reloaded.Task) == reloaded.Task)
                await configurationLifetime.CancelAsync();

            try
            {
                await run;
            }
            catch (OperationCanceledException) when (configurationLifetime.IsCancellationRequested)
            {
                // Finish the old attempt before entering the new configuration.
            }
        }
    }

    private async Task RunConfigurationAsync(CancellationToken token)
    {
        try
        {
            var scope = coreOptions.CurrentValue;
            if (serviceOptions.CurrentValue.Enabled && scope.Enabled)
            {
                core.ValidateConfiguration(scope);
                await RunCadenceAsync(scope, token);
            }
        }
        catch (CoreConfigurationException exception)
        {
            if (!token.IsCancellationRequested)
                Warn(exception.Message);
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }

    private async Task RunCadenceAsync(CoreOptions scope, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var started = clock.GetTimestamp();
            var session = sessions.GetHeartbeatSession(scope);
            if (session is { HasUnexpiredToken: true, IntervalSeconds: null })
            {
                await WaitForNextAttemptAsync(RecheckDelay, session.IntervalChanged, token);
                continue;
            }

            var routeUnavailable = await SendAsync(scope, session.IntervalSeconds, started, token);
            var current = sessions.GetHeartbeatSession(scope);
            var cadence = current.IntervalSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : RecheckDelay;
            if (routeUnavailable && cadence < RecheckDelay)
                cadence = RecheckDelay;

            // Choose the first future tick after the attempt. Elapsed ticks have no work queued.
            var elapsed = clock.GetElapsedTime(started);
            var delay = cadence - TimeSpan.FromTicks(elapsed.Ticks % cadence.Ticks);
            if (routeUnavailable)
            {
                // Reauthorization does not shorten the retry window for an unavailable route.
                await Task.Delay(delay, clock, token);
            }
            else
            {
                // Another caller can negotiate a different cadence while this worker is asleep.
                await WaitForNextAttemptAsync(delay, current.IntervalChanged, token);
            }
        }
    }

    private async Task WaitForNextAttemptAsync(TimeSpan delay, Task intervalChanged, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tick = Task.Delay(delay, clock, wait.Token);
        if (await Task.WhenAny(tick, intervalChanged) == tick)
            await tick;
        else
            await wait.CancelAsync();
    }

    private async Task<bool> SendAsync(CoreOptions scope, int? interval, long started, CancellationToken token)
    {
        var timeoutSeconds = interval is { } seconds
            ? Math.Min(scope.RequestTimeoutSeconds, seconds / 2.0)
            : scope.RequestTimeoutSeconds;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds), clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var routeUnavailable = false;

        void ShortenDeadline(int negotiatedInterval)
        {
            var halfInterval = negotiatedInterval / 2.0;
            if (halfInterval >= timeoutSeconds)
                return;

            timeoutSeconds = halfInterval;
            var remaining = TimeSpan.FromSeconds(halfInterval) - clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                timeout.Cancel();
            else
                timeout.CancelAfter(remaining);
        }

        try
        {
            await core.SendHeartbeatAsync(scope, ShortenDeadline, deadline.Token);
        }
        catch (CoreHeartbeatUnsupportedException)
        {
            // A legacy Core or newly refreshed session may omit heartbeat support.
        }
        catch (CoreRequestException exception)
        {
            routeUnavailable = exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;
            Warn($"Core returned HTTP {(int)exception.StatusCode}.");
        }
        catch (Exception exception) when (!token.IsCancellationRequested &&
            exception is CoreAuthenticationException or CoreAuthorizationException or
                HttpRequestException or JsonException or OperationCanceledException)
        {
            Warn("Could not complete a heartbeat with Core.");
        }

        token.ThrowIfCancellationRequested();
        return routeUnavailable;
    }

    private void Warn(string message)
    {
        var now = clock.GetTimestamp();
        if (_lastWarning is { } previous && clock.GetElapsedTime(previous, now) < RecheckDelay)
            return;

        _lastWarning = now;
        logger.LogWarning("Alta heartbeat failed: {Failure}", message);
    }
}
