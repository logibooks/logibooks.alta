using Logibooks.Alta.Configuration;
using Microsoft.Data.SqlClient;

namespace Logibooks.Alta.Monitoring;

internal static class SensitiveDataSanitizer
{
    private const string Redacted = "[REDACTED]";

    public static string Sanitize(string? message, RuntimeConfiguration configuration)
    {
        var sanitized = string.IsNullOrWhiteSpace(message) ? "Unspecified error" : message;
        sanitized = Replace(sanitized, configuration.DatabaseConnectionString);
        sanitized = Replace(sanitized, configuration.Core.Password);

        try
        {
            var builder = new SqlConnectionStringBuilder(configuration.DatabaseConnectionString);
            sanitized = Replace(sanitized, builder.Password);
        }
        catch (ArgumentException)
        {
            // Configuration validation reports the field name without echoing its value.
        }

        return sanitized;
    }

    private static string Replace(string value, string? secret) =>
        string.IsNullOrEmpty(secret)
            ? value
            : value.Replace(secret, Redacted, StringComparison.Ordinal);
}
