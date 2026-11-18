// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using Logibooks.Alta.Configuration;
using Logibooks.Alta.CoreApi;
using Microsoft.Data.SqlClient;

namespace Logibooks.Alta.Database;

internal sealed record AltaDeclaration(Guid Id, string? Code, string? Procedure, string? Status, int? ElectronicStatus);

internal sealed record AltaParcel(
    Guid Id, string Number, string? DeclarationReference, string? Decision, decimal? WeightKg);

internal sealed record AltaRegisterSnapshot(
    string Cmr, IReadOnlyDictionary<Guid, AltaDeclaration> Declarations, IReadOnlyDictionary<Guid, AltaParcel> Parcels);

internal sealed record AltaGoodsLookup(IReadOnlyList<AltaGoods>? Items, string? Failure = null);

internal readonly record struct AltaGoodsKey(string Cmr, string ParcelNumber);

internal sealed record AltaAssignmentRequest(AltaGoodsKey Key, Guid DeclarationId, IReadOnlySet<Guid> IndPostIds);

internal sealed record AltaSourceRead(
    IReadOnlyList<AltaRegisterSnapshot> Registers, IReadOnlyDictionary<AltaGoodsKey, AltaGoodsLookup> Goods,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, AltaShipment?>> Shipments);

internal interface IAltaSource
{
    Task<AltaSourceRead> ReadAsync(RuntimeDatabaseTarget target, IReadOnlyList<string> cmrs, IReadOnlySet<string> worklist,
        Func<IReadOnlyList<AltaRegisterSnapshot>, IReadOnlyCollection<AltaAssignmentRequest>> selectAssignments,
        CancellationToken token);
}

internal sealed class AltaSqlSource : IAltaSource
{
    private const int GroupTimeoutSeconds = 40;
    private const int SingleRegisterTimeoutSeconds = 75;
    private static readonly string SqlWhitespace = new(
        Enumerable.Range(char.MinValue, char.MaxValue + 1).Select(value => (char)value).Where(char.IsWhiteSpace).ToArray());
    private static readonly string[] GoodsFields = ["GOODSDESCRIPTION", "GOODSCODE", "GOODSQUANTITY", "TOTALCOST"];

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
        IReadOnlySet<string> worklist,
        Func<IReadOnlyList<AltaRegisterSnapshot>, IReadOnlyCollection<AltaAssignmentRequest>> selectAssignments,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(cmrs.Count == 1 ? SingleRegisterTimeoutSeconds : GroupTimeoutSeconds));
        var readToken = deadline.Token;
        await using var connection = new SqlConnection(target.ConnectionString);
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
        // Select submissions from the table snapshots first. DEC XML and E2IList are payload data read on demand.
        var requests = selectAssignments(registers);
        var shipments = await ReadDeclarationDetailsAsync(transaction,
            requests.Select(request => request.DeclarationId).Distinct().ToArray(), readToken);
        var goodsRequests = requests.Where(request => shipments.ContainsKey(request.DeclarationId)).ToArray();
        var goods = goodsRequests.Length == 0
            ? new Dictionary<AltaGoodsKey, AltaGoodsLookup>()
            : await ReadGoodsAsync(transaction, worklist, goodsRequests, readToken);
        await transaction.CommitAsync(readToken);
        return new AltaSourceRead(registers, goods, shipments);
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
        SqlTransaction transaction, IReadOnlyCollection<Guid> declarationIds, CancellationToken token)
    {
        var shipments = new Dictionary<Guid, IReadOnlyDictionary<Guid, AltaShipment?>>();
        if (declarationIds.Count == 0)
            return shipments;

        await using var command = CreateCommand(transaction, """
            SELECT d.GID, d.BLOB
            FROM OPENJSON(@ids) AS k
            JOIN dbo.Docs AS d ON d.GID = TRY_CONVERT(uniqueidentifier, k.[value])
            WHERE d.DOCTYP = 'ExpressCargoDeclaration';
            """);
        command.Parameters.Add("@ids", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(declarationIds);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetGuid(0);
            if (await reader.IsDBNullAsync(1, token))
                continue;

            try
            {
                await using var stream = reader.GetStream(1);
                shipments.Add(id, await AltaDeclarationDocument.ReadAsync(stream, token));
            }
            catch (XmlException)
            {
                // Missing or invalid shipment details defer just the linked parcel numbers.
            }
        }
        return shipments;
    }

    private static async Task<Dictionary<AltaGoodsKey, AltaGoodsLookup>> ReadGoodsAsync(SqlTransaction transaction,
        IReadOnlySet<string> worklist, IReadOnlyCollection<AltaAssignmentRequest> requests, CancellationToken token)
    {
        var slots = await ReadGoodsSlotsAsync(transaction, token);
        var values = string.Join(",\n", slots.Select(slot =>
        {
            var prefix = $"InvoiceGoods\\{slot.Number}\\";
            var names = GoodsFields.Select(field => $"e.[{prefix}{field}]");
            var marking = slot.HasMarking ? $"e.[{prefix}GOODMARKING]" : "NULL";
            return $"({slot.Number}, {string.Join(", ", names)}, {marking})";
        }));

        var sql = $"""
            SELECT e.REGISTRATION_PRDOCUMENTNUMBER, e.DOCGID,
                   g.Slot, g.Description, g.TnVed, g.Quantity, g.TotalCost, g.Marking
            FROM dbo.E2IList AS e
            CROSS APPLY (VALUES {values}) AS g(Slot, Description, TnVed, Quantity, TotalCost, Marking)
            WHERE EXISTS (
                SELECT 1 FROM OPENJSON(@keys) AS k
                WHERE k.[value] COLLATE DATABASE_DEFAULT =
                    TRIM(@whitespace FROM CONVERT(nvarchar(4000), e.REGISTRATION_PRDOCUMENTNUMBER))
            );
            """;

        var numbers = requests.Select(request => request.Key.ParcelNumber).Distinct(StringComparer.Ordinal).ToArray();
        var invoices = numbers.ToDictionary(number => number,
            _ => new Dictionary<Guid, List<AltaGoodsSlot>>(), StringComparer.Ordinal);
        await using var command = CreateCommand(transaction, sql);
        AddKeys(command, numbers);
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                if (!invoices.TryGetValue(ReadText(reader, 0)?.Trim() ?? "", out var matching))
                    continue;
                var id = reader.GetGuid(1);
                if (!matching.TryGetValue(id, out var items))
                    matching.Add(id, items = []);
                items.Add(new AltaGoodsSlot(reader.GetInt32(2), ReadText(reader, 3), ReadText(reader, 4),
                    ReadText(reader, 5), ReadText(reader, 6), ReadText(reader, 7)));
            }
        }

        var invoiceIds = invoices.Values.SelectMany(matching => matching.Keys).Distinct().ToArray();
        var parents = invoiceIds.Length == 0
            ? new Dictionary<Guid, Guid>()
            : await ReadInvoiceParentsAsync(transaction, invoiceIds, token);
        var parentless = invoices.Where(pair => pair.Value.Count == 1 &&
                parents.GetValueOrDefault(pair.Value.Keys.Single()) == Guid.Empty)
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var sharedWithoutParent = parentless.Count == 0 || worklist.Count <= 1
            ? new HashSet<string>(StringComparer.Ordinal)
            : await ReadSharedParcelNumbersAsync(transaction, worklist, parentless, token);
        return requests.ToDictionary(request => request.Key,
            request => ReadInvoice(invoices[request.Key.ParcelNumber], parents, request.IndPostIds,
                allowParentless: !sharedWithoutParent.Contains(request.Key.ParcelNumber)));
    }

    private static async Task<Dictionary<Guid, Guid>> ReadInvoiceParentsAsync(SqlTransaction transaction,
        IReadOnlyCollection<Guid> ids, CancellationToken token)
    {
        var parents = new Dictionary<Guid, Guid>();
        await using var command = CreateCommand(transaction, """
            SELECT d.GID, d.PARENTGID
            FROM OPENJSON(@ids) AS k
            JOIN dbo.Docs AS d ON d.GID = TRY_CONVERT(uniqueidentifier, k.[value])
            WHERE d.PARENTGID IS NOT NULL;
            """);
        command.Parameters.Add("@ids", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(ids);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            parents.Add(reader.GetGuid(0), reader.GetGuid(1));
        return parents;
    }

    private static async Task<HashSet<string>> ReadSharedParcelNumbersAsync(SqlTransaction transaction,
        IReadOnlySet<string> worklist, IReadOnlySet<string> numbers, CancellationToken token)
    {
        // A number-only fallback for a parentless invoice must be independent of polling group boundaries.
        await using var command = CreateCommand(transaction, """
            SELECT p.Num, p.AVIANUM
            FROM dbo.IndPostList AS p
            WHERE EXISTS (
                SELECT 1 FROM OPENJSON(@keys) AS k
                WHERE k.[value] COLLATE DATABASE_DEFAULT = TRIM(@whitespace FROM CONVERT(nvarchar(4000), p.Num))
            ) AND EXISTS (
                SELECT 1 FROM OPENJSON(@worklist) AS w
                WHERE w.[value] COLLATE DATABASE_DEFAULT = TRIM(@whitespace FROM CONVERT(nvarchar(4000), p.AVIANUM))
            );
            """);
        AddKeys(command, numbers);
        command.Parameters.Add("@worklist", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(worklist);
        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        var shared = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var number = ReadText(reader, 0)?.Trim() ?? "";
            var cmr = ReadText(reader, 1)?.Trim() ?? "";
            if (numbers.Contains(number) && worklist.Contains(cmr) &&
                !scopes.TryAdd(number, cmr) && scopes[number] != cmr)
                shared.Add(number);
        }
        return shared;
    }

    private static async Task<GoodsSlotColumns[]> ReadGoodsSlotsAsync(SqlTransaction transaction, CancellationToken token)
    {
        await using var command = CreateCommand(transaction,
            "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.E2IList');");
        await using var reader = await command.ExecuteReaderAsync(token);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new SortedSet<int>();
        while (await reader.ReadAsync(token))
        {
            var name = reader.GetString(0);
            columns.Add(name);
            var parts = name.Split('\\');
            if (parts is [var prefix, var ordinal, var field] &&
                prefix.Equals("InvoiceGoods", StringComparison.OrdinalIgnoreCase) &&
                (GoodsFields.Contains(field, StringComparer.OrdinalIgnoreCase) ||
                 field.Equals("GOODMARKING", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) &&
                slot > 0 && slot.ToString(CultureInfo.InvariantCulture) == ordinal)
                slots.Add(slot);
        }

        if (slots.Count == 0)
            throw new InvalidDataException("Alta E2IList has no numbered InvoiceGoods columns.");

        return slots.Select(slot =>
        {
            var prefix = $"InvoiceGoods\\{slot}\\";
            if (GoodsFields.Any(field => !columns.Contains(prefix + field)))
                throw new InvalidDataException($"Alta E2IList goods slot {slot} has incomplete column metadata.");
            return new GoodsSlotColumns(slot, columns.Contains(prefix + "GOODMARKING"));
        }).ToArray();
    }

    internal static AltaGoodsLookup ReadInvoice(IReadOnlyDictionary<Guid, List<AltaGoodsSlot>> invoices,
        IReadOnlyDictionary<Guid, Guid>? parents = null, IReadOnlySet<Guid>? indPostIds = null, bool allowParentless = true)
    {
        if (invoices.Count == 0)
            return new(null, "No matching E2IList invoice");

        var selected = invoices.Values;
        if (invoices.Count == 1)
        {
            var id = invoices.Keys.Single();
            var parent = parents?.GetValueOrDefault(id) ?? Guid.Empty;
            if (parent == Guid.Empty && !allowParentless)
                return new(null, "E2IList invoice has no parent and more than one register uses this parcel number");
            if (parent != Guid.Empty && indPostIds?.Contains(parent) != true)
                return new(null, "E2IList invoice belongs to another IndPost document");
        }
        else
        {
            // The Alta invoice document is a child of its IndPost document in reestr_1.
            var linked = invoices.Where(invoice => parents?.TryGetValue(invoice.Key, out var parent) == true &&
                indPostIds?.Contains(parent) == true).Select(invoice => invoice.Value).Take(2).ToArray();
            if (linked.Length != 1)
                return new(null, "Several E2IList invoices match without a unique IndPost parent link");
            selected = linked;
        }

        var items = new List<AltaGoods>();
        foreach (var slot in selected.Single().OrderBy(slot => slot.Position))
        {
            if (!slot.IsPopulated)
                continue;
            if (string.IsNullOrWhiteSpace(slot.Description) ||
                !TryReadDecimal(slot.Quantity, out var quantity) || !TryReadDecimal(slot.TotalCost, out var total))
                return new(null, $"Incomplete or invalid E2IList goods slot {slot.Position}");

            items.Add(new AltaGoods(slot.Description, FirstText(slot.TnVed, null), quantity, total));
        }
        return items.Count == 0 ? new(null, "E2IList invoice has no populated goods") : new(items);
    }

    internal static bool TryReadDecimal(string? source, out decimal value)
    {
        var text = source?.Trim() ?? "";
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value))
            return false;

        // Decimal.TryParse can round an otherwise valid value with more precision than decimal supports.
        var unsigned = text.TrimStart('+', '-');
        var dot = unsigned.IndexOf('.');
        var whole = (dot < 0 ? unsigned : unsigned[..dot]).TrimStart('0');
        var fraction = dot < 0 ? "" : unsigned[(dot + 1)..].TrimEnd('0');
        var canonical = whole.Length == 0 ? "0" : whole;
        if (fraction.Length != 0)
            canonical += "." + fraction;
        if (text.StartsWith('-') && canonical != "0")
            canonical = "-" + canonical;
        return canonical == value.ToString("0.############################", CultureInfo.InvariantCulture);
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

    private sealed record GoodsSlotColumns(int Number, bool HasMarking);

    private sealed record RegisterRows(
        string Cmr, Dictionary<Guid, AltaDeclaration> Declarations, Dictionary<Guid, AltaParcel> Parcels);
}

internal sealed record AltaGoodsSlot(
    int Position, string? Description, string? TnVed, string? Quantity, string? TotalCost, string? Marking)
{
    public bool IsPopulated =>
        !string.IsNullOrWhiteSpace(Description) || !string.IsNullOrWhiteSpace(TnVed) ||
        !string.IsNullOrWhiteSpace(Quantity) || !string.IsNullOrWhiteSpace(TotalCost) ||
        !string.IsNullOrWhiteSpace(Marking);
}
