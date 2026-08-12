namespace Logibooks.Alta.Configuration;

internal sealed class ServiceOptions
{
    public const string SectionName = "Service";

    public bool Enabled { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;
}
