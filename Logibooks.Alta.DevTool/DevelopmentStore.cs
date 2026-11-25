// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Logibooks.Alta.DevTool;

internal sealed class DevelopmentStore(SqlConnection connection, SqlTransaction transaction)
{
    private const string IndPostType = "IndPost";
    private const string DeclarationType = "ExpressCargoDeclaration";

    private sealed record ParcelRow(string Number, Guid Id, Guid? DeclarationId, string? Decision, double? Weight,
        byte[]? Xml);

    private sealed record DeclarationRow(Guid Id, string Cmr, string Number, string Procedure);

    public static async Task<string> RunAsync(string connectionString, Func<DevelopmentStore, Task<string>> operation)
    {
        var settings = new SqlConnectionStringBuilder(connectionString);
        if (!settings.InitialCatalog.Equals(DevelopmentDatabase.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The writer connection must select {DevelopmentDatabase.Name}.");

        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await DevelopmentDatabase.RequireMarkerAsync(connection);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var store = new DevelopmentStore(connection, transaction);
        await using var acquire = store.Command("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource=N'Logibooks.Alta.DevTool', @LockMode='Exclusive',
                @LockOwner='Transaction', @LockTimeout=15000;
            SELECT @result;
            """);
        if ((int)(await acquire.ExecuteScalarAsync())! < 0)
            throw new InvalidDataException("Another Alta development write did not finish in time.");

        var result = await operation(store);
        await transaction.CommitAsync();
        return result;
    }

    public async Task<string> ImportAsync(string cmr, IReadOnlyList<IndPost> imported)
    {
        var existing = await ReadParcelsAsync(cmr, includeXml: true);
        var added = 0;
        foreach (var parcel in imported)
        {
            if (existing.TryGetValue(parcel.Number, out var row))
            {
                if (row.Number != parcel.Number || !IndPostXml.Equivalent(parcel, ReadSource(row, cmr)))
                    throw new InvalidDataException($"Parcel {parcel.Number} conflicts with an existing document in {cmr}.");
                continue;
            }

            var id = Guid.NewGuid();
            await InsertDocumentAsync(id, IndPostType, IndPostXml.Serialize(parcel.Document));
            await using var insert = Command("""
                INSERT dbo.IndPostList (DOCGID, AVIANUM, Num, ALLWEIGHT)
                VALUES (@id, @cmr, @number, @weight);
                """, ("@id", id), ("@cmr", cmr), ("@number", parcel.Number));
            insert.Parameters.Add("@weight", SqlDbType.Float).Value = parcel.WeightKg is { } weight ? (double)weight : DBNull.Value;
            await insert.ExecuteNonQueryAsync();
            added++;
        }
        return $"{cmr}: imported {added}, reused {imported.Count - added}.";
    }

    public async Task<string> DeclareAsync(string cmr, string number, string procedure, string[] parcelNumbers)
    {
        var declaration = await FindDeclarationAsync(number);
        var parcels = await ReadParcelsAsync(cmr, includeXml: declaration is null);
        if (declaration is not null)
        {
            if (declaration.Cmr != cmr || declaration.Number != number || declaration.Procedure != procedure)
                throw new InvalidDataException($"DEC {number} already belongs to a different declaration.");
            var members = parcels.Values.Where(parcel => parcel.DeclarationId == declaration.Id).ToArray();
            if (parcelNumbers.Length > 0 && !parcelNumbers.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(members.Select(parcel => parcel.Number)))
                throw new InvalidDataException($"DEC {number} already has a different parcel selection.");
            return $"{cmr}: reused DEC {number} with {members.Length} parcels.";
        }

        var selected = (parcelNumbers.Length == 0
                ? parcels.Values.Where(parcel => parcel.DeclarationId is null)
                : parcelNumbers.Select(parcel => FindParcel(parcels, cmr, parcel)))
            .OrderBy(parcel => parcel.Number, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0)
            throw new InvalidDataException($"No unlinked parcels in {cmr}.");
        if (selected.Any(parcel => parcel.DeclarationId.HasValue))
            throw new InvalidDataException("The parcel selection contains a parcel already linked to a DEC.");

        var id = Guid.NewGuid();
        var xml = IndPostXml.BuildDeclaration(cmr, number, procedure,
            selected.Select(parcel => (parcel.Id, ReadSource(parcel, cmr))));
        await InsertDocumentAsync(id, DeclarationType, xml, number);
        await using var insert = Command("""
            INSERT dbo.ExpressCargoDeclarationList (DOCGID, AviaNum, Num, RegNum, CustomsModeCode)
            VALUES (@id, @cmr, @number, @number, @procedure);
            """, ("@id", id), ("@cmr", cmr), ("@number", number), ("@procedure", procedure));
        await insert.ExecuteNonQueryAsync();
        await using var link = Command("UPDATE dbo.IndPostList SET REESTRGID=@reference WHERE DOCGID=@id;",
            ("@reference", id.ToString("B")), ("@id", Guid.Empty));
        foreach (var parcel in selected)
        {
            link.Parameters["@id"].Value = parcel.Id;
            await link.ExecuteNonQueryAsync();
        }
        return $"{cmr}: created DEC {number} for {selected.Length} parcels.";
    }

    public async Task<string> DecideAsync(string cmr, string number, int code, string? parcelNumber, decimal? weight)
    {
        var declaration = await FindDeclarationAsync(number);
        if (declaration is null || declaration.Cmr != cmr || declaration.Number != number)
            throw new InvalidDataException($"DEC {number} was not found in {cmr}.");

        var parcels = await ReadParcelsAsync(cmr, includeXml: false);
        var selected = parcelNumber is null
            ? parcels.Values.Where(parcel => parcel.DeclarationId == declaration.Id).ToArray()
            : [FindParcel(parcels, cmr, parcelNumber)];
        if (selected.Length == 0 || selected.Any(parcel => parcel.DeclarationId != declaration.Id))
            throw new InvalidDataException($"The parcel selection does not belong to DEC {number}.");

        var decision = code.ToString(CultureInfo.InvariantCulture);
        var weightKg = (double?)weight;
        await using var update = Command("""
            UPDATE dbo.IndPostList SET DECISION=@decision, ALLWEIGHT=COALESCE(@weight, ALLWEIGHT) WHERE DOCGID=@id;
            """, ("@decision", decision), ("@id", Guid.Empty));
        update.Parameters.Add("@weight", SqlDbType.Float).Value = (object?)weightKg ?? DBNull.Value;
        var changed = 0;
        foreach (var parcel in selected)
        {
            if (decision == parcel.Decision && (!weightKg.HasValue || weightKg == parcel.Weight))
                continue;
            update.Parameters["@id"].Value = parcel.Id;
            changed += await update.ExecuteNonQueryAsync();
        }
        return $"{cmr}: updated {changed} of {selected.Length} parcels for DEC {number}.";
    }

    private async Task<Dictionary<string, ParcelRow>> ReadParcelsAsync(string cmr, bool includeXml)
    {
        await using var command = Command("""
            SELECT p.Num, p.DOCGID, p.REESTRGID, p.DECISION, p.ALLWEIGHT, p.AVIANUM,
                CASE WHEN @includeXml=1 THEN CONVERT(varbinary(max), d.BLOB) END
            FROM dbo.IndPostList AS p
            LEFT JOIN dbo.Docs AS d ON p.DOCGID=d.GID AND d.DOCTYP='IndPost'
            WHERE p.AVIANUM=@cmr;
            """, ("@includeXml", includeXml), ("@cmr", cmr));
        await using var reader = await command.ExecuteReaderAsync();
        var result = new Dictionary<string, ParcelRow>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            if (reader.GetString(5).Trim() != cmr || reader.IsDBNull(0))
                throw new InvalidDataException($"Ambiguous Alta parcel keys in {cmr}.");
            var number = reader.GetString(0).Trim();
            var reference = reader.IsDBNull(2) ? null : reader.GetString(2).Trim();
            Guid? declarationId = reference is null ? null : Guid.TryParse(reference, out var id) ? id
                : throw new InvalidDataException($"Parcel {number} has an invalid DEC reference.");
            if (!result.TryAdd(number, new ParcelRow(number, reader.GetGuid(1), declarationId,
                    reader.IsDBNull(3) ? null : reader.GetString(3).Trim(), reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6))))
                throw new InvalidDataException($"Parcel {number} is ambiguous in {cmr}.");
        }
        return result;
    }

    private async Task<DeclarationRow?> FindDeclarationAsync(string number)
    {
        await using var command = Command("""
            SELECT e.DOCGID, e.AviaNum, COALESCE(e.RegNum, d.REGNUM), e.CustomsModeCode
            FROM dbo.ExpressCargoDeclarationList AS e JOIN dbo.Docs AS d ON e.DOCGID=d.GID
            WHERE e.RegNum=@number OR d.REGNUM=@number;
            """, ("@number", number));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        var result = new DeclarationRow(reader.GetGuid(0), reader.GetString(1).Trim(), reader.GetString(2).Trim(),
            reader.GetString(3).Trim());
        if (await reader.ReadAsync())
            throw new InvalidDataException($"DEC {number} is ambiguous.");
        return result;
    }

    private async Task InsertDocumentAsync(Guid id, string type, byte[] xml, string? registrationNumber = null)
    {
        await using var command = Command("""
            INSERT dbo.Docs (GID, DOCTYP, BLOB, DOCSIZE, REGNUM, CRDATE, MODDATE)
            VALUES (@id, @type, @xml, @size, @registration, GETDATE(), GETDATE());
            """, ("@id", id), ("@type", type), ("@size", xml.Length));
        command.Parameters.Add("@xml", SqlDbType.VarBinary, -1).Value = xml;
        command.Parameters.Add("@registration", SqlDbType.VarChar, 25).Value = (object?)registrationNumber ?? DBNull.Value;
        await command.ExecuteNonQueryAsync();
    }

    private SqlCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static ParcelRow FindParcel(Dictionary<string, ParcelRow> parcels, string cmr, string number) =>
        parcels.TryGetValue(number, out var parcel) && parcel.Number == number
            ? parcel : throw new InvalidDataException($"Parcel {number} was not found in {cmr}.");

    private static IndPost ReadSource(ParcelRow row, string cmr)
    {
        if (row.Xml is null)
            throw new InvalidDataException($"Parcel {row.Number} has no saved IndPost XML.");
        using var stream = new MemoryStream(row.Xml);
        var parcel = IndPostXml.ReadParcel(stream, cmr, row.Number);
        return parcel.Number == row.Number ? parcel
            : throw new InvalidDataException($"Saved XML does not match parcel {row.Number}.");
    }
}
