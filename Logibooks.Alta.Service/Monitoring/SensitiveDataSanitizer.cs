// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Logibooks.Alta.Configuration;
using Microsoft.Data.SqlClient;

namespace Logibooks.Alta.Monitoring;

internal static class SensitiveDataSanitizer
{
    private const string Redacted = "[REDACTED]";

    public static string Sanitize(string? message, RuntimeConfiguration configuration)
    {
        var sanitized = string.IsNullOrWhiteSpace(message) ? "Unspecified error" : message;
        sanitized = Replace(sanitized, configuration.Core.Password);

        foreach (var database in configuration.Databases)
        {
            sanitized = Replace(sanitized, database.ConnectionString);
            try
            {
                var builder = new SqlConnectionStringBuilder(database.ConnectionString);
                sanitized = Replace(sanitized, builder.Password);
                foreach (var serverName in GetServerNames(builder.DataSource))
                {
                    sanitized = Replace(
                        sanitized,
                        serverName,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (ArgumentException)
            {
                // Configuration validation reports the target name without echoing its value.
            }
        }

        return sanitized;
    }

    private static IEnumerable<string> GetServerNames(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            yield break;
        }

        yield return dataSource;
        var host = dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
            ? dataSource[4..]
            : dataSource;
        host = host.Split(['\\', ','], 2)[0];
        if (host.Length > 1 && !string.Equals(host, dataSource, StringComparison.OrdinalIgnoreCase))
        {
            yield return host;
        }
    }

    private static string Replace(
        string value,
        string? secret,
        StringComparison comparison = StringComparison.Ordinal) =>
        string.IsNullOrEmpty(secret)
            ? value
            : value.Replace(secret, Redacted, comparison);
}
