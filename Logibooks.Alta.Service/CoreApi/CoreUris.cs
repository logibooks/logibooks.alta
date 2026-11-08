// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Logibooks.Alta.Configuration;

namespace Logibooks.Alta.CoreApi;

internal static class CoreUris
{
    public static Uri Login(CoreOptions options) => Combine(options, "api/Auth/login");

    public static Uri Check(CoreOptions options) => Combine(options, "api/Auth/check");

    public static Uri GetValidatedBaseUri(CoreOptions options)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new CoreConfigurationException("Core.BaseUrl must be an absolute HTTPS URL.");
        }

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static Uri Combine(CoreOptions options, string relativePath) =>
        new(GetValidatedBaseUri(options), relativePath);
}
