using Logibooks.Alta.Configuration;

namespace Logibooks.Alta.Database;

internal interface IMssqlProbe
{
    Task ProbeAsync(RuntimeDatabaseTarget database, CancellationToken cancellationToken);
}
