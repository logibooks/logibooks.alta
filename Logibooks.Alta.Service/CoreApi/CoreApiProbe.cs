// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Net;
using Logibooks.Alta.Configuration;
using Microsoft.Extensions.Options;

namespace Logibooks.Alta.CoreApi;

internal interface ICoreApiProbe
{
    Task ProbeAsync(CancellationToken cancellationToken);
}

internal sealed class CoreApiProbe(
    IOptionsMonitor<CoreOptions> options,
    IHttpClientFactory httpClientFactory,
    IHttpRetryExecutor retryExecutor) : ICoreApiProbe
{
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        var checkUri = CoreUris.Check(current);
        var client = httpClientFactory.CreateClient(HttpClientNames.Api);

        using var response = await retryExecutor.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Get, checkUri),
            TimeSpan.FromSeconds(current.RequestTimeoutSeconds),
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new CoreAuthenticationException("Logibooks Core rejected the refreshed JWT.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new CoreAuthorizationException("Logibooks Core denied the authentication check.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CoreRequestException(response.StatusCode);
        }
    }
}
