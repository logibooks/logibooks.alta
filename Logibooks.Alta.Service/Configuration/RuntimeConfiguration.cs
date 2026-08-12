using Microsoft.Extensions.Options;

namespace Logibooks.Alta.Configuration;

internal sealed record RuntimeConfiguration(
    ServiceOptions Service,
    CoreOptions Core,
    string DatabaseConnectionString);

internal interface IRuntimeConfigurationProvider
{
    RuntimeConfiguration GetValidated();
}

internal sealed class RuntimeConfigurationProvider(
    IOptionsMonitor<ServiceOptions> serviceOptions,
    IOptionsMonitor<CoreOptions> coreOptions,
    IConfiguration configuration) : IRuntimeConfigurationProvider
{
    public RuntimeConfiguration GetValidated()
    {
        var service = serviceOptions.CurrentValue;
        var core = coreOptions.CurrentValue;
        var errors = new List<string>();

        if (service.PollIntervalSeconds is < 1 or > 86_400)
        {
            errors.Add("Service.PollIntervalSeconds must be between 1 and 86400");
        }

        var connectionString = configuration.GetConnectionString("AltaDatabase") ?? string.Empty;
        if (service.Enabled)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                errors.Add("ConnectionStrings.AltaDatabase is required when the service is enabled");
            }

            if (!Uri.TryCreate(core.BaseUrl, UriKind.Absolute, out var baseUri) ||
                !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Core.BaseUrl must be an absolute HTTPS URL when the service is enabled");
            }

            if (string.IsNullOrWhiteSpace(core.Email))
            {
                errors.Add("Core.Email is required when the service is enabled");
            }

            if (string.IsNullOrWhiteSpace(core.Password))
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

        return new RuntimeConfiguration(Clone(service), Clone(core), connectionString);
    }

    private static ServiceOptions Clone(ServiceOptions value) => new()
    {
        Enabled = value.Enabled,
        PollIntervalSeconds = value.PollIntervalSeconds
    };

    private static CoreOptions Clone(CoreOptions value) => new()
    {
        BaseUrl = value.BaseUrl.Trim(),
        Email = value.Email.Trim(),
        Password = value.Password,
        RequestTimeoutSeconds = value.RequestTimeoutSeconds,
        JwtRefreshSkewMinutes = value.JwtRefreshSkewMinutes
    };
}

internal sealed class RuntimeConfigurationException(IReadOnlyCollection<string> errors)
    : Exception(string.Join("; ", errors))
{
    public IReadOnlyCollection<string> Errors { get; } = errors;
}
