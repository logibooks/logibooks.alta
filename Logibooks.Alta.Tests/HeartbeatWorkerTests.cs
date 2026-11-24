// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Logibooks.Alta.Database;
using Logibooks.Alta.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class HeartbeatWorkerTests
{
    [TestCase(null)]
    [TestCase("null")]
    [TestCase("\"600\"")]
    [TestCase("{}")]
    [TestCase("true")]
    [TestCase("1.5")]
    [TestCase("2147483648")]
    [TestCase("4")]
    [TestCase("3601")]
    public async Task LegacyOrInvalidIntervalPreservesTheJwtWithoutSending(string? metadata)
    {
        await using var fixture = new Fixture(loginInterval: _ => metadata);
        var token = await fixture.Sessions.GetTokenAsync(CancellationToken.None);
        await fixture.StartAsync();
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromMinutes(1));

        Assert.That(fixture.Login.Calls, Is.EqualTo(1));
        Assert.That(fixture.Heartbeats.Calls, Is.Zero);
        Assert.That(await fixture.Sessions.GetTokenAsync(CancellationToken.None), Is.EqualTo(token));
    }

    [Test]
    public async Task StartsWithoutQuartzAndUsesRefreshedIntervalAfterOneUnauthorizedRetry()
    {
        await using var fixture = new Fixture(loginInterval: call => call == 1 ? "5" : "10",
            send: (call, _) => Task.FromResult(call == 2 ? HttpStatusCode.Unauthorized : HttpStatusCode.NoContent));
        await fixture.StartAsync();
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));

        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(1));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(10));

        var requests = fixture.Heartbeats.Requests.ToArray();
        Assert.That(requests, Has.Length.EqualTo(3));
        Assert.That(requests.Select(r => r.Method), Is.All.EqualTo(HttpMethod.Post));
        Assert.That(requests.Select(r => r.AuthorizationScheme), Is.All.EqualTo("Bearer"));
        Assert.That(requests.Select(r => r.Uri!.AbsolutePath), Is.All.EqualTo("/prefix/api/alta/v1/heartbeat"));
        Assert.That(requests.Select(r => r.Body), Is.All.Null);
        Assert.That(requests[1].AuthorizationParameter, Is.EqualTo(requests[0].AuthorizationParameter));
        Assert.That(requests[2].AuthorizationParameter, Is.Not.EqualTo(requests[0].AuthorizationParameter));
        Assert.That(fixture.Login.Calls, Is.EqualTo(2));

        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(3));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(10));
        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(4));
    }

    [Test]
    public async Task SendsWhileTheInitialSqlProbeIsBlocked()
    {
        await using var fixture = new Fixture();
        fixture.ServiceOptions.Set(new ServiceOptions { Enabled = true });
        var databaseOptions = new TestOptionsMonitor<DatabaseMonitoringOptions>(new()
        {
            Targets = [new DatabaseTargetOptions { Name = "test", ConnectionString = "Server=sql;Integrated Security=true" }]
        });
        using var provider = new RuntimeConfigurationProvider(fixture.ServiceOptions, fixture.CoreOptions,
            databaseOptions, new CoreUris(isDevelopment: false));
        using var reload = new ConfigurationReloadSignal(fixture.ServiceOptions, fixture.CoreOptions, databaseOptions);
        var database = new BlockingDatabaseProbe();
        using var probeWorker = new ProbeWorker(provider, database, new NoOpCoreProbe(), reload,
            NullLogger<ProbeWorker>.Instance, clock: fixture.Clock);
        await probeWorker.StartAsync(CancellationToken.None);

        try
        {
            await database.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.StartAsync();
            await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));
            Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(2));
            Assert.That(probeWorker.ExecuteTask!.IsCompleted, Is.False);
        }
        finally
        {
            await probeWorker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LearningAShorterIntervalCapsTheCurrentAttempt(bool onReplay)
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fixture = new Fixture(loginInterval: call => onReplay && call == 1 ? "600" : "5",
            send: async (call, token) =>
            {
                if (onReplay && call == 1)
                {
                    firstEntered.TrySetResult();
                    await rejectFirst.Task.WaitAsync(token);
                    return HttpStatusCode.Unauthorized;
                }

                blocked.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return HttpStatusCode.NoContent;
                }
                finally
                {
                    cancelled.TrySetResult();
                }
            });
        await fixture.StartAsync();

        if (onReplay)
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            rejectFirst.TrySetResult();
        }

        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var remaining = TimeSpan.FromSeconds(onReplay ? 1.5 : 2.5);
        await fixture.Clock.WaitForDelayAsync(remaining);
        fixture.Clock.Advance(remaining);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(2.5));

        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(onReplay ? 2 : 1));
    }

    [Test]
    public async Task OmitsUnauthorizedReplayWhenTheRenewedSessionHasNoInterval()
    {
        await using var fixture = new Fixture(loginInterval: call => call == 1 ? "5" : null,
            send: (_, _) => Task.FromResult(HttpStatusCode.Unauthorized));
        await fixture.StartAsync();
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromMinutes(1));

        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(1));
        Assert.That(fixture.Login.Calls, Is.EqualTo(2));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromMinutes(1));
        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(1));
        Assert.That(fixture.Login.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task LegacySessionIsNotRenewedInsideRefreshSkewButIsReplacedAfterExpiration()
    {
        await using var fixture = new Fixture(loginInterval: call => call == 1 ? null : "10",
            jwtLifetime: call => call == 1 ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(1));
        await fixture.Sessions.GetTokenAsync(CancellationToken.None);
        await fixture.StartAsync();
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromMinutes(1));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromMinutes(1));
        Assert.That(fixture.Login.Calls, Is.EqualTo(1));
        Assert.That(fixture.Heartbeats.Calls, Is.Zero);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(10));
        Assert.That(fixture.Login.Calls, Is.EqualTo(2));
        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task LongAttemptIsCancelledAndSkipsElapsedTicks()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(send: async (call, token) =>
        {
            if (call == 2)
            {
                started.TrySetResult();
                using var registration = token.Register(() => cancelled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return HttpStatusCode.NoContent;
        });
        await fixture.StartAsync();
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(2.5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(12));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(3));
        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(2));

        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(3));
    }

    [TestCase(HttpStatusCode.NotFound, 60)]
    [TestCase(HttpStatusCode.MethodNotAllowed, 60)]
    [TestCase(HttpStatusCode.ServiceUnavailable, 5)]
    [TestCase(HttpStatusCode.Forbidden, 5)]
    public async Task HeartbeatFailureRetriesOnlyAtTheNextEligibleTick(HttpStatusCode status, int delaySeconds)
    {
        await using var fixture = new Fixture(send: (call, _) =>
            Task.FromResult(call == 1 ? status : HttpStatusCode.NoContent));
        await fixture.StartAsync();
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(delaySeconds));
        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(1));
        Assert.That(fixture.Log.Entries.Any(entry => entry.Level == LogLevel.Warning &&
            entry.Message.Contains(((int)status).ToString(), StringComparison.Ordinal)), Is.True);

        fixture.Clock.Advance(TimeSpan.FromSeconds(delaySeconds));
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task ChangingDestinationAndDisablingCancelThePreviousRequest()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(send: async (call, token) =>
        {
            if (call is 1 or 2)
            {
                var entered = call == 1 ? firstStarted : nextStarted;
                var stopped = call == 1 ? firstStopped : nextStopped;
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { stopped.TrySetResult(); }
            }
            return HttpStatusCode.NoContent;
        });
        await fixture.StartAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.CoreOptions.Set(Fixture.CreateOptions("https://other.example", "new-adapter"));
        await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(firstStopped.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(fixture.Login.Calls, Is.EqualTo(2));
        var loggedIn = fixture.Login.Requests.ToArray();
        Assert.That(loggedIn[0].Uri!.Host, Is.EqualTo("core.example"));
        Assert.That(loggedIn[1].Uri!.Host, Is.EqualTo("other.example"));
        Assert.That(loggedIn[1].Body, Does.Contain("new-adapter"));

        fixture.ServiceOptions.Set(new ServiceOptions { Enabled = false });
        await nextStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(2));

        fixture.ServiceOptions.Set(new ServiceOptions { Enabled = true });
        await fixture.Clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));
        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(3));
        Assert.That(fixture.Heartbeats.Requests.Last().Uri!.Host, Is.EqualTo("other.example"));
        Assert.That(fixture.Login.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task ShutdownCancelsAnInFlightHeartbeat()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(send: async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return HttpStatusCode.NoContent;
        });
        await fixture.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.StopAsync();
        Assert.That(cancelled.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(fixture.Heartbeats.Calls, Is.EqualTo(1));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly HeartbeatWorker _worker;
        private readonly JwtSessionHandler _authenticated;

        public Fixture(Func<int, string?>? loginInterval = null,
            Func<int, TimeSpan>? jwtLifetime = null,
            Func<int, CancellationToken, Task<HttpStatusCode>>? send = null)
        {
            Login = new RecordingHttpHandler((_, call, _) =>
            {
                var field = loginInterval is null ? "5" : loginInterval(call);
                var token = new JwtSecurityToken(claims: [new Claim("jti", call.ToString())],
                    expires: Clock.GetUtcNow().Add(jwtLifetime?.Invoke(call) ?? TimeSpan.FromHours(1)).UtcDateTime);
                var result = JsonSerializer.Serialize(new
                {
                    token = new JwtSecurityTokenHandler().WriteToken(token),
                    roles = new[] { "adapter-alta" }
                });
                if (field is not null)
                    result = result[..^1] + ", \"heartbeatIntervalSeconds\": " + field + "}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(result, Encoding.UTF8, "application/json")
                });
            });
            Heartbeats = new RecordingHttpHandler(async (_, call, cancellationToken) =>
                new HttpResponseMessage(send is null ? HttpStatusCode.NoContent : await send(call, cancellationToken)));
            var factory = new Factory(Login);
            var retry = new HttpRetryExecutor(new NoRetryDelay());
            var uris = new CoreUris(isDevelopment: false);
            Sessions = new CoreSessionManager(CoreOptions, factory, retry, Clock,
                NullLogger<CoreSessionManager>.Instance, uris);
            _authenticated = new JwtSessionHandler(Sessions) { InnerHandler = Heartbeats };
            factory.Authenticated = _authenticated;
            var core = new AltaCoreClient(CoreOptions, factory, retry, uris);
            _worker = new HeartbeatWorker(ServiceOptions, CoreOptions, Sessions, core, Clock, Log);
        }

        public TestOptionsMonitor<ServiceOptions> ServiceOptions { get; } =
            new(new ServiceOptions { Enabled = true, MonitoringCron = "invalid schedule" });
        public TestOptionsMonitor<CoreOptions> CoreOptions { get; } = new(CreateOptions());
        public ManualClock Clock { get; } = new();
        public ListLogger<HeartbeatWorker> Log { get; } = new();
        public CoreSessionManager Sessions { get; }
        public RecordingHttpHandler Login { get; }
        public RecordingHttpHandler Heartbeats { get; }

        public static CoreOptions CreateOptions(string url = "https://core.example/prefix", string email = "adapter") =>
            new() { BaseUrl = url, Email = email, Password = "test-password" };

        public Task StartAsync() => _worker.StartAsync(CancellationToken.None);

        public Task StopAsync() => _worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            finally
            {
                _worker.Dispose();
                _authenticated.Dispose();
                Login.Dispose();
                Sessions.Dispose();
            }
        }

        private sealed class Factory(HttpMessageHandler login) : IHttpClientFactory
        {
            public HttpMessageHandler Authenticated { get; set; } = null!;

            public HttpClient CreateClient(string name) =>
                new(name == HttpClientNames.Authentication ? login : Authenticated, disposeHandler: false);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly object _gate = new();
        private readonly HashSet<ManualTimer> _timers = [];
        private TaskCompletionSource _changed = NewSignal();
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => Origin.AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                Interlocked.Add(ref _ticks, elapsed.Ticks);
                due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                _timers.ExceptWith(due);
            }
            foreach (var timer in due)
                timer.Fire();
        }

        public async Task WaitForDelayAsync(TimeSpan expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_timers.Any(timer => timer.Due - _ticks == expected.Ticks))
                        return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(deadline.Token);
            }
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.That(period, Is.EqualTo(Timeout.InfiniteTimeSpan));
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        Due = clock._ticks + dueTime.Ticks;
                        clock._timers.Add(this);
                    }
                    clock._changed.TrySetResult();
                    clock._changed = NewSignal();
                }
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (clock._gate)
                    clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class BlockingDatabaseProbe : IMssqlProbe
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProbeAsync(RuntimeDatabaseTarget database, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class NoOpCoreProbe : ICoreApiProbe
    {
        public Task ProbeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
