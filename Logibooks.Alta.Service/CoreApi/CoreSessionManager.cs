// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Logibooks.Alta.Configuration;
using Microsoft.Extensions.Options;

namespace Logibooks.Alta.CoreApi;

internal interface ICoreSessionManager
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken, CoreOptions? options = null,
        Action<int>? observeHeartbeatInterval = null);
    HeartbeatSession GetHeartbeatSession(CoreOptions options);
    void InvalidateToken(string token);
}

internal readonly record struct HeartbeatSession(bool HasUnexpiredToken, int? IntervalSeconds, Task IntervalChanged);

internal sealed class CoreSessionManager : ICoreSessionManager, IDisposable
{
    private const string RequiredRole = "adapter-alta";
    private readonly IOptionsMonitor<CoreOptions> _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHttpRetryExecutor _retryExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CoreSessionManager> _logger;
    private readonly CoreUris _coreUris;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private readonly object _heartbeatGate = new();
    private TaskCompletionSource _heartbeatIntervalChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? _lastPublishedHeartbeatInterval;
    private TokenState? _tokenState;

    public CoreSessionManager(
        IOptionsMonitor<CoreOptions> options,
        IHttpClientFactory httpClientFactory,
        IHttpRetryExecutor retryExecutor,
        TimeProvider timeProvider,
        ILogger<CoreSessionManager> logger,
        CoreUris coreUris)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _retryExecutor = retryExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
        _coreUris = coreUris;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken, CoreOptions? options = null,
        Action<int>? observeHeartbeatInterval = null)
    {
        var session = await GetSessionAsync(options ?? _options.CurrentValue, cancellationToken);
        if (observeHeartbeatInterval is not null)
        {
            if (session.HeartbeatIntervalSeconds is not { } interval)
                throw new CoreHeartbeatUnsupportedException();

            observeHeartbeatInterval(interval);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return session.Token;
    }

    public HeartbeatSession GetHeartbeatSession(CoreOptions options)
    {
        EnsureCurrent(options);
        lock (_heartbeatGate)
        {
            var session = Volatile.Read(ref _tokenState);
            var unexpired = session is not null && ReferenceEquals(session.Options, options)
                && session.ExpiresAtUtc > _timeProvider.GetUtcNow();
            return new HeartbeatSession(unexpired, unexpired ? session!.HeartbeatIntervalSeconds : null,
                _heartbeatIntervalChanged.Task);
        }
    }

    private async Task<TokenState> GetSessionAsync(CoreOptions options, CancellationToken cancellationToken)
    {
        EnsureCurrent(options);
        var existing = Volatile.Read(ref _tokenState);
        if (IsUsable(existing, options))
        {
            return existing!;
        }

        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            EnsureCurrent(options);
            existing = Volatile.Read(ref _tokenState);
            if (IsUsable(existing, options))
            {
                return existing!;
            }

            var token = await LoginAsync(options, cancellationToken);
            EnsureCurrent(options);
            PublishSession(token);
            _logger.LogInformation("A Logibooks Core JWT session was established");
            return token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private void PublishSession(TokenState session)
    {
        lock (_heartbeatGate)
        {
            Volatile.Write(ref _tokenState, session);
            if (session.HeartbeatIntervalSeconds == _lastPublishedHeartbeatInterval)
                return;

            _lastPublishedHeartbeatInterval = session.HeartbeatIntervalSeconds;
            var previous = _heartbeatIntervalChanged;
            _heartbeatIntervalChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    public void InvalidateToken(string token)
    {
        var existing = Volatile.Read(ref _tokenState);
        if (existing is not null && string.Equals(existing.Token, token, StringComparison.Ordinal))
        {
            Interlocked.CompareExchange(ref _tokenState, null, existing);
        }
    }

    public void Dispose()
    {
        _loginLock.Dispose();
    }

    private void EnsureCurrent(CoreOptions options)
    {
        if (!ReferenceEquals(_options.CurrentValue, options))
        {
            throw new CoreConfigurationException("Core configuration changed. Start the request again.");
        }
    }

    private bool IsUsable(TokenState? state, CoreOptions options) =>
        state is not null && ReferenceEquals(state.Options, options) &&
        state.ExpiresAtUtc > _timeProvider.GetUtcNow().AddMinutes(options.JwtRefreshSkewMinutes);

    private async Task<TokenState> LoginAsync(CoreOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrWhiteSpace(options.Password))
        {
            throw new CoreConfigurationException("Core credentials are not configured.");
        }

        var loginUri = _coreUris.Login(options);
        var timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        var client = _httpClientFactory.CreateClient(HttpClientNames.Authentication);

        using var response = await _retryExecutor.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Post, loginUri)
            {
            Content = JsonContent.Create(new LoginRequest(options.Email.Trim(), options.Password))
            },
            timeout,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new CoreAuthenticationException("Logibooks Core rejected the configured credentials.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CoreRequestException(response.StatusCode);
        }

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
        if (login is null)
        {
            throw new CoreAuthenticationException("Logibooks Core returned a null login response. Expected a JSON object.");
        }

        if (string.IsNullOrWhiteSpace(login.Token))
        {
            throw new CoreAuthenticationException("The Logibooks Core login response is missing a nonempty token field.");
        }

        if (login.Roles is null || !login.Roles.Contains(RequiredRole, StringComparer.OrdinalIgnoreCase))
        {
            throw new CoreAuthorizationException("The configured Core account does not have the adapter-alta role.");
        }

        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login.Token);
            if (jwt.Payload.Expiration is null)
            {
                throw new CoreAuthenticationException("The Core JWT has no expiration claim.");
            }

            return new TokenState(
                login.Token,
                DateTimeOffset.FromUnixTimeSeconds(jwt.Payload.Expiration.Value), options,
                ReadHeartbeatInterval(login.HeartbeatIntervalSeconds));
        }
        catch (ArgumentException exception)
        {
            throw new CoreAuthenticationException(
                "Logibooks Core returned a malformed JWT.",
                exception);
        }
    }

    private static int? ReadHeartbeatInterval(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var seconds) && seconds is >= 5 and <= 3600
            ? seconds
            : null;

    private sealed record TokenState(
        string Token, DateTimeOffset ExpiresAtUtc, CoreOptions Options, int? HeartbeatIntervalSeconds);

    private sealed record LoginRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("password")] string Password);

    private sealed class LoginResponse
    {
        [JsonPropertyName("token")]
        public string Token { get; init; } = string.Empty;

        [JsonPropertyName("roles")]
        public IReadOnlyCollection<string>? Roles { get; init; }

        [JsonPropertyName("heartbeatIntervalSeconds")]
        public JsonElement HeartbeatIntervalSeconds { get; init; }
    }
}

internal static class HttpClientNames
{
    public const string Authentication = "CoreAuthentication";
    public const string Api = "CoreApi";
}
