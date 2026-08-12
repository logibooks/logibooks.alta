using Microsoft.Data.SqlClient;
using System.Net.Sockets;
using Logibooks.Alta.Configuration;

namespace Logibooks.Alta.Database;

internal interface IDatabaseConnectionFactory
{
    IDatabaseConnection Create(string connectionString);
}

internal interface IDatabaseConnection : IAsyncDisposable
{
    Task OpenAsync(CancellationToken cancellationToken);
    Task<object?> ExecuteScalarAsync(
        string commandText,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken);
}

internal sealed class SqlConnectionFactory : IDatabaseConnectionFactory
{
    public IDatabaseConnection Create(string connectionString) =>
        new SqlDatabaseConnection(new SqlConnection(connectionString));
}

internal sealed class SqlDatabaseConnection(SqlConnection connection) : IDatabaseConnection
{
    public Task OpenAsync(CancellationToken cancellationToken) => connection.OpenAsync(cancellationToken);

    public async Task<object?> ExecuteScalarAsync(
        string commandText,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = commandTimeoutSeconds;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

internal sealed class MssqlProbe(
    IDatabaseConnectionFactory connectionFactory,
    IDatabaseRetryDelay retryDelay) : IMssqlProbe
{
    internal const string ProbeCommandText = "SELECT CAST(1 AS int)";
    internal const int CommandTimeoutSeconds = 30;
    private const int MaximumAttempts = 3;
    private static readonly HashSet<int> TransientSqlErrorNumbers =
    [
        -2, 20, 53, 64, 233, 258, 11_001, 10_053, 10_054, 10_060,
        40_197, 40_501, 40_613, 49_918, 49_919, 49_920, 10_928, 10_929
    ];

    public async Task ProbeAsync(
        RuntimeDatabaseTarget database,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(database.ConnectionString))
        {
            throw new InvalidOperationException(
                $"The MSSQL connection for database target '{database.Name}' is not configured.");
        }

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                await ProbeOnceAsync(database.ConnectionString, cancellationToken);
                return;
            }
            catch (Exception exception) when (
                attempt < MaximumAttempts && IsTransientNetworkFailure(exception))
            {
                await retryDelay.DelayAsync(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("The MSSQL retry loop completed without a result.");
    }

    private async Task ProbeOnceAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create(connectionString);
        await connection.OpenAsync(cancellationToken);
        var result = await connection.ExecuteScalarAsync(
            ProbeCommandText,
            CommandTimeoutSeconds,
            cancellationToken);
        if (result is null || Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException("The MSSQL connectivity probe returned an unexpected result.");
        }
    }

    private static bool IsTransientNetworkFailure(Exception exception) => exception switch
    {
        SqlException sqlException => sqlException.Errors
            .Cast<SqlError>()
            .Any(error => TransientSqlErrorNumbers.Contains(error.Number)),
        IOException => true,
        SocketException => true,
        TimeoutException => true,
        _ => false
    };
}

internal interface IDatabaseRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class DatabaseRetryDelay : IDatabaseRetryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
