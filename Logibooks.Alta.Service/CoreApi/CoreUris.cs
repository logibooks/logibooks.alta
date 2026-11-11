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

    public Uri? GetAllowedBaseUri(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme == Uri.UriSchemeHttps ||
               (isDevelopment && uri.Scheme == Uri.UriSchemeHttp &&
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;
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
