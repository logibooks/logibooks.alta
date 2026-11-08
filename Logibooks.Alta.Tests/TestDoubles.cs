// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Logibooks.Alta.Tests;

internal sealed class TestOptionsMonitor<T>(T initialValue) : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];

    public T CurrentValue { get; private set; } = initialValue;

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new CallbackDisposable(() => _listeners.Remove(listener));
    }

    public void Set(T value)
    {
        CurrentValue = value;
        foreach (var listener in _listeners.ToArray())
        {
            listener(value, Options.DefaultName);
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}

internal sealed class RecordingHttpHandler(
    Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> handler)
    : HttpMessageHandler
{
    private int _calls;

    public int Calls => _calls;
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new RecordedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            body));
        return await handler(request, call, cancellationToken);
    }
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    string? AuthorizationScheme,
    string? AuthorizationParameter,
    string? Body);

internal sealed class TestHttpClientFactory(IReadOnlyDictionary<string, HttpClient> clients)
    : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => clients[name];
}

internal sealed class NoRetryDelay : Logibooks.Alta.CoreApi.IRetryDelay
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
