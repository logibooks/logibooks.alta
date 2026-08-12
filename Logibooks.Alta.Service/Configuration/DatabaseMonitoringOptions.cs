namespace Logibooks.Alta.Configuration;

internal sealed class DatabaseMonitoringOptions
{
    public const string SectionName = "Databases";

    public int MaxConcurrentProbes { get; set; } = 4;
    public List<DatabaseTargetOptions> Targets { get; set; } = [];
}

internal sealed class DatabaseTargetOptions
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string ConnectionString { get; set; } = string.Empty;
}
