using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;

namespace Logibooks.Alta.Configuration;

internal sealed record RuntimeConfiguration(
    ServiceOptions Service,
    CoreOptions Core,
    int MaxConcurrentDatabaseProbes,
    IReadOnlyList<RuntimeDatabaseTarget> Databases);

internal sealed record RuntimeDatabaseTarget(string Name, string ConnectionString);

internal interface IRuntimeConfigurationProvider
{
    RuntimeConfiguration GetValidated();
}

internal sealed class RuntimeConfigurationProvider(
    IOptionsMonitor<ServiceOptions> serviceOptions,
    IOptionsMonitor<CoreOptions> coreOptions,
    IOptionsMonitor<DatabaseMonitoringOptions> databaseOptions) : IRuntimeConfigurationProvider
{
    public RuntimeConfiguration GetValidated()
    {
        var service = serviceOptions.CurrentValue;
        var core = coreOptions.CurrentValue;
        var databases = databaseOptions.CurrentValue;
        var errors = new List<string>();

        if (service.PollIntervalSeconds is < 1 or > 86_400)
        {
            errors.Add("Service.PollIntervalSeconds must be between 1 and 86400");
        }

        if (databases.MaxConcurrentProbes is < 1 or > 32)
        {
            errors.Add("Databases.MaxConcurrentProbes must be between 1 and 32");
        }

        var configuredDatabases = databases.Targets ?? [];
        var enabledDatabases = configuredDatabases.Where(target => target.Enabled).ToArray();
        if (service.Enabled)
        {
            if (enabledDatabases.Length == 0)
            {
                errors.Add("Databases.Targets must contain at least one enabled database when the service is enabled");
            }

            ValidateDatabases(enabledDatabases, errors);

            if (core.Enabled &&
                (!Uri.TryCreate(core.BaseUrl, UriKind.Absolute, out var baseUri) ||
                 !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("Core.BaseUrl must be an absolute HTTPS URL when the service is enabled");
            }

            if (core.Enabled && string.IsNullOrWhiteSpace(core.Email))
            {
                errors.Add("Core.Email is required when the service is enabled");
            }

            if (core.Enabled && string.IsNullOrWhiteSpace(core.Password))
            {
                errors.Add("Core.Password is required when the service is enabled");
            }
        }

        if (core.RequestTimeoutSeconds is < 1 or > 300)
        {
            errors.Add("Core.RequestTimeoutSeconds must be between 1 and 300");
        }

        if (core.JwtRefreshSkewMinutes is < 0 or > 1_440)
        {
            errors.Add("Core.JwtRefreshSkewMinutes must be between 0 and 1440");
        }

        if (errors.Count != 0)
        {
            throw new RuntimeConfigurationException(errors);
        }

        return new RuntimeConfiguration(
            Clone(service),
            Clone(core),
            databases.MaxConcurrentProbes,
            enabledDatabases
                .Select(target => new RuntimeDatabaseTarget(
                    target.Name?.Trim() ?? string.Empty,
                    target.ConnectionString ?? string.Empty))
                .ToArray());
    }

    private static void ValidateDatabases(
        IReadOnlyCollection<DatabaseTargetOptions> databases,
        ICollection<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var database in databases)
        {
            var name = database.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                errors.Add("Every enabled Databases.Targets entry must have a nonempty Name");
            }
            else if (!names.Add(name))
            {
                errors.Add($"Databases.Targets contains duplicate enabled name '{name}'");
            }

            var connectionString = database.ConnectionString ?? string.Empty;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                errors.Add($"Database target '{DisplayName(name)}' requires a ConnectionString");
                continue;
            }

            try
            {
                _ = new SqlConnectionStringBuilder(connectionString);
            }
            catch (ArgumentException)
            {
                errors.Add($"Database target '{DisplayName(name)}' has an invalid ConnectionString");
            }
        }
    }

    private static string DisplayName(string name) => name.Length == 0 ? "<unnamed>" : name;

    private static ServiceOptions Clone(ServiceOptions value) => new()
    {
        Enabled = value.Enabled,
        PollIntervalSeconds = value.PollIntervalSeconds
    };

    private static CoreOptions Clone(CoreOptions value) => new()
    {
        Enabled = value.Enabled,
        BaseUrl = value.BaseUrl?.Trim() ?? string.Empty,
        Email = value.Email?.Trim() ?? string.Empty,
        Password = value.Password ?? string.Empty,
        RequestTimeoutSeconds = value.RequestTimeoutSeconds,
        JwtRefreshSkewMinutes = value.JwtRefreshSkewMinutes
    };
}

internal sealed class RuntimeConfigurationException(IReadOnlyCollection<string> errors)
    : Exception(string.Join("; ", errors))
{
    public IReadOnlyCollection<string> Errors { get; } = errors;
}
