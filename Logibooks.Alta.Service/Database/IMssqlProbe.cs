namespace Logibooks.Alta.Database;

internal interface IMssqlProbe
{
    Task ProbeAsync(CancellationToken cancellationToken);
}
