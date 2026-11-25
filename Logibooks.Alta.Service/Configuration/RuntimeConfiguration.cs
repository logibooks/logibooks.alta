// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using Logibooks.Alta.CoreApi;
using Quartz;

namespace Logibooks.Alta.Configuration;

internal sealed record RuntimeConfiguration(
    ServiceOptions Service,
    CoreOptions Core,
    int MaxConcurrentDatabaseProbes,
    IReadOnlyList<RuntimeDatabaseTarget> Databases,
    MonitoringRevision? Revision = null)
{
    public (long Global, long Target) ScopeFor(string target) =>
        (Revision?.Global ?? 0, Revision?.Targets.GetValueOrDefault(target) ?? 0);
}

internal sealed record MonitoringRevision(long Global, IReadOnlyDictionary<string, long> Targets);

internal sealed record RuntimeDatabaseTarget(string Name, string ConnectionString);

internal interface IRuntimeConfigurationProvider
{
    RuntimeConfiguration GetValidated(DateTimeOffset? scheduleFrom = null);
}

internal sealed class RuntimeConfigurationProvider : IRuntimeConfigurationProvider, IDisposable
{
    private readonly IOptionsMonitor<ServiceOptions> _serviceOptions;
    private readonly IOptionsMonitor<CoreOptions> _coreOptions;
    private readonly IOptionsMonitor<DatabaseMonitoringOptions> _databaseOptions;
    private readonly CoreUris _coreUris;
    private readonly List<IDisposable?> _subscriptions;
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Connection, long Revision)> _sources = new(StringComparer.Ordinal);
    private bool _enabled;
    private (bool Enabled, string BaseUrl, string Writer) _recipient;
    private long _globalRevision;
    private long _nextSourceRevision;

    public RuntimeConfigurationProvider(IOptionsMonitor<ServiceOptions> serviceOptions,
        IOptionsMonitor<CoreOptions> coreOptions, IOptionsMonitor<DatabaseMonitoringOptions> databaseOptions, CoreUris coreUris)
    {
        _serviceOptions = serviceOptions;
        _coreOptions = coreOptions;
        _databaseOptions = databaseOptions;
        _coreUris = coreUris;
        _enabled = serviceOptions.CurrentValue.Enabled;
        _recipient = Recipient(coreOptions.CurrentValue);
        ObserveSources(GetEnabledDatabases(databaseOptions.CurrentValue));
        _subscriptions =
        [
            serviceOptions.OnChange((options, _) => { lock (_gate) ObserveService(options); }),
            coreOptions.OnChange((options, _) => { lock (_gate) ObserveCore(options); }),
            databaseOptions.OnChange((options, _) => { lock (_gate) ObserveSources(GetEnabledDatabases(options)); })
        ];
    }

    public RuntimeConfiguration GetValidated(DateTimeOffset? scheduleFrom = null)
    {
        ServiceOptions service;
        CoreOptions core;
        DatabaseMonitoringOptions databases;
        DatabaseTargetOptions[] enabledDatabases;
        MonitoringRevision revision;
        lock (_gate)
        {
            service = _serviceOptions.CurrentValue;
            core = _coreOptions.CurrentValue;
            databases = _databaseOptions.CurrentValue;
            enabledDatabases = GetEnabledDatabases(databases);
            ObserveService(service);
            ObserveCore(core);
            ObserveSources(enabledDatabases);
            revision = new MonitoringRevision(_globalRevision,
                _sources.ToDictionary(pair => pair.Key, pair => pair.Value.Revision, StringComparer.Ordinal));
        }
        var errors = new List<string>();

        var cron = service.MonitoringCron?.Trim() ?? "";
        var hasCron = CronExpression.IsValidExpression(cron);
        if (!hasCron)
            errors.Add("Service.MonitoringCron must be a valid Quartz cron expression");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(service.MonitoringTimeZone?.Trim() ?? "", out var zone))
            errors.Add("Service.MonitoringTimeZone must identify a time zone available on this host");
        else if (hasCron && scheduleFrom is { } from &&
            new CronExpression(cron) { TimeZone = zone }.GetNextValidTimeAfter(from) is null)
            errors.Add("Service.MonitoringCron must schedule a future firing");

        if (databases.MaxConcurrentProbes is < 1 or > 32)
        {
            errors.Add("Databases.MaxConcurrentProbes must be between 1 and 32");
        }

        if (service.Enabled)
        {
            if (enabledDatabases.Length == 0)
            {
                errors.Add("Databases.Targets must contain at least one enabled database when the service is enabled");
            }

            ValidateDatabases(enabledDatabases, errors);

            if (core.Enabled)
                errors.AddRange(core.ConnectionErrors(_coreUris));
        }

        errors.AddRange(core.TimeoutErrors());

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
                .ToArray(),
            revision);
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
            subscription?.Dispose();
    }

    private void ObserveService(ServiceOptions options)
    {
        if (_enabled != options.Enabled)
        {
            _enabled = options.Enabled;
            _globalRevision++;
        }
    }

    private void ObserveCore(CoreOptions options)
    {
        var recipient = Recipient(options);
        if (_recipient != recipient)
        {
            _recipient = recipient;
            _globalRevision++;
        }
    }

    private void ObserveSources(IEnumerable<DatabaseTargetOptions> targets)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var name = target.Name?.Trim() ?? string.Empty;
            var connection = target.ConnectionString ?? string.Empty;
            if (active.Add(name) && (!_sources.TryGetValue(name, out var previous) || previous.Connection != connection))
                _sources[name] = (connection, ++_nextSourceRevision);
        }
        foreach (var removed in _sources.Keys.Where(name => !active.Contains(name)).ToArray())
            _sources.Remove(removed);
    }

    private static DatabaseTargetOptions[] GetEnabledDatabases(DatabaseMonitoringOptions databases) =>
        (databases.Targets ?? []).Where(target => target.Enabled).ToArray();

    private static (bool Enabled, string BaseUrl, string Writer) Recipient(CoreOptions options) =>
        (options.Enabled, options.BaseUrl?.Trim().TrimEnd('/') ?? string.Empty, options.Email?.Trim() ?? string.Empty);

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
        MonitoringCron = value.MonitoringCron?.Trim() ?? string.Empty,
        MonitoringTimeZone = value.MonitoringTimeZone?.Trim() ?? string.Empty
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
