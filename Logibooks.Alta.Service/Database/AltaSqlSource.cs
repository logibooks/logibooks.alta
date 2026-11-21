// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Data;
using System.Text.Json;
using System.Xml;
using Logibooks.Alta.Configuration;
using Microsoft.Data.SqlClient;

namespace Logibooks.Alta.Database;

internal sealed record AltaDeclaration(Guid Id, string? Code, string? Procedure, string? Status, int? ElectronicStatus);

internal sealed record AltaParcel(
    Guid Id, string Number, string? DeclarationReference, string? Decision, decimal? WeightKg);

internal sealed record AltaRegisterSnapshot(
    string Cmr, IReadOnlyDictionary<Guid, AltaDeclaration> Declarations, IReadOnlyDictionary<Guid, AltaParcel> Parcels);

internal sealed record AltaSourceRead(
    IReadOnlyList<AltaRegisterSnapshot> Registers,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, AltaShipment?>> Shipments);

internal interface IAltaSource
{
    Task<AltaSourceRead> ReadAsync(RuntimeDatabaseTarget target, IReadOnlyList<string> cmrs,
        Func<IReadOnlyList<AltaRegisterSnapshot>, IReadOnlyDictionary<Guid, HashSet<Guid>>> selectShipments,
        CancellationToken token);
}

internal sealed class AltaSqlSource : IAltaSource
{
    private const int GroupTimeoutSeconds = 40;
    private const int SingleRegisterTimeoutSeconds = 75;
    private static readonly string SqlWhitespace = new(
        Enumerable.Range(char.MinValue, char.MaxValue + 1).Select(value => (char)value).Where(char.IsWhiteSpace).ToArray());

    private const string SnapshotSql = """
        SELECT e.AviaNum, e.DOCGID, e.RegNum, d.REGNUM, e.CustomsModeCode, d.STATUS, d.EDSTAT
        FROM dbo.ExpressCargoDeclarationList AS e
        LEFT JOIN dbo.Docs AS d ON d.GID = e.DOCGID
        WHERE EXISTS (
            SELECT 1 FROM OPENJSON(@keys) AS k
            WHERE k.[value] COLLATE DATABASE_DEFAULT = TRIM(@whitespace FROM CONVERT(nvarchar(4000), e.AviaNum))
        );

        SELECT p.AVIANUM, p.DOCGID, p.Num, p.REESTRGID, p.DECISION, p.ALLWEIGHT
        FROM dbo.IndPostList AS p
        WHERE EXISTS (
            SELECT 1 FROM OPENJSON(@keys) AS k
            WHERE k.[value] COLLATE DATABASE_DEFAULT = TRIM(@whitespace FROM CONVERT(nvarchar(4000), p.AVIANUM))
        );
        """;

    public async Task<AltaSourceRead> ReadAsync(RuntimeDatabaseTarget target, IReadOnlyList<string> cmrs,
        Func<IReadOnlyList<AltaRegisterSnapshot>, IReadOnlyDictionary<Guid, HashSet<Guid>>> selectShipments,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(cmrs.Count == 1 ? SingleRegisterTimeoutSeconds : GroupTimeoutSeconds));
        var readToken = deadline.Token;
        await using var connection = SqlConnectionFactory.CreateUnpooledConnection(target.ConnectionString);
        await connection.OpenAsync(readToken);

        await using var isolation = connection.CreateCommand();
        isolation.CommandText = """
            SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID();
            """;
        isolation.CommandTimeout = SingleRegisterTimeoutSeconds;
        var state = await isolation.ExecuteScalarAsync(readToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            state is byte and 1 ? IsolationLevel.Snapshot : IsolationLevel.Serializable, readToken);

        var rows = await ReadRegistersAsync(transaction, cmrs, readToken);
        var registers = rows.Select(row => new AltaRegisterSnapshot(row.Cmr, row.Declarations, row.Parcels)).ToArray();
        // Select submissions from the table snapshots before reading their DEC XML.
        var shipments = await ReadDeclarationDetailsAsync(transaction, selectShipments(registers), readToken);
        await transaction.CommitAsync(readToken);
        return new AltaSourceRead(registers, shipments);
    }

    private static async Task<RegisterRows[]> ReadRegistersAsync(SqlTransaction transaction,
        IReadOnlyList<string> cmrs, CancellationToken token)
    {
        var declarations = cmrs.ToDictionary(cmr => cmr, _ => new Dictionary<Guid, AltaDeclaration>(),
            StringComparer.Ordinal);
        var parcels = cmrs.ToDictionary(cmr => cmr, _ => new Dictionary<Guid, AltaParcel>(), StringComparer.Ordinal);
        await using var command = CreateCommand(transaction, SnapshotSql);
        AddKeys(command, cmrs);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (!declarations.TryGetValue(ReadText(reader, 0)?.Trim() ?? "", out var rows))
                continue;
            var id = reader.GetGuid(1);
            rows.Add(id, new AltaDeclaration(id, FirstText(ReadText(reader, 2), ReadText(reader, 3)),
                ReadText(reader, 4)?.Trim(), ReadText(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        }

        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (!parcels.TryGetValue(ReadText(reader, 0)?.Trim() ?? "", out var rows))
                continue;
            var id = reader.GetGuid(1);
            var weight = reader.IsDBNull(5) ? (double?)null : reader.GetDouble(5);
            rows.Add(id, new AltaParcel(id, ReadText(reader, 2)?.Trim() ?? "", ReadText(reader, 3)?.Trim(),
                ReadText(reader, 4)?.Trim(), weight is >= 0 and < 10_000_000 ? (decimal)weight.Value : null));
        }

        return cmrs.Select(cmr => new RegisterRows(cmr, declarations[cmr], parcels[cmr])).ToArray();
    }

    private static async Task<Dictionary<Guid, IReadOnlyDictionary<Guid, AltaShipment?>>> ReadDeclarationDetailsAsync(
        SqlTransaction transaction, IReadOnlyDictionary<Guid, HashSet<Guid>> requestedShipments, CancellationToken token)
    {
        var shipments = new Dictionary<Guid, IReadOnlyDictionary<Guid, AltaShipment?>>();
        if (requestedShipments.Count == 0)
            return shipments;

        await using var command = CreateCommand(transaction, """
            SELECT d.GID, d.BLOB
            FROM OPENJSON(@ids) AS k
            JOIN dbo.Docs AS d ON d.GID = TRY_CONVERT(uniqueidentifier, k.[value])
            WHERE d.DOCTYP = 'ExpressCargoDeclaration';
            """);
        command.Parameters.Add("@ids", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(requestedShipments.Keys);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetGuid(0);
            if (await reader.IsDBNullAsync(1, token))
                continue;

            try
            {
                await using var stream = reader.GetStream(1);
                shipments.Add(id, await AltaDeclarationDocument.ReadAsync(stream, requestedShipments[id], token));
            }
            catch (XmlException)
            {
                // Missing or invalid shipment details defer just the linked parcel numbers.
            }
        }
        return shipments;
    }

    private static SqlCommand CreateCommand(SqlTransaction transaction, string sql) =>
        new(sql, transaction.Connection, transaction) { CommandTimeout = SingleRegisterTimeoutSeconds };

    private static void AddKeys(SqlCommand command, IReadOnlyCollection<string> keys)
    {
        command.Parameters.Add("@keys", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(keys);
        command.Parameters.Add("@whitespace", SqlDbType.NVarChar, SqlWhitespace.Length).Value = SqlWhitespace;
    }

    private static string? ReadText(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string? FirstText(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : string.IsNullOrWhiteSpace(second) ? null : second.Trim();

    private sealed record RegisterRows(
        string Cmr, Dictionary<Guid, AltaDeclaration> Declarations, Dictionary<Guid, AltaParcel> Parcels);
}
