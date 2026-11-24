// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Logibooks.Alta.Configuration;
using Microsoft.Extensions.Options;

namespace Logibooks.Alta.CoreApi;

internal sealed class AltaCoreClient(
    IOptionsMonitor<CoreOptions> options,
    IHttpClientFactory clientFactory,
    IHttpRetryExecutor retryExecutor,
    CoreUris uris)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, (WorklistIdentity Identity, AltaWorklistStatus Status)> _worklists =
        new(StringComparer.Ordinal);

    public CoreOptions CaptureConfiguration()
    {
        var scope = options.CurrentValue;
        if (!scope.Enabled)
            throw new CoreConfigurationException("Core integration is disabled.");
        if (uris.GetAllowedBaseUri(scope.BaseUrl) is null)
            throw new CoreConfigurationException(uris.BaseUrlError);
        if (string.IsNullOrWhiteSpace(scope.Email) || string.IsNullOrWhiteSpace(scope.Password))
            throw new CoreConfigurationException("Core credentials are not configured.");
        if (scope.RequestTimeoutSeconds is < 1 or > 300 || scope.JwtRefreshSkewMinutes is < 0 or > 1_440)
            throw new CoreConfigurationException("Core timeout or JWT refresh interval is outside the allowed range.");

        return scope;
    }

    public AltaWorklistStatus? GetWorklistStatus(string databaseName) =>
        _worklists.TryGetValue(databaseName, out var entry) && entry.Identity == GetWorklistIdentity(options.CurrentValue)
            ? entry.Status : null;

    public async Task<IReadOnlyList<string>> RefreshWorklistAsync(
        CoreOptions scope, string databaseName, CancellationToken token)
    {
        EnsureCurrent(scope);
        try
        {
            using var client = clientFactory.CreateClient(HttpClientNames.Api);
            using var response = await retryExecutor.SendAsync(client,
                () => CreateRequest(HttpMethod.Get, uris.Worklist(scope, databaseName), scope),
                TimeSpan.FromSeconds(scope.RequestTimeoutSeconds), token);
            await EnsureSuccessAsync(response, token);
            var result = await response.Content.ReadFromJsonAsync<AltaWorklist>(JsonOptions, token);
            if (result?.Cmrs is null || result.Cmrs.Any(string.IsNullOrWhiteSpace))
                throw new JsonException("The Core worklist is invalid.");

            EnsureCurrent(scope);
            _worklists[databaseName] = (GetWorklistIdentity(scope), new(result.Cmrs, false));
            return result.Cmrs;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            MarkWorklistStale(scope, databaseName);
            throw;
        }
    }

    public async Task<AltaBatchResult> SubmitAsync(
        CoreOptions scope, string databaseName, AltaBatch batch, CancellationToken token)
    {
        EnsureCurrent(scope);
        using var client = clientFactory.CreateClient(HttpClientNames.Api);
        using var request = CreateRequest(HttpMethod.Post, uris.SubmitDec(scope, databaseName), scope);
        request.Content = JsonContent.Create(batch, options: JsonOptions);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(scope.RequestTimeoutSeconds));

        try
        {
            using var response = await client.SendAsync(request, deadline.Token);
            await EnsureSuccessAsync(response, deadline.Token);
            var result = await response.Content.ReadFromJsonAsync<AltaBatchResult>(JsonOptions, deadline.Token);
            if (result is null || result.CmrNumber != batch.CmrNumber.Trim() || result.DecCode != batch.DecCode.Trim() ||
                result.AcceptedCount != batch.Assignments.Count || result.Results is null ||
                result.Results.Count != batch.Assignments.Count || result.Results.Select(item => item?.Index).Distinct().Count() != batch.Assignments.Count ||
                result.Results.Any(item => item is null || item.Index < 0 || item.Index >= batch.Assignments.Count ||
                    item.Outcome is not ("applied" or "unchanged" or "error")))
                throw new JsonException("Core returned an invalid batch result.");

            return result;
        }
        catch (CoreRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict &&
                                             ex.Code is "AltaCmrUnavailable" or "AltaScopeChanged")
        {
            MarkWorklistStale(scope, databaseName);
            try
            {
                await RefreshWorklistAsync(scope, databaseName, deadline.Token);
            }
            catch (Exception refreshError) when (refreshError is CoreApiException or HttpRequestException or
                                                OperationCanceledException or JsonException)
            {
                // The submission was rejected. Keep its error even if refreshing the worklist also failed.
            }
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or
                                       CoreRequestException { StatusCode: >= HttpStatusCode.InternalServerError } ||
                                   ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new AltaOutcomeUnknownException(ex);
        }
    }

    internal static string DescribeReadFailure(Exception ex) => ex switch
    {
        CoreRequestException request => $"HTTP {(int)request.StatusCode}" + (request.Code is null ? "" : $" ({request.Code})"),
        CoreApiException => ex.Message,
        OperationCanceledException => "Core request timed out.",
        JsonException => "Core returned an invalid response.",
        _ => "Could not read the Core response."
    };

    private void EnsureCurrent(CoreOptions scope)
    {
        if (!ReferenceEquals(options.CurrentValue, scope))
            throw new CoreConfigurationException("Core configuration changed. Start the request again.");
    }

    private WorklistIdentity GetWorklistIdentity(CoreOptions scope) =>
        new(uris.GetAllowedBaseUri(scope.BaseUrl), scope.Email?.Trim() ?? string.Empty);

    private void MarkWorklistStale(CoreOptions scope, string databaseName)
    {
        var identity = GetWorklistIdentity(scope);
        if (identity == GetWorklistIdentity(options.CurrentValue))
        {
            var cmrs = GetWorklistStatus(databaseName)?.Cmrs;
            _worklists[databaseName] = (identity, new(cmrs, true));
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, CoreOptions scope)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Options.Set(JwtSessionHandler.Configuration, scope);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode == HttpStatusCode.OK)
            return;

        CoreError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<CoreError>(JsonOptions, token);
        }
        catch (JsonException)
        {
            // HTTP status is still useful when an intermediary returned a different format.
        }
        throw new CoreRequestException(response.StatusCode, error?.Code, error?.Msg);
    }

    private sealed record CoreError(string? Code, string? Msg);
    private sealed record WorklistIdentity(Uri? BaseUri, string Account);
}
