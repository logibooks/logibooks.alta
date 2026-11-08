// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

namespace Logibooks.Alta.CoreApi;

internal interface IRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemRetryDelay : IRetryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

internal interface IHttpRetryExecutor
{
    Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class HttpRetryExecutor(IRetryDelay retryDelay) : IHttpRetryExecutor
{
    private const int MaximumAttempts = 3;

    public async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                using var request = requestFactory();
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);

                var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token);

                if ((int)response.StatusCode < 500 || attempt == MaximumAttempts)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < MaximumAttempts)
            {
                // A fresh request is created for the next bounded attempt.
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && attempt < MaximumAttempts)
            {
                // Per-attempt timeout; retry within the configured bound.
            }

            await retryDelay.DelayAsync(TimeSpan.FromSeconds(attempt), cancellationToken);
        }

        throw new InvalidOperationException("The HTTP retry loop completed without a response.");
    }
}
