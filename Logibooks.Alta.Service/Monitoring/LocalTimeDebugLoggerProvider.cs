// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Microsoft.Extensions.Logging.Debug;

namespace Logibooks.Alta.Monitoring;

[ProviderAlias("Debug")]
internal sealed class LocalTimeDebugLoggerProvider : ILoggerProvider
{
    private readonly DebugLoggerProvider _provider = new();

    public ILogger CreateLogger(string categoryName) => new TimestampedLogger(_provider.CreateLogger(categoryName));

    public void Dispose() => _provider.Dispose();

    private sealed class TimestampedLogger(ILogger logger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            logger.Log(logLevel, eventId, (state, formatter), exception, static (entry, error) =>
            {
                var message = entry.formatter(entry.state, error);
                return string.IsNullOrEmpty(message) && error is null
                    ? string.Empty
                    : $"[{DateTimeOffset.Now:G}] {message}";
            });
        }
    }
}
