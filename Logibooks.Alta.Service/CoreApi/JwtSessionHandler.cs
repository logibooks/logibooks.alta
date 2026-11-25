// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Net;
using System.Net.Http.Headers;
using Logibooks.Alta.Configuration;

namespace Logibooks.Alta.CoreApi;

internal sealed class JwtSessionHandler(ICoreSessionManager sessionManager) : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<CoreOptions> Configuration = new("AltaCoreConfiguration");
    internal static readonly HttpRequestOptionsKey<Action<int>> HeartbeatIntervalObserver = new("AltaHeartbeatIntervalObserver");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var retryRequest = await CloneAsync(request, cancellationToken);
        request.Options.TryGetValue(Configuration, out var options);
        request.Options.TryGetValue(HeartbeatIntervalObserver, out var observeHeartbeatInterval);
        var token = await sessionManager.GetTokenAsync(cancellationToken, options, observeHeartbeatInterval);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        sessionManager.InvalidateToken(token);
        var refreshedToken = await sessionManager.GetTokenAsync(cancellationToken, options, observeHeartbeatInterval);
        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken);
        return await base.SendAsync(retryRequest, cancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
