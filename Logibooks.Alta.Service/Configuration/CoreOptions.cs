// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Logibooks.Alta.CoreApi;

namespace Logibooks.Alta.Configuration;

internal sealed class CoreOptions
{
    public const string SectionName = "Core";

    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int JwtRefreshSkewMinutes { get; set; } = 5;

    public IEnumerable<string> ConnectionErrors(CoreUris uris)
    {
        if (uris.GetAllowedBaseUri(BaseUrl) is null)
            yield return uris.BaseUrlError;
        if (string.IsNullOrWhiteSpace(Email))
            yield return "Core.Email is required when the service is enabled";
        if (string.IsNullOrWhiteSpace(Password))
            yield return "Core.Password is required when the service is enabled";
    }

    public IEnumerable<string> TimeoutErrors()
    {
        if (RequestTimeoutSeconds is < 1 or > 300)
            yield return "Core.RequestTimeoutSeconds must be between 1 and 300";
        if (JwtRefreshSkewMinutes is < 0 or > 1_440)
            yield return "Core.JwtRefreshSkewMinutes must be between 0 and 1440";
    }
}
