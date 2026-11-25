// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Logibooks.Alta.Configuration;

namespace Logibooks.Alta.CoreApi;

internal sealed class CoreUris(bool isDevelopment)
{
    public string BaseUrlError => isDevelopment
        ? "Core.BaseUrl must be an absolute HTTPS URL or an HTTP URL on localhost."
        : "Core.BaseUrl must be an absolute HTTPS URL.";

    public Uri Login(CoreOptions options) => Combine(options, "api/Auth/login");
    public Uri Check(CoreOptions options) => Combine(options, "api/Auth/check");
    public Uri Heartbeat(CoreOptions options) => Combine(options, "api/alta/v1/heartbeat");

    public Uri Worklist(CoreOptions options, string databaseName) => Alta(options, databaseName, "worklist");

    public Uri SubmitDec(CoreOptions options, string databaseName) => Alta(options, databaseName, "parcel-change-batches");

    public Uri? GetAllowedBaseUri(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0)
        {
            return null;
        }

        return uri.Scheme == Uri.UriSchemeHttps ||
               (isDevelopment && uri.Scheme == Uri.UriSchemeHttp &&
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;
    }

    private Uri Alta(CoreOptions options, string databaseName, string action)
    {
        if (string.IsNullOrWhiteSpace(databaseName) || databaseName is "." or ".." ||
            databaseName.Contains('/') || databaseName.Contains('\\'))
        {
            throw new CoreConfigurationException("The database name must be a nonempty URL path segment.");
        }

        return Combine(options, $"api/alta/v1/databases/{Uri.EscapeDataString(databaseName)}/{action}");
    }

    private Uri Combine(CoreOptions options, string relativePath)
    {
        var uri = GetAllowedBaseUri(options.BaseUrl) ?? throw new CoreConfigurationException(BaseUrlError);
        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return new Uri(builder.Uri, relativePath);
    }
}
