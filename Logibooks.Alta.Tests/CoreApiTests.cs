using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class HttpRetryExecutorTests
{
    [Test]
    public async Task SendAsync_RetriesServerErrorsWithBoundedDelays()
    {
        var handler = new RecordingHttpHandler((_, call, _) => Task.FromResult(
            new HttpResponseMessage(call < 3 ? HttpStatusCode.BadGateway : HttpStatusCode.NoContent)));
        var delay = new NoRetryDelay();
        var executor = new HttpRetryExecutor(delay);

        using var response = await executor.SendAsync(
            new HttpClient(handler),
            () => new HttpRequestMessage(HttpMethod.Get, "https://core.example/probe"),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(handler.Calls, Is.EqualTo(3));
            Assert.That(delay.Delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }));
        });
    }

    [Test]
    public async Task SendAsync_DoesNotRetryClientErrors()
    {
        var handler = new RecordingHttpHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var delay = new NoRetryDelay();
        var executor = new HttpRetryExecutor(delay);

        using var response = await executor.SendAsync(
            new HttpClient(handler),
            () => new HttpRequestMessage(HttpMethod.Get, "https://core.example/probe"),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(handler.Calls, Is.EqualTo(1));
            Assert.That(delay.Delays, Is.Empty);
        });
    }

    [Test]
    public async Task SendAsync_RetriesTransientNetworkFailure()
    {
        var handler = new RecordingHttpHandler((_, call, _) =>
            call == 1
                ? throw new HttpRequestException("network")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var delay = new NoRetryDelay();
        var executor = new HttpRetryExecutor(delay);

        using var response = await executor.SendAsync(
            new HttpClient(handler),
            () => new HttpRequestMessage(HttpMethod.Get, "https://core.example/probe"),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.That(handler.Calls, Is.EqualTo(2));
        Assert.That(delay.Delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(1) }));
    }

    [Test]
    public void SendAsync_StopsImmediatelyForCallerCancellation()
    {
        var handler = new RecordingHttpHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var delay = new NoRetryDelay();
        var executor = new HttpRetryExecutor(delay);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(
            async () => await executor.SendAsync(
                new HttpClient(handler),
                () => new HttpRequestMessage(HttpMethod.Get, "https://core.example/probe"),
                TimeSpan.FromSeconds(30),
                cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(handler.Calls, Is.EqualTo(1));
        Assert.That(delay.Delays, Is.Empty);
    }
}

[TestFixture]
public sealed class CoreSessionManagerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task GetTokenAsync_PostsExpectedCredentialsAndCachesToken()
    {
        var token = CreateToken(Now.AddHours(1));
        var handler = LoginHandler(_ => LoginResponse(token, "adapter-alta"));
        using var session = CreateSession(handler, ValidOptions(), new MutableTimeProvider(Now));

        var first = await session.GetTokenAsync(CancellationToken.None);
        var second = await session.GetTokenAsync(CancellationToken.None);
        var request = handler.Requests.Single();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(token));
            Assert.That(second, Is.EqualTo(token));
            Assert.That(handler.Calls, Is.EqualTo(1));
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.Uri, Is.EqualTo(new Uri("https://core.example/root/api/Auth/login")));
            Assert.That(request.Body, Does.Contain("\"email\":\"adapter-alta\""));
            Assert.That(request.Body, Does.Contain("\"password\":\"core-secret\""));
        });
    }

    [Test]
    public void GetTokenAsync_RejectsAccountWithoutAltaRole()
    {
        var handler = LoginHandler(_ => LoginResponse(CreateToken(Now.AddHours(1)), "administrator"));
        using var session = CreateSession(handler, ValidOptions(), new MutableTimeProvider(Now));

        Assert.That(
            async () => await session.GetTokenAsync(CancellationToken.None),
            Throws.TypeOf<CoreAuthorizationException>());
    }

    [Test]
    public async Task GetTokenAsync_RefreshesTokenInsideConfiguredSkew()
    {
        var firstToken = CreateToken(Now.AddMinutes(4), "first");
        var secondToken = CreateToken(Now.AddHours(1), "second");
        var handler = LoginHandler(call => LoginResponse(call == 1 ? firstToken : secondToken, "adapter-alta"));
        using var session = CreateSession(handler, ValidOptions(), new MutableTimeProvider(Now));

        var first = await session.GetTokenAsync(CancellationToken.None);
        var second = await session.GetTokenAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(firstToken));
            Assert.That(second, Is.EqualTo(secondToken));
            Assert.That(handler.Calls, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task GetTokenAsync_SerializesConcurrentLogins()
    {
        var token = CreateToken(Now.AddHours(1));
        var handler = new RecordingHttpHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(25, cancellationToken);
            return LoginResponse(token, "adapter-alta");
        });
        using var session = CreateSession(handler, ValidOptions(), new MutableTimeProvider(Now));

        var tokens = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => session.GetTokenAsync(CancellationToken.None)));

        Assert.That(tokens, Is.All.EqualTo(token));
        Assert.That(handler.Calls, Is.EqualTo(1));
    }

    [Test]
    public void GetTokenAsync_RejectsNonHttpsBaseUrlBeforeSending()
    {
        var options = ValidOptions();
        options.BaseUrl = "http://core.example";
        var handler = LoginHandler(_ => LoginResponse(CreateToken(Now.AddHours(1)), "adapter-alta"));
        using var session = CreateSession(handler, options, new MutableTimeProvider(Now));

        Assert.That(
            async () => await session.GetTokenAsync(CancellationToken.None),
            Throws.TypeOf<CoreConfigurationException>());
        Assert.That(handler.Calls, Is.Zero);
    }

    [Test]
    public async Task OptionsChange_InvalidatesCachedToken()
    {
        var firstToken = CreateToken(Now.AddHours(1), "first");
        var secondToken = CreateToken(Now.AddHours(1), "second");
        var handler = LoginHandler(call => LoginResponse(call == 1 ? firstToken : secondToken, "adapter-alta"));
        var options = new TestOptionsMonitor<CoreOptions>(ValidOptions());
        using var session = CreateSession(handler, options, new MutableTimeProvider(Now));

        await session.GetTokenAsync(CancellationToken.None);
        options.Set(ValidOptions());
        var refreshed = await session.GetTokenAsync(CancellationToken.None);

        Assert.That(refreshed, Is.EqualTo(secondToken));
        Assert.That(handler.Calls, Is.EqualTo(2));
    }

    [Test]
    public void GetTokenAsync_PreservesMalformedJwtParsingFailure()
    {
        var handler = LoginHandler(_ => LoginResponse("not-a-jwt", "adapter-alta"));
        using var session = CreateSession(handler, ValidOptions(), new MutableTimeProvider(Now));

        var exception = Assert.ThrowsAsync<CoreAuthenticationException>(
            async () => await session.GetTokenAsync(CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("Logibooks Core returned a malformed JWT."));
            Assert.That(exception.InnerException, Is.InstanceOf<ArgumentException>());
            Assert.That(exception.InnerException!.Message, Is.Not.Empty);
        });
    }

    private static CoreSessionManager CreateSession(
        RecordingHttpHandler handler,
        CoreOptions options,
        TimeProvider timeProvider) =>
        CreateSession(handler, new TestOptionsMonitor<CoreOptions>(options), timeProvider);

    private static CoreSessionManager CreateSession(
        RecordingHttpHandler handler,
        TestOptionsMonitor<CoreOptions> options,
        TimeProvider timeProvider)
    {
        var clients = new Dictionary<string, HttpClient>
        {
            [HttpClientNames.Authentication] = new(handler)
        };
        return new CoreSessionManager(
            options,
            new TestHttpClientFactory(clients),
            new HttpRetryExecutor(new NoRetryDelay()),
            timeProvider,
            new ListLogger<CoreSessionManager>());
    }

    private static RecordingHttpHandler LoginHandler(Func<int, HttpResponseMessage> response) =>
        new((_, call, _) => Task.FromResult(response(call)));

    private static HttpResponseMessage LoginResponse(string token, params string[] roles) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { token, roles }),
            Encoding.UTF8,
            "application/json")
    };

    private static string CreateToken(DateTimeOffset expires, string id = "token")
    {
        var jwt = new JwtSecurityToken(
            claims: [new System.Security.Claims.Claim(JwtRegisteredClaimNames.Jti, id)],
            expires: expires.UtcDateTime);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static CoreOptions ValidOptions() => new()
    {
        BaseUrl = "https://core.example/root/",
        Email = "adapter-alta",
        Password = "core-secret",
        RequestTimeoutSeconds = 30,
        JwtRefreshSkewMinutes = 5
    };
}

[TestFixture]
public sealed class JwtSessionHandlerTests
{
    [Test]
    public async Task SendAsync_ReauthenticatesAndRetriesExactlyOnceAfterUnauthorized()
    {
        var session = new StubSessionManager("old-token", "new-token");
        var inner = new RecordingHttpHandler((_, call, _) => Task.FromResult(
            new HttpResponseMessage(call == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.NoContent)));
        var jwtHandler = new JwtSessionHandler(session) { InnerHandler = inner };
        using var client = new HttpClient(jwtHandler);

        using var response = await client.GetAsync("https://core.example/api/Auth/check");
        var requests = inner.Requests.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(inner.Calls, Is.EqualTo(2));
            Assert.That(session.InvalidatedTokens, Is.EqualTo(new[] { "old-token" }));
            Assert.That(requests[0].AuthorizationParameter, Is.EqualTo("old-token"));
            Assert.That(requests[1].AuthorizationParameter, Is.EqualTo("new-token"));
            Assert.That(requests, Has.All.Property(nameof(RecordedRequest.AuthorizationScheme)).EqualTo("Bearer"));
        });
    }

    [Test]
    public async Task SendAsync_ReturnsSecondUnauthorizedWithoutLooping()
    {
        var session = new StubSessionManager("old-token", "new-token", "unused-token");
        var inner = new RecordingHttpHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var client = new HttpClient(new JwtSessionHandler(session) { InnerHandler = inner });

        using var response = await client.GetAsync("https://core.example/api/Auth/check");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(inner.Calls, Is.EqualTo(2));
        Assert.That(session.GetTokenCalls, Is.EqualTo(2));
    }

    [Test]
    public void SendAsync_DisposesRetryRequestWhenSecondSendThrows()
    {
        var session = new StubSessionManager("old-token", "new-token");
        var inner = new ThrowingRetryHandler();
        using var client = new HttpClient(new JwtSessionHandler(session) { InnerHandler = inner });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://core.example/api/Auth/check")
        {
            Content = new StringContent("probe")
        };

        Assert.That(
            async () => await client.SendAsync(request),
            Throws.TypeOf<HttpRequestException>());
        Assert.That(inner.RetryRequest, Is.Not.Null);
        Assert.That(
            async () => await inner.RetryRequest!.Content!.ReadAsByteArrayAsync(),
            Throws.TypeOf<ObjectDisposedException>());
    }

    private sealed class StubSessionManager(params string[] tokens) : ICoreSessionManager
    {
        private readonly Queue<string> _tokens = new(tokens);
        public int GetTokenCalls { get; private set; }
        public List<string> InvalidatedTokens { get; } = [];

        public Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            GetTokenCalls++;
            return Task.FromResult(_tokens.Dequeue());
        }

        public void InvalidateToken(string token) => InvalidatedTokens.Add(token);
    }

    private sealed class ThrowingRetryHandler : HttpMessageHandler
    {
        private int _calls;
        public HttpRequestMessage? RetryRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            RetryRequest = request;
            throw new HttpRequestException("The retry failed.");
        }
    }
}

[TestFixture]
public sealed class CoreApiProbeTests
{
    [Test]
    public async Task ProbeAsync_CallsConfiguredCheckEndpoint()
    {
        var handler = new RecordingHttpHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var client = new HttpClient(handler);
        var probe = CreateProbe(handler, client);

        await probe.ProbeAsync(CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.That(request.Uri, Is.EqualTo(new Uri("https://core.example/root/api/Auth/check")));
    }

    [Test]
    public void ProbeAsync_MapsForbiddenResponse()
    {
        var handler = new RecordingHttpHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var client = new HttpClient(handler);
        var probe = CreateProbe(handler, client);

        Assert.That(
            async () => await probe.ProbeAsync(CancellationToken.None),
            Throws.TypeOf<CoreAuthorizationException>());
        Assert.That(handler.Calls, Is.EqualTo(1));
    }

    private static CoreApiProbe CreateProbe(RecordingHttpHandler handler, HttpClient client)
    {
        var options = new TestOptionsMonitor<CoreOptions>(new CoreOptions
        {
            BaseUrl = "https://core.example/root/",
            Email = "adapter-alta",
            Password = "secret",
            RequestTimeoutSeconds = 30,
            JwtRefreshSkewMinutes = 5
        });
        var clients = new Dictionary<string, HttpClient> { [HttpClientNames.Api] = client };
        return new CoreApiProbe(
            options,
            new TestHttpClientFactory(clients),
            new HttpRetryExecutor(new NoRetryDelay()));
    }
}
