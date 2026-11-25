// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Logibooks.Alta.DevTool;

internal static class DevelopmentDatabase
{
    public const string Name = "reestr_1";
    private const string Marker = "LogibooksAltaDevelopmentSchema";
    private const int Version = 1;
    private const string BackupPath = "/baseline/reestr_1.bak";
    private const string ReaderLogin = "logibooks";
    private const string WriterLogin = "alta_dev_writer";
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record DatabaseFile(string LogicalName, string Type, long Size);
    private sealed record Manifest(int Version, string Sha256, DatabaseFile[] Files);

    public static async Task<string> InspectSourceAsync()
    {
        await using var source = new SqlConnection(Program.EnvironmentValue("ALTA_SOURCE_CONNECTION_STRING"));
        await source.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT name, collation_name, compatibility_level FROM sys.databases WHERE database_id=DB_ID();
            """, source);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || !reader.GetString(0).Equals(Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The source connection must select {Name}.");

        var collation = reader.GetString(1);
        if (!collation.Equals("Cyrillic_General_CI_AS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported source collation: {collation}");
        return JsonSerializer.Serialize(new { collation, compatibility = reader.GetByte(2) });
    }

    public static async Task<string> InitializeAsync(bool reset)
    {
        var admin = Login("sa", Program.EnvironmentValue("MSSQL_SA_PASSWORD"), "master");
        var reader = Login(ReaderLogin, Program.EnvironmentValue("ALTA_DB_PASSWORD"), Name);
        var writer = Login(WriterLogin, Program.EnvironmentValue("ALTA_DEV_WRITER_PASSWORD"), Name);
        await using var master = new SqlConnection(admin.ConnectionString);
        await master.OpenAsync();
        await using var acquire = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource=N'Logibooks.Alta.DevelopmentDatabase', @LockMode='Exclusive',
                @LockOwner='Session', @LockTimeout=30000;
            SELECT @result;
            """, master) { CommandTimeout = 40 };
        if ((int)(await acquire.ExecuteScalarAsync())! < 0)
            throw new InvalidDataException("Another Alta database initializer is running.");

        await using var find = new SqlCommand("SELECT CAST(IIF(DB_ID(N'reestr_1') IS NULL, 0, 1) AS bit);", master);
        var exists = await find.ExecuteScalarAsync() is true;
        if (exists)
            await RequireMarkerAsync(master, "[reestr_1].sys.extended_properties");
        else if (reset)
            throw new InvalidDataException("Reset requires an existing marked development database.");

        if (!exists || reset)
        {
            var files = await ValidateBackupAsync(master);
            await InstallBackupAsync(master, admin, reader, writer, files, reset);
        }
        else
            await ConfigureAccessAsync(master, admin, reader, writer, Name);
        return exists && !reset ? "Alta development database is ready." : "Restored the empty Alta development database.";
    }

    public static async Task<string> PrepareBackupAsync(string serverBackupPath)
    {
        await using var database = ScratchConnection();
        await database.OpenAsync();
        await RequireEmptySchemaAsync(database);
        await ExecuteAsync(database, "DBCC CHECKDB WITH NO_INFOMSGS, DATA_PURITY;");
        await using var mark = new SqlCommand("""
            IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE class=0 AND name=@marker)
                EXEC sys.sp_addextendedproperty @name=@marker, @value=@version;
            """, database);
        mark.Parameters.AddWithValue("@marker", Marker);
        mark.Parameters.AddWithValue("@version", Version);
        await mark.ExecuteNonQueryAsync();
        await RequireMarkerAsync(database);
        await ExecuteAsync(database, """
            BACKUP DATABASE [reestr_1] TO DISK=@backup
              WITH COPY_ONLY, COMPRESSION, CHECKSUM, INIT, NAME=N'Logibooks Alta development baseline';
            """, ("@backup", serverBackupPath));
        return "Saved the empty development backup inside the scratch SQL Server.";
    }

    public static async Task<string> WriteManifestAsync(string serverBackupPath, string localBackupPath)
    {
        await using var database = ScratchConnection();
        await database.OpenAsync();
        await RequireMarkerAsync(database);
        var files = await ReadFileListAsync(database, serverBackupPath);
        var manifest = new Manifest(Version, await FileHashAsync(localBackupPath), files);
        await File.WriteAllTextAsync(ManifestPath(localBackupPath), JsonSerializer.Serialize(manifest, ManifestJson));
        return $"Prepared {localBackupPath} ({new FileInfo(localBackupPath).Length:N0} bytes) and manifest " +
            $"({files.Sum(file => file.Size):N0} bytes restored allocation).";
    }

    public static async Task RequireMarkerAsync(SqlConnection connection, string properties = "sys.extended_properties")
    {
        await using var command = new SqlCommand($"""
            SELECT TRY_CONVERT(int, value) FROM {properties} WHERE class=0 AND name=@marker;
            """, connection);
        command.Parameters.AddWithValue("@marker", Marker);
        if (await command.ExecuteScalarAsync() is not int version || version != Version)
            throw new InvalidDataException("Expected a marked Alta development database with the supported schema version.");
    }

    private static async Task<DatabaseFile[]> ValidateBackupAsync(SqlConnection master)
    {
        await using var stream = File.OpenRead(ManifestPath(BackupPath));
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, ManifestJson);
        if (manifest is not { Version: Version, Files.Length: > 0 })
            throw new InvalidDataException("The Alta development manifest is missing the supported version or file list.");

        var files = await ReadFileListAsync(master, BackupPath);
        if (!files.SequenceEqual(manifest.Files))
            throw new InvalidDataException("The Alta development backup file list does not match its manifest.");
        // Hash the SQL Server mount, which is also the file used by RESTORE.
        await using var verifyHash = new SqlCommand("""
            SELECT HASHBYTES('SHA2_256', BulkColumn)
            FROM OPENROWSET(BULK '/baseline/reestr_1.bak', SINGLE_BLOB) AS backup_content;
            """, master);
        if (await verifyHash.ExecuteScalarAsync() is not byte[] hash ||
            !string.Equals(manifest.Sha256, Convert.ToHexString(hash), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Alta development backup does not match its manifest.");
        await ExecuteAsync(master, "RESTORE VERIFYONLY FROM DISK=@backup WITH CHECKSUM;", ("@backup", BackupPath));
        return files;
    }

    private static async Task<DatabaseFile[]> ReadFileListAsync(SqlConnection connection, string path)
    {
        await using var command = new SqlCommand("RESTORE FILELISTONLY FROM DISK=@backup;", connection);
        command.Parameters.AddWithValue("@backup", path);
        await using var reader = await command.ExecuteReaderAsync();
        var files = new List<DatabaseFile>();
        while (await reader.ReadAsync())
        {
            var type = reader.GetString(reader.GetOrdinal("Type"));
            if (type is not ("D" or "L"))
                throw new InvalidDataException($"Unsupported database file type {type} in the development backup.");
            files.Add(new DatabaseFile(reader.GetString(reader.GetOrdinal("LogicalName")), type,
                Convert.ToInt64(reader.GetValue(reader.GetOrdinal("Size")))));
        }
        if (files.Count == 0)
            throw new InvalidDataException("The backup contains no database files.");
        return files.OrderBy(file => file.LogicalName, StringComparer.Ordinal).ToArray();
    }

    private static async Task InstallBackupAsync(SqlConnection master, SqlConnectionStringBuilder admin,
        SqlConnectionStringBuilder reader, SqlConnectionStringBuilder writer, DatabaseFile[] files, bool reset)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var candidate = "reestr_1_candidate_" + suffix;
        try
        {
            await RestoreAsync(master, files, candidate);
            await using (var database = await OpenDatabaseAsync(admin, candidate))
            {
                await RequireMarkerAsync(database);
                await RequireEmptySchemaAsync(database);
            }
            await ConfigureAccessAsync(master, admin, reader, writer, candidate, provisionLogins: !reset);

            if (reset)
            {
                var previous = "reestr_1_previous_" + suffix;
                await ExecuteAsync(master, $"""
                    BEGIN TRY
                        ALTER DATABASE [reestr_1] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        ALTER DATABASE [reestr_1] MODIFY NAME = [{previous}];
                        ALTER DATABASE [{candidate}] MODIFY NAME = [reestr_1];
                    END TRY
                    BEGIN CATCH
                        IF DB_ID(N'{previous}') IS NOT NULL AND DB_ID(N'reestr_1') IS NULL
                            ALTER DATABASE [{previous}] MODIFY NAME = [reestr_1];
                        IF DB_ID(N'reestr_1') IS NOT NULL
                            ALTER DATABASE [reestr_1] SET MULTI_USER;
                        THROW;
                    END CATCH;
                    DROP DATABASE [{previous}];
                    """);
            }
            else
                await ExecuteAsync(master, $"ALTER DATABASE [{candidate}] MODIFY NAME = [reestr_1];");
        }
        finally
        {
            await ExecuteAsync(master, $"IF DB_ID(N'{candidate}') IS NOT NULL DROP DATABASE [{candidate}];");
        }
    }

    private static Task RestoreAsync(SqlConnection master, DatabaseFile[] files, string candidate)
    {
        var firstData = Array.FindIndex(files, file => file.Type == "D");
        var moves = files.Select((file, index) =>
        {
            var extension = file.Type == "L" ? "ldf" : index == firstData ? "mdf" : "ndf";
            var logicalName = file.LogicalName.Replace("'", "''", StringComparison.Ordinal);
            return $"MOVE N'{logicalName}' TO N'/var/opt/mssql/data/{candidate}_{index}.{extension}'";
        });
        return ExecuteAsync(master, $"RESTORE DATABASE [{candidate}] FROM DISK=@backup WITH CHECKSUM, " +
            string.Join(", ", moves) + ";", ("@backup", BackupPath));
    }

    private static Task RequireEmptySchemaAsync(SqlConnection database) => ExecuteAsync(database, """
        IF EXISTS (SELECT 1 FROM dbo.Docs) OR EXISTS (SELECT 1 FROM dbo.IndPostList)
            OR EXISTS (SELECT 1 FROM dbo.ExpressCargoDeclarationList)
            THROW 50000, 'The development baseline contains documents.', 1;
        IF CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation')) <> N'Cyrillic_General_CI_AS'
            THROW 50001, 'The development baseline collation does not match the supported Alta schema.', 1;
        IF NOT EXISTS (SELECT 1 FROM OPENJSON(N'["Alta"]') WHERE [value]=TRIM(N' ' FROM N' Alta '))
            THROW 50004, 'The development baseline does not support the adapter polling query.', 1;
        """);

    private static async Task ConfigureAccessAsync(SqlConnection master, SqlConnectionStringBuilder admin,
        SqlConnectionStringBuilder reader, SqlConnectionStringBuilder writer, string databaseName, bool provisionLogins = true)
    {
        if (provisionLogins)
        {
            await ExecuteAsync(master, """
                DECLARE @quotedReader nvarchar(258)=QUOTENAME(@reader, ''''),
                        @quotedWriter nvarchar(258)=QUOTENAME(@writer, '''');
                IF SUSER_ID(N'logibooks') IS NULL
                    EXEC(N'CREATE LOGIN [logibooks] WITH PASSWORD=N' + @quotedReader);
                ELSE IF PWDCOMPARE(@reader, CONVERT(varbinary(256), LOGINPROPERTY(N'logibooks', 'PasswordHash'))) <> 1
                    EXEC(N'ALTER LOGIN [logibooks] WITH PASSWORD=N' + @quotedReader);
                IF SUSER_ID(N'alta_dev_writer') IS NULL
                    EXEC(N'CREATE LOGIN [alta_dev_writer] WITH PASSWORD=N' + @quotedWriter);
                ELSE IF PWDCOMPARE(@writer, CONVERT(varbinary(256), LOGINPROPERTY(N'alta_dev_writer', 'PasswordHash'))) <> 1
                    EXEC(N'ALTER LOGIN [alta_dev_writer] WITH PASSWORD=N' + @quotedWriter);
                """, ("@reader", reader.Password), ("@writer", writer.Password));
        }
        await using var database = await OpenDatabaseAsync(admin, databaseName);
        await ExecuteAsync(database, """
            IF USER_ID(N'logibooks') IS NULL CREATE USER [logibooks] FOR LOGIN [logibooks];
            ELSE ALTER USER [logibooks] WITH LOGIN=[logibooks];
            IF USER_ID(N'alta_dev_writer') IS NULL CREATE USER [alta_dev_writer] FOR LOGIN [alta_dev_writer];
            ELSE ALTER USER [alta_dev_writer] WITH LOGIN=[alta_dev_writer];
            GRANT SELECT ON dbo.Docs TO [logibooks];
            GRANT SELECT ON dbo.IndPostList TO [logibooks];
            GRANT SELECT ON dbo.ExpressCargoDeclarationList TO [logibooks];
            GRANT SELECT, INSERT ON dbo.Docs TO [alta_dev_writer];
            GRANT SELECT, INSERT ON dbo.IndPostList TO [alta_dev_writer];
            GRANT UPDATE ON dbo.IndPostList (REESTRGID, DECISION, ALLWEIGHT) TO [alta_dev_writer];
            GRANT SELECT, INSERT ON dbo.ExpressCargoDeclarationList TO [alta_dev_writer];
            GRANT VIEW DEFINITION TO [alta_dev_writer];
            """);
        await CheckAccessAsync(reader, databaseName, writer: false);
        await CheckAccessAsync(writer, databaseName, writer: true);
    }

    private static async Task CheckAccessAsync(SqlConnectionStringBuilder settings, string database, bool writer)
    {
        await using var connection = await OpenDatabaseAsync(settings, database);
        const string read = """
            SELECT TOP (0) GID, BLOB, DOCTYP, DOCSIZE, REGNUM, CRDATE, MODDATE, STATUS, EDSTAT FROM dbo.Docs;
            SELECT TOP (0) AVIANUM, DOCGID, Num, REESTRGID, DECISION, ALLWEIGHT FROM dbo.IndPostList;
            SELECT TOP (0) AviaNum, DOCGID, Num, RegNum, CustomsModeCode FROM dbo.ExpressCargoDeclarationList;
            """;
        await ExecuteAsync(connection, read);
        if (writer)
        {
            await RequireMarkerAsync(connection);
            await ExecuteAsync(connection, """
                IF HAS_PERMS_BY_NAME('dbo.Docs', 'OBJECT', 'INSERT') <> 1 OR
                   HAS_PERMS_BY_NAME('dbo.IndPostList', 'OBJECT', 'INSERT') <> 1 OR
                   HAS_PERMS_BY_NAME('dbo.ExpressCargoDeclarationList', 'OBJECT', 'INSERT') <> 1 OR
                   HAS_PERMS_BY_NAME('dbo.IndPostList', 'OBJECT', 'UPDATE', 'DECISION', 'COLUMN') <> 1 OR
                   HAS_PERMS_BY_NAME('dbo.IndPostList', 'OBJECT', 'UPDATE', 'ALLWEIGHT', 'COLUMN') <> 1 OR
                   HAS_PERMS_BY_NAME('dbo.IndPostList', 'OBJECT', 'UPDATE', 'REESTRGID', 'COLUMN') <> 1
                    THROW 50002, 'The development writer grants are incomplete.', 1;
                """);
        }
        else
        {
            await ExecuteAsync(connection, """
                IF EXISTS (
                        SELECT 1
                        FROM (VALUES ('dbo.Docs'), ('dbo.IndPostList'), ('dbo.ExpressCargoDeclarationList')) AS objects(name)
                        CROSS JOIN (VALUES ('INSERT'), ('UPDATE'), ('DELETE')) AS permissions(name)
                        WHERE HAS_PERMS_BY_NAME(objects.name, 'OBJECT', permissions.name)=1
                    ) OR EXISTS (
                        SELECT 1 FROM sys.columns
                        WHERE object_id IN (OBJECT_ID('dbo.Docs'), OBJECT_ID('dbo.IndPostList'),
                            OBJECT_ID('dbo.ExpressCargoDeclarationList'))
                          AND HAS_PERMS_BY_NAME('dbo.'+OBJECT_NAME(object_id), 'OBJECT', 'UPDATE', name, 'COLUMN')=1
                    )
                    THROW 50003, 'The adapter login has unexpected write access.', 1;
                """);
        }
    }

    private static async Task<SqlConnection> OpenDatabaseAsync(SqlConnectionStringBuilder settings, string database)
    {
        var target = new SqlConnectionStringBuilder(settings.ConnectionString) { InitialCatalog = database };
        var connection = new SqlConnection(target.ConnectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static SqlConnectionStringBuilder Login(string user, string password, string database)
    {
        if (password.Length > 128)
            throw new InvalidDataException($"The password for {user} exceeds the SQL Server limit.");
        return new SqlConnectionStringBuilder
        {
            DataSource = "mssql,1433",
            InitialCatalog = database,
            UserID = user,
            Password = password,
            TrustServerCertificate = true,
            Pooling = false
        };
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static string ManifestPath(string backup) => backup + ".manifest.json";

    private static SqlConnection ScratchConnection()
    {
        var settings = new SqlConnectionStringBuilder(Program.EnvironmentValue("ALTA_BASELINE_SCRATCH_CONNECTION_STRING"));
        if (!settings.InitialCatalog.Equals(Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The scratch connection must select {Name}.");
        return new SqlConnection(settings.ConnectionString);
    }

    private static async Task<string> FileHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
