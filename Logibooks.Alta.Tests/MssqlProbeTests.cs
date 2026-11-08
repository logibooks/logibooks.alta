// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Logibooks.Alta.Database;
using Logibooks.Alta.Configuration;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class MssqlProbeTests
{
    [Test]
    public async Task ProbeAsync_OpensConnectionAndExecutesExpectedCommand()
    {
        var connection = new FakeDatabaseConnection { ScalarResult = 1 };
        var factory = new FakeConnectionFactory(connection);
        var probe = new MssqlProbe(factory, new NoDatabaseRetryDelay());

        await probe.ProbeAsync(Target("Server=db;User Id=alta;Password=secret"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(factory.ConnectionString, Is.EqualTo("Server=db;User Id=alta;Password=secret"));
            Assert.That(connection.Opened, Is.True);
            Assert.That(connection.CommandText, Is.EqualTo(MssqlProbe.ProbeCommandText));
            Assert.That(connection.CommandTimeout, Is.EqualTo(MssqlProbe.CommandTimeoutSeconds));
            Assert.That(connection.Disposed, Is.True);
        });
    }

    [Test]
    public async Task ProbeAsync_UsesEachTargetsExactConnectionString()
    {
        var factory = new FakeConnectionFactory(
            new FakeDatabaseConnection { ScalarResult = 1 },
            new FakeDatabaseConnection { ScalarResult = 1 });
        var probe = new MssqlProbe(factory, new NoDatabaseRetryDelay());
        var first = "Server=first;Database=one;User Id=alta;Password=secret-one";
        var second = "Server=second;Database=two;User Id=alta;Password=secret-two";

        await probe.ProbeAsync(new RuntimeDatabaseTarget("one", first), CancellationToken.None);
        await probe.ProbeAsync(new RuntimeDatabaseTarget("two", second), CancellationToken.None);

        Assert.That(factory.ConnectionStrings, Is.EqualTo(new[] { first, second }));
    }

    [Test]
    public void ProbeAsync_PropagatesCancellationWithoutRetrying()
    {
        var factory = new FakeConnectionFactory(new FakeDatabaseConnection());
        var delay = new NoDatabaseRetryDelay();
        var probe = new MssqlProbe(factory, delay);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(
            async () => await probe.ProbeAsync(Target("Server=db;Integrated Security=True"), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(factory.CreateCalls, Is.EqualTo(1));
        Assert.That(delay.Delays, Is.Empty);
    }

    [Test]
    public void ProbeAsync_RejectsMissingConnectionString()
    {
        var probe = new MssqlProbe(
            new FakeConnectionFactory(new FakeDatabaseConnection()),
            new NoDatabaseRetryDelay());

        Assert.That(
            async () => await probe.ProbeAsync(Target(string.Empty), CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void ProbeAsync_RejectsUnexpectedScalarResult()
    {
        var connection = new FakeDatabaseConnection { ScalarResult = 0 };
        var probe = new MssqlProbe(
            new FakeConnectionFactory(connection),
            new NoDatabaseRetryDelay());

        Assert.That(
            async () => await probe.ProbeAsync(Target("Server=db;Password=secret"), CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task ProbeAsync_RetriesTransientNetworkFailuresWithBoundedDelays()
    {
        var first = new FakeDatabaseConnection { OpenException = new IOException("network") };
        var second = new FakeDatabaseConnection { ScalarResult = 1 };
        var factory = new FakeConnectionFactory(first, second);
        var delay = new NoDatabaseRetryDelay();
        var probe = new MssqlProbe(
            factory,
            delay);

        await probe.ProbeAsync(Target("Server=db;Password=secret"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(factory.CreateCalls, Is.EqualTo(2));
            Assert.That(first.Disposed, Is.True);
            Assert.That(second.Disposed, Is.True);
            Assert.That(delay.Delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(1) }));
        });
    }

    private static RuntimeDatabaseTarget Target(string connectionString) =>
        new("database-one", connectionString);

    private sealed class FakeConnectionFactory(params FakeDatabaseConnection[] connections)
        : IDatabaseConnectionFactory
    {
        private readonly Queue<FakeDatabaseConnection> _connections = new(connections);
        public string? ConnectionString { get; private set; }
        public List<string> ConnectionStrings { get; } = [];
        public int CreateCalls { get; private set; }

        public IDatabaseConnection Create(string connectionString)
        {
            ConnectionString = connectionString;
            ConnectionStrings.Add(connectionString);
            CreateCalls++;
            return _connections.Dequeue();
        }
    }

    private sealed class FakeDatabaseConnection : IDatabaseConnection
    {
        public bool Opened { get; private set; }
        public bool Disposed { get; private set; }
        public object? ScalarResult { get; set; }
        public string? CommandText { get; private set; }
        public int CommandTimeout { get; private set; }
        public Exception? OpenException { get; set; }

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OpenException is not null)
            {
                return Task.FromException(OpenException);
            }

            Opened = true;
            return Task.CompletedTask;
        }

        public Task<object?> ExecuteScalarAsync(
            string commandText,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandText = commandText;
            CommandTimeout = commandTimeoutSeconds;
            return Task.FromResult(ScalarResult);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoDatabaseRetryDelay : IDatabaseRetryDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }
}
