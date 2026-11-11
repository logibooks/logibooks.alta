// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Logibooks.Alta.Configuration;
using Microsoft.Extensions.Options;

namespace Logibooks.Alta.CoreApi;

internal interface ICoreSessionManager
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
    void InvalidateToken(string token);
}

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
    private readonly IDisposable? _optionsRegistration;
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
        _optionsRegistration = options.OnChange((_, _) => Volatile.Write(ref _tokenState, null));
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var existing = Volatile.Read(ref _tokenState);
        if (IsUsable(existing, options))
        {
            return existing!.Token;
        }

        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            options = _options.CurrentValue;
            existing = Volatile.Read(ref _tokenState);
            if (IsUsable(existing, options))
            {
                return existing!.Token;
            }

            var token = await LoginAsync(options, cancellationToken);
            Volatile.Write(ref _tokenState, token);
            _logger.LogInformation("A Logibooks Core JWT session was established");
            return token.Token;
        }
        finally
        {
            _loginLock.Release();
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
        _optionsRegistration?.Dispose();
        _loginLock.Dispose();
    }

    private bool IsUsable(TokenState? state, CoreOptions options) =>
        state is not null &&
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
                Content = JsonContent.Create(new LoginRequest(options.Email, options.Password))
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
        if (login is null || string.IsNullOrWhiteSpace(login.Token))
        {
            throw new CoreAuthenticationException("Logibooks Core returned an invalid login response.");
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
                DateTimeOffset.FromUnixTimeSeconds(jwt.Payload.Expiration.Value));
        }
        catch (ArgumentException exception)
        {
            throw new CoreAuthenticationException(
                "Logibooks Core returned a malformed JWT.",
                exception);
        }
    }

    private sealed record TokenState(string Token, DateTimeOffset ExpiresAtUtc);

    private sealed record LoginRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("password")] string Password);

    private sealed class LoginResponse
    {
        [JsonPropertyName("token")]
        public string Token { get; init; } = string.Empty;

        [JsonPropertyName("roles")]
        public IReadOnlyCollection<string>? Roles { get; init; }
    }
}

internal static class HttpClientNames
{
    public const string Authentication = "CoreAuthentication";
    public const string Api = "CoreApi";
}
