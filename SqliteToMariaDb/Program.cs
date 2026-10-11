using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using TightWiki.Repository.Helpers;

namespace ContinuumWiki.SqliteToMariaDb;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        MigrationOptions? options = null;
        var started = DateTimeOffset.UtcNow;
        try
        {
            options = MigrationOptions.Parse(args);
            var engine = new MigrationEngine(options);
            var report = options.Operation switch
            {
                "preflight" => await engine.PreflightAsync(),
                "integrity" => await engine.IntegrityAsync(),
                "dry-run" => await engine.DryRunAsync(),
                "execute" => await engine.ExecuteAsync(),
                "verify" => await engine.VerifyAsync(),
                _ => throw new InvalidOperationException("Unsupported operation.")
            };
            await WriteReportAsync(options.ReportPath, report);
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return report.Succeeded ? 0 : 1;
        }
        catch (Exception exception)
        {
            var report = new MigrationReport(
                options?.Operation ?? "invalid",
                started,
                DateTimeOffset.UtcNow,
                "mariadb",
                null,
                null,
                null,
                null,
                [],
                [],
                [],
                [],
                [],
                [exception.Message],
                [],
                0,
                false,
                false);
            if (options != null)
                await WriteReportAsync(options.ReportPath, report);
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task WriteReportAsync(string path, MigrationReport report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions));
    }
}

internal sealed class MigrationEngine
{
    private readonly MigrationOptions _options;
    private readonly IReadOnlyList<MigrationMapping> _mappings;
    private readonly Dictionary<string, string> _sourcePaths;
    private readonly Dictionary<int, string> _pageNavigations = [];

    public MigrationEngine(MigrationOptions options)
    {
        _options = options;
        _sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["config"] = Path.Combine(options.SourceRoot, "config.db"),
            ["users"] = Path.Combine(options.SourceRoot, "users.db"),
            ["pages"] = Path.Combine(options.SourceRoot, "pages.db"),
            ["deleted-pages"] = Path.Combine(options.SourceRoot, "deletedpages.db"),
            ["deleted-revisions"] = Path.Combine(options.SourceRoot, "deletedpagerevisions.db"),
            ["logging"] = Path.Combine(options.SourceRoot, "logging.db"),
            ["statistics"] = Path.Combine(options.SourceRoot, "statistics.db"),
            ["emoji"] = Path.Combine(options.SourceRoot, "emoji.db"),
            ["translations"] = options.TranslationDatabase,
            ["defaults"] = options.DefaultsDatabase
        };
        _mappings = BuildMappings();
    }

    public Task<MigrationReport> PreflightAsync() => RunAsync("preflight", mutate: false, verify: false);
    public Task<MigrationReport> IntegrityAsync() => RunAsync("integrity", mutate: false, verify: false);
    public Task<MigrationReport> DryRunAsync() => RunAsync("dry-run", mutate: false, verify: false);
    public Task<MigrationReport> VerifyAsync() => RunAsync("verify", mutate: false, verify: true);

    public async Task<MigrationReport> ExecuteAsync()
    {
        var report = await RunAsync("execute", mutate: true, verify: false);
        if (!report.Succeeded) return report;

        var verification = await RunAsync("verify", mutate: false, verify: true);
        if (!verification.Succeeded)
            return verification with { Operation = "execute", DestinationMutated = true };

        await using var connection = new MySqlConnection(_options.DestinationConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("CREATE TABLE IF NOT EXISTS ContinuumWikiMigrationCompletion (MigrationKey VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL, SourceRootHash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL, CompletedAtUtc DATETIME(6) NOT NULL, PRIMARY KEY (MigrationKey)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");
        var sourceHash = await ComputeSourceRootHashAsync();
        await connection.ExecuteAsync("INSERT INTO ContinuumWikiMigrationCompletion (MigrationKey, SourceRootHash, CompletedAtUtc) VALUES (@MigrationKey, @SourceRootHash, UTC_TIMESTAMP(6));", new { MigrationKey = "sqlite-to-mariadb-v1", SourceRootHash = sourceHash });
        return report with
        {
            Operation = "execute",
            CompletedUtc = verification.CompletedUtc,
            CompletionMarker = "sqlite-to-mariadb-v1",
            Discrepancies = verification.Discrepancies,
            DestinationMutated = true,
            Succeeded = verification.Succeeded
        };
    }

    private async Task<MigrationReport> RunAsync(string operation, bool mutate, bool verify)
    {
        var started = DateTimeOffset.UtcNow;
        var conflicts = new List<string>();
        var unsupported = new List<string>();
        var exclusions = new List<string>();
        var discrepancies = new List<string>();
        var mappings = new List<MappingReport>();
        var integrityFindings = new List<IntegrityFinding>();
        var repairPlan = new List<RepairPlanEntry>();
        DestinationInfo? destination = null;
        var sourceStores = new List<SourceStoreReport>();
        var destinationMutated = false;

        ValidateOptions();
        await ValidateSourceTablesAsync();
        await LoadPageNavigationIndexAsync();
        foreach (var store in _sourcePaths)
            sourceStores.Add(await InspectStoreAsync(store.Key, store.Value));
        if (sourceStores.Any(store => !store.Readable))
            discrepancies.Add("One or more required SQLite source stores are unavailable.");
        await ValidateSourceIntegrityAsync(discrepancies, integrityFindings, repairPlan);
        await ValidateStatisticsIntegrityAsync(discrepancies, integrityFindings, repairPlan);

        await using var connection = new MySqlConnection(_options.DestinationConnectionString);
        try
        {
            await connection.OpenAsync();
            destination = await ReadDestinationInfoAsync(connection);
            await ValidateDestinationSchemaAsync(connection, discrepancies);
            if (mutate && discrepancies.Count == 0)
            {
                await using var transaction = await connection.BeginTransactionAsync();
                try
                {
                    foreach (var mapping in _mappings)
                    {
                        if (mapping.Classification == "Excluded")
                        {
                            var sourceCount = await CountSourceRowsAsync(mapping);
                            exclusions.Add($"{mapping.SourceStore}.{mapping.SourceTable}: {sourceCount} rows; {mapping.Transformation}");
                            mappings.Add(new(mapping.SourceStore, mapping.SourceTable, mapping.TargetTable, mapping.Classification, mapping.Transformation, sourceCount, 0, null, 0, null, null, "EXCLUDED", mapping.Transformation));
                            continue;
                        }

                        var result = await MigrateMappingAsync(connection, transaction, mapping, conflicts);
                        mappings.Add(result);
                        if (result.Status != "PASS")
                            throw new InvalidOperationException(result.Detail ?? $"Migration failed for {mapping.SourceTable}.");
                    }
                    await transaction.CommitAsync();
                    destinationMutated = true;
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            else
            {
                foreach (var mapping in _mappings)
                {
                    var sourceCount = await CountSourceRowsAsync(mapping);
                    if (mapping.Classification == "Excluded")
                        exclusions.Add($"{mapping.SourceStore}.{mapping.SourceTable}: {sourceCount} rows; {mapping.Transformation}");
                    var destinationCount = verify ? (long?)await CountDestinationRowsAsync(connection, mapping.TargetTable) : null;
                    var status = mapping.Classification == "Excluded" ? "EXCLUDED" : discrepancies.Count == 0 ? (verify ? "PASS" : "PLANNED") : "BLOCKED";
                    mappings.Add(new(mapping.SourceStore, mapping.SourceTable, mapping.TargetTable, mapping.Classification, mapping.Transformation, sourceCount, mapping.Classification == "Excluded" ? 0 : sourceCount, destinationCount, 0, null, null, status, mapping.Transformation));
                }
            }

            if (verify && discrepancies.Count == 0)
                await VerifyMappingsAsync(connection, mappings, discrepancies);
        }
        catch (Exception exception)
        {
            discrepancies.Add(Sanitize(exception.Message));
        }

        var succeeded = discrepancies.Count == 0 && conflicts.Count == 0 && sourceStores.All(store => store.Readable) && (!mutate || destinationMutated);
        return new(
            operation,
            started,
            DateTimeOffset.UtcNow,
            "mariadb",
            destination?.ServerVersion,
            destination?.ServerComment,
            destination?.Collation,
            null,
            sourceStores,
            mappings,
            conflicts,
            unsupported,
            exclusions,
            discrepancies,
            _mappings.Where(mapping => mapping.Classification != "Excluded").Select(mapping => $"{mapping.SourceStore}.{mapping.SourceTable} -> {mapping.TargetTable}").ToArray(),
            mappings.Sum(mapping => mapping.BinaryBytes),
            destinationMutated,
            succeeded,
            integrityFindings,
            repairPlan);
    }

    private async Task<MappingReport> MigrateMappingAsync(MySqlConnection connection, MySqlTransaction transaction, MigrationMapping mapping, List<string> conflicts)
    {
        if (mapping.Classification == "Unsupported")
        {
            conflicts.Add($"Unsupported mapping {mapping.SourceStore}.{mapping.SourceTable}: {mapping.Transformation}");
            return new(mapping.SourceStore, mapping.SourceTable, mapping.TargetTable, mapping.Classification, mapping.Transformation, 0, 0, null, 0, null, null, "FAIL", mapping.Transformation);
        }

        await using var source = OpenSource(mapping.SourceStore);
        await using var command = source.CreateCommand();
        var columns = mapping.Columns ?? await GetSourceColumnsAsync(source, mapping.SourceTable);
        command.CommandText = mapping.SelectSql ?? $"SELECT {string.Join(", ", columns.Select(QuoteIdentifier))} FROM {QuoteIdentifier(mapping.SourceTable)} ORDER BY {string.Join(", ", mapping.PrimaryKey.Select(QuoteIdentifier))};";
        await using var reader = await command.ExecuteReaderAsync();
        var targetColumns = columns;
        var insert = $"INSERT INTO {QuoteIdentifier(mapping.TargetTable)} ({string.Join(", ", targetColumns.Select(QuoteIdentifier))}) VALUES ({string.Join(", ", targetColumns.Select((_, index) => $"@p{index}"))});";
        long count = 0;
        long binaryBytes = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (await reader.ReadAsync())
        {
            var values = ReadValues(reader, columns, mapping);
            var parameters = new DynamicParameters();
            for (var index = 0; index < values.Length; index++)
            {
                parameters.Add($"p{index}", values[index]);
                if (values[index] is byte[] bytes)
                {
                    binaryBytes += bytes.LongLength;
                    hash.AppendData(bytes);
                }
            }
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(insert, parameters, transaction, cancellationToken: default));
            }
            catch (MySqlException exception) when (exception.Number == 1062)
            {
                var key = string.Join(", ", mapping.PrimaryKey.Select(keyColumn => $"{keyColumn}={values[Array.IndexOf(columns.ToArray(), keyColumn)]}"));
                conflicts.Add($"{mapping.TargetTable} collision at {key}; no overwrite was attempted.");
                return new(mapping.SourceStore, mapping.SourceTable, mapping.TargetTable, mapping.Classification, mapping.Transformation, count + 1, count, null, binaryBytes, null, null, "FAIL", conflicts[^1]);
            }
            count++;
        }
        return new(mapping.SourceStore, mapping.SourceTable, mapping.TargetTable, mapping.Classification, mapping.Transformation, count, count, count, binaryBytes, binaryBytes == 0 ? null : Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), null, "PASS", null);
    }

    private async Task VerifyMappingsAsync(MySqlConnection connection, IReadOnlyList<MappingReport> reports, List<string> discrepancies)
    {
        var expectedByTarget = reports.Where(report => report.Classification != "Excluded").GroupBy(report => report.TargetTable, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Sum(report => report.SourceCount), StringComparer.OrdinalIgnoreCase);
        foreach (var item in expectedByTarget)
        {
            var actual = await CountDestinationRowsAsync(connection, item.Key);
            if (actual != item.Value)
                discrepancies.Add($"{item.Key}: expected {item.Value} rows from source mappings, found {actual}.");
        }

        var destinationUsers = (await connection.QueryAsync<(string Id, string? PasswordHash, string? SecurityStamp)>("SELECT Id, PasswordHash, SecurityStamp FROM AspNetUsers ORDER BY Id;")).ToList();
        var sourceUsers = await ReadSourceIdentityFingerprintsAsync();
        if (destinationUsers.Count != sourceUsers.Count || destinationUsers.Any(user => !sourceUsers.TryGetValue(user.Id, out var expected) || expected != (user.PasswordHash, user.SecurityStamp)))
            discrepancies.Add("AspNetUsers password hashes or security stamps do not reconcile.");

        var sourceAttachmentHashes = await ReadAttachmentHashesAsync(OpenSource("pages"));
        var destinationAttachmentHashes = (await connection.QueryAsync<(int PageFileId, int Revision, byte[] Data)>("SELECT PageFileId, Revision, Data FROM PageFileRevision ORDER BY PageFileId, Revision;")).Select(row => (row.PageFileId, row.Revision, Hash(row.Data))).ToList();
        if (!sourceAttachmentHashes.SequenceEqual(destinationAttachmentHashes))
            discrepancies.Add("PageFileRevision binary SHA-256 hashes do not reconcile.");
    }

    private async Task<Dictionary<string, (string? PasswordHash, string? SecurityStamp)>> ReadSourceIdentityFingerprintsAsync()
    {
        await using var source = OpenSource("users");
        return (await ReadRowsAsync(source, "SELECT Id, PasswordHash, SecurityStamp FROM AspNetUsers ORDER BY Id;"))
            .ToDictionary(row => Convert.ToString(row["Id"], CultureInfo.InvariantCulture)!, row => (row["PasswordHash"] as string, row["SecurityStamp"] as string), StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<(int PageFileId, int Revision, string Hash)>> ReadAttachmentHashesAsync(SqliteConnection source)
    {
        var rows = await ReadRowsAsync(source, "SELECT PageFileId, Revision, Data FROM PageFileRevision ORDER BY PageFileId, Revision;");
        return rows.Select(row =>
        {
            if (row["Data"] is not byte[] bytes) throw new InvalidDataException("PageFileRevision.Data is not a binary SQLite value.");
            return (Convert.ToInt32(row["PageFileId"], CultureInfo.InvariantCulture), Convert.ToInt32(row["Revision"], CultureInfo.InvariantCulture), Hash(bytes));
        }).ToArray();
    }

    private async Task<SourceStoreReport> InspectStoreAsync(string name, string path)
    {
        if (!File.Exists(path)) return new(name, path, true, false, new Dictionary<string, long>(), "File does not exist.");
        try
        {
            await using var connection = OpenSource(name);
            var tables = await ReadTableNamesAsync(connection);
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables)
                counts[table] = await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {QuoteIdentifier(table)};");
            return new(name, path, true, true, counts, null);
        }
        catch (Exception exception)
        {
            return new(name, path, true, false, new Dictionary<string, long>(), Sanitize(exception.Message));
        }
    }

    private async Task ValidateDestinationSchemaAsync(MySqlConnection connection, List<string> discrepancies)
    {
        var required = _mappings.Where(mapping => mapping.Classification != "Excluded").Select(mapping => mapping.TargetTable).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tables = (await connection.QueryAsync<string>("SELECT TABLE_NAME FROM information_schema.tables WHERE TABLE_SCHEMA = DATABASE();")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var table in required)
            if (!tables.Contains(table)) discrepancies.Add($"Destination table '{table}' is missing; initialize the MariaDB schema before migration.");
        var provider = await connection.ExecuteScalarAsync<string>("SELECT VERSION();");
        var comment = await connection.ExecuteScalarAsync<string>("SELECT @@version_comment;");
        if (!(provider ?? string.Empty).Contains("mariadb", StringComparison.OrdinalIgnoreCase) && !(comment ?? string.Empty).Contains("maria", StringComparison.OrdinalIgnoreCase))
            discrepancies.Add($"Destination is not identified as MariaDB (version={provider}, comment={comment}).");
    }

    private static async Task<DestinationInfo> ReadDestinationInfoAsync(MySqlConnection connection)
        => new(await connection.ExecuteScalarAsync<string>("SELECT VERSION();") ?? "unknown", await connection.ExecuteScalarAsync<string>("SELECT @@version_comment;") ?? "unknown", await connection.ExecuteScalarAsync<string>("SELECT @@collation_database;") ?? "unknown");

    private async Task<long> CountSourceRowsAsync(MigrationMapping mapping)
    {
        await using var source = OpenSource(mapping.SourceStore);
        return await source.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {QuoteIdentifier(mapping.SourceTable)};");
    }

    private static Task<long> CountDestinationRowsAsync(MySqlConnection connection, string table)
        => connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {QuoteIdentifier(table)};");

    private async Task LoadPageNavigationIndexAsync()
    {
        foreach (var store in new[] { "pages", "deleted-pages" })
        {
            await using var source = OpenSource(store);
            foreach (var row in await ReadRowsAsync(source, "SELECT Id, Navigation FROM Page;"))
                _pageNavigations[Convert.ToInt32(row["Id"], CultureInfo.InvariantCulture)] = Convert.ToString(row["Navigation"], CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private SqliteConnection OpenSource(string store)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _sourcePaths[store],
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private async Task<string[]> ReadTableNamesAsync(SqliteConnection connection)
        => (await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;")).ToArray();

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadRowsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<string[]> GetSourceColumnsAsync(SqliteConnection connection, string table)
        => (await connection.QueryAsync<string>($"SELECT name FROM pragma_table_info({QuoteLiteral(table)}) ORDER BY cid;")).ToArray();

    private object?[] ReadValues(SqliteDataReader reader, IReadOnlyList<string> columns, MigrationMapping mapping)
    {
        var values = new object?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            if (mapping.TargetTable == "DeletedPageRevision" && columns[i].Equals("Navigation", StringComparison.OrdinalIgnoreCase))
            {
                var pageIdIndex = Array.IndexOf(columns.ToArray(), "PageId");
                var pageId = pageIdIndex >= 0 && !reader.IsDBNull(pageIdIndex) ? Convert.ToInt32(reader.GetValue(pageIdIndex), CultureInfo.InvariantCulture) : 0;
                value = _pageNavigations.GetValueOrDefault(pageId, string.Empty);
            }
            values[i] = NormalizeValue(value, columns[i], mapping);
        }
        return values;
    }

    private object? NormalizeValue(object? value, string column, MigrationMapping mapping)
    {
        if (value is null || value is DBNull) return null;
        if (column is "UserId" or "CreatedByUserId" or "ModifiedByUserId" or "DeletedByUserId" || (column == "Id" && mapping.TargetTable is "AspNetUsers" or "AspNetRoles"))
        {
            var identifier = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (Guid.TryParse(identifier, out var guid)) return guid.ToString("D");
        }
        if (column is "Navigation" or "ActiveNavigation" or "ReferencesPageNavigation")
        {
            var navigation = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return mapping.TargetTable == "PageFile" ? CleanNavigation(navigation) : CleanNamespaceNavigation(navigation);
        }
        if (column.EndsWith("Date", StringComparison.OrdinalIgnoreCase) || column.Contains("DateTime", StringComparison.OrdinalIgnoreCase))
        {
            if (value is DateTime dateTime) return dateTime;
            if (DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return parsed;
        }
        if (mapping.TargetTable == "Page" && column == "ActiveNavigation" && mapping.SourceTable == "Page") return value;
        if (mapping.TargetTable == "Page" && column == "IsDeleted") return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        return value;
    }

    private IReadOnlyList<MigrationMapping> BuildMappings()
    {
        var mappings = new List<MigrationMapping>();
        void Direct(string store, string source, string target, string[] columns, string[] key, string? transform = null, string? select = null, string classification = "Direct")
            => mappings.Add(new(store, source, target, columns, key, classification, transform, select));

        Direct("users", "AspNetUsers", "AspNetUsers", ["Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "PhoneNumber", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnd", "LockoutEnabled", "AccessFailedCount"], ["Id"]);
        Direct("users", "AspNetRoles", "AspNetRoles", ["Id", "Name", "NormalizedName", "ConcurrencyStamp"], ["Id"]);
        Direct("users", "AspNetUserClaims", "AspNetUserClaims", ["Id", "UserId", "ClaimType", "ClaimValue"], ["Id"]);
        Direct("users", "AspNetRoleClaims", "AspNetRoleClaims", ["Id", "RoleId", "ClaimType", "ClaimValue"], ["Id"]);
        Direct("users", "AspNetUserLogins", "AspNetUserLogins", ["LoginProvider", "ProviderKey", "ProviderDisplayName", "UserId"], ["LoginProvider", "ProviderKey"]);
        Direct("users", "AspNetUserRoles", "AspNetUserRoles", ["UserId", "RoleId"], ["UserId", "RoleId"]);
        Direct("users", "AspNetUserTokens", "AspNetUserTokens", ["UserId", "LoginProvider", "Name", "Value"], ["UserId", "LoginProvider", "Name"]);
        Direct("users", "Profile", "Profile", ["UserId", "AccountName", "Navigation", "Biography", "Avatar", "CreatedDate", "ModifiedDate", "AvatarContentType"], ["UserId"]);
        Direct("users", "PermissionDisposition", "PermissionDisposition", ["Id", "Name"], ["Id"]);
        Direct("users", "Permission", "Permission", ["Id", "Name", "Description"], ["Id"]);
        Direct("users", "Role", "Role", ["Id", "Name", "Description", "IsBuiltIn"], ["Id"]);
        Direct("users", "AccountRole", "AccountRole", ["Id", "UserId", "RoleId"], ["Id"]);
        Direct("users", "AccountPermission", "AccountPermission", ["Id", "UserId", "PermissionId", "Namespace", "PageId", "PermissionDispositionId"], ["Id"]);
        Direct("users", "RolePermission", "RolePermission", ["Id", "RoleId", "PermissionId", "Namespace", "PageId", "PermissionDispositionId"], ["Id"]);
        Direct("users", "AdminPwCheck", "AdminPwCheck", ["Id", "IsChanged"], ["Id"], "SQLite Value is transformed to MariaDB Id=1, IsChanged=Value=1.", "SELECT 1 AS Id, (Value = 1) AS IsChanged FROM AdminPwCheck;");

        Direct("config", "DataType", "DataType", ["Id", "Name"], ["Id"]);
        Direct("config", "ConfigurationGroup", "ConfigurationGroup", ["Id", "Name", "Description"], ["Id"]);
        Direct("config", "ConfigurationEntry", "ConfigurationEntry", ["Id", "ConfigurationGroupId", "Name", "Value", "DataTypeId", "Description", "IsEncrypted", "IsRequired"], ["Id"]);
        Direct("config", "MenuItem", "MenuItem", ["Id", "Name", "Link", "Ordinal"], ["Id"]);
        Direct("config", "Theme", "Theme", ["Name", "DelimitedFiles", "ClassNavBar", "ClassNavLink", "ClassDropdown", "ClassBranding", "EditorTheme"], ["Name"]);
        Direct("config", "CryptoCheck", "CryptoCheck", ["Content"], ["Content"]);
        Direct("config", "VersionState", "VersionState", ["Id", "Name", "Value"], ["Id"]);

        AddPageMappings(mappings, "pages", false);
        AddPageMappings(mappings, "deleted-pages", true);
        Direct("deleted-revisions", "PageRevision", "DeletedPageRevision", ["PageId", "Revision", "Name", "Namespace", "Navigation", "Description", "Body", "DataHash", "ChangeSummary", "ModifiedByUserId", "ModifiedDate", "DeletedByUserId", "DeletedDate"], ["PageId", "Revision"], "Navigation is resolved from the read-only pages/deletedpages Page stores; deletion metadata is joined from DeletionMeta.", "SELECT R.PageId, R.Revision, R.Name, R.Namespace, '' AS Navigation, R.Description, R.Body, R.DataHash, COALESCE(R.ChangeSummary, '') AS ChangeSummary, R.ModifiedByUserId, R.ModifiedDate, M.DeletedByUserId, M.DeletedDate FROM PageRevision R JOIN DeletionMeta M ON M.PageId = R.PageId AND M.Revision = R.Revision;");
        Direct("deleted-revisions", "PageRevisionAttachment", "DeletedPageRevisionAttachment", ["PageId", "PageFileId", "FileRevision", "PageRevision"], ["PageId", "PageFileId", "FileRevision", "PageRevision"]);

        Direct("logging", "Severity", "Severity", ["Id", "Name"], ["Id"]);
        Direct("logging", "Log", "Log", ["Id", "SeverityId", "Text", "ExceptionText", "StackTrace", "CreatedDate"], ["Id"]);
        Direct("statistics", "PageStatistics", "PageStatistics", ["PageId", "LastCompileDateTime", "TotalCompilationCount", "TotalViewCount", "LastWikifyTimeMs", "TotalWikifyTimeMs", "LastMatchCount", "LastErrorCount", "LastOutgoingLinkCount", "LastTagCount", "LastProcessedBodySize", "LastBodySize"], ["PageId"], "SQLite Id is intentionally omitted; MariaDB keys statistics by PageId.");
        Direct("emoji", "Emoji", "Emoji", ["Id", "Name", "Shortcut", "ImageData", "MimeType"], ["Id"], "SQLite has no Shortcut column; the stable source Name is used as the shortcut.", "SELECT Id, Name, Name AS Shortcut, ImageData, COALESCE(MimeType, 'application/octet-stream') AS MimeType FROM Emoji;");
        Direct("emoji", "EmojiCategory", "EmojiCategory", ["EmojiId", "Category"], ["EmojiId", "Category"]);
        Direct("translations", "Translation", "Translation", null!, ["English"], "Read-only shipped translation artifact is copied column-for-column.", "SELECT * FROM Translation;");

        Direct("pages", "FeatureTemplate", "DefaultFeatureTemplates", ["Name", "Type", "PageName", "Description", "TemplateText"], ["Name", "Type"], "User feature templates are represented in the target's versioned default-template store; PageId is resolved to the source page Navigation.", "SELECT F.Name, COALESCE(F.Type, ''), P.Navigation, F.Description, F.TemplateText FROM FeatureTemplate F LEFT JOIN Page P ON P.Id = F.PageId;");
        mappings.Add(new("defaults", "DefaultConfiguration", "DefaultConfiguration", null, ["ConfigurationGroupName", "ConfigurationEntryName"], "Excluded", "Canonical application defaults are immutable shipped seed input and are recomputed by MySqlDefaultContentSeeder, not imported as user data.", null));
        mappings.Add(new("defaults", "DefaultThemes", "DefaultThemes", null, ["Name"], "Excluded", "Canonical application defaults are immutable shipped seed input and are recomputed by MySqlDefaultContentSeeder, not imported as user data.", null));
        mappings.Add(new("defaults", "DefaultFeatureTemplates", "DefaultFeatureTemplates", null, ["Name", "Type"], "Excluded", "Canonical application defaults are immutable shipped seed input and are recomputed by MySqlDefaultContentSeeder, not imported as user data.", null));
        mappings.Add(new("defaults", "DefaultWikiPages", "DefaultWikiPages", null, ["Navigation"], "Excluded", "Canonical application defaults are immutable shipped seed input and are recomputed by MySqlDefaultContentSeeder, not imported as user data.", null));
        return mappings;
    }

    private static void AddPageMappings(List<MigrationMapping> mappings, string store, bool deleted)
    {
        mappings.Add(new(store, "Page", "Page", ["Id", "Name", "Namespace", "Navigation", "ActiveNavigation", "Description", "Revision", "CreatedByUserId", "CreatedDate", "ModifiedByUserId", "ModifiedDate", "IsDeleted", "DeletedByUserId", "DeletedDate"], ["Id"], "Direct", deleted ? "Deleted-page rows are preserved in the unified Page table with IsDeleted=true and ActiveNavigation=NULL." : "ActiveNavigation is copied from Navigation; deletion fields are empty.", deleted ? "SELECT P.Id, P.Name, P.Namespace, P.Navigation, NULL, P.Description, P.Revision, P.CreatedByUserId, P.CreatedDate, P.ModifiedByUserId, P.ModifiedDate, TRUE, M.DeletedByUserId, M.DeletedDate FROM Page P LEFT JOIN DeletionMeta M ON M.PageId = P.Id;" : "SELECT Id, Name, Namespace, Navigation, Navigation, Description, Revision, CreatedByUserId, CreatedDate, ModifiedByUserId, ModifiedDate, FALSE, NULL, NULL FROM Page;"));
        mappings.Add(new(store, "PageRevision", "PageRevision", ["PageId", "Revision", "Name", "Namespace", "Navigation", "Description", "Body", "DataHash", "ChangeSummary", "ModifiedByUserId", "ModifiedDate"], ["PageId", "Revision"], "Direct", "Navigation is derived from the owning source Page; nullable ChangeSummary becomes the target empty string.", "SELECT R.PageId, R.Revision, R.Name, R.Namespace, P.Navigation, R.Description, R.Body, R.DataHash, COALESCE(R.ChangeSummary, ''), R.ModifiedByUserId, R.ModifiedDate FROM PageRevision R JOIN Page P ON P.Id = R.PageId;"));
        mappings.Add(new(store, "PageFile", "PageFile", ["Id", "PageId", "Name", "Navigation", "Revision", "CreatedDate"], ["Id"], "Direct"));
        mappings.Add(new(store, "PageFileRevision", "PageFileRevision", ["PageFileId", "ContentType", "Size", "CreatedByUserId", "CreatedDate", "Data", "Revision", "DataHash"], ["PageFileId", "Revision"], "Direct"));
        mappings.Add(new(store, "PageRevisionAttachment", "PageRevisionAttachment", ["PageId", "PageFileId", "FileRevision", "PageRevision"], ["PageId", "PageFileId", "FileRevision", "PageRevision"], "Direct"));
        mappings.Add(new(store, "PageComment", "PageComment", ["Id", "PageId", "CreatedDate", "UserId", "Body"], ["Id"], "Direct"));
        mappings.Add(new(store, "PageTag", "PageTag", ["PageId", "Tag"], ["PageId", "Tag"], "Direct"));
        mappings.Add(new(store, "PageProcessingInstruction", "PageProcessingInstruction", ["PageId", "Instruction"], ["PageId", "Instruction"], "Direct"));
        mappings.Add(new(store, "PageToken", "PageToken", ["PageId", "Token", "Weight", "DoubleMetaphone"], ["PageId", "Token"], "Direct"));
        if (!deleted)
        {
            mappings.Add(new(store, "PageReference", "PageReference", ["PageId", "ReferencesPageName", "ReferencesPageNavigation", "ReferencesPageId"], ["PageId", "ReferencesPageNavigation"], "Direct"));
            mappings.Add(new(store, "CurrentPageEditors", "CurrentPageEditors", ["PageId", "UserId", "AccountName", "UTCDate"], ["PageId", "UserId"], "Direct"));
        }
    }

    private async Task ValidateSourceTablesAsync()
    {
        foreach (var group in _mappings.GroupBy(mapping => mapping.SourceStore, StringComparer.OrdinalIgnoreCase))
        {
            await using var source = OpenSource(group.Key);
            var tables = (await ReadTableNamesAsync(source)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in group)
                if (!tables.Contains(mapping.SourceTable)) throw new InvalidOperationException($"SQLite source store '{group.Key}' is missing required table '{mapping.SourceTable}'.");
        }
    }

    private async Task ValidateSourceIntegrityAsync(List<string> discrepancies, List<IntegrityFinding> findings, List<RepairPlanEntry> repairPlan)
    {
        foreach (var store in new[] { "config", "users", "pages", "deleted-pages", "deleted-revisions", "logging", "statistics", "emoji", "translations", "defaults" })
        {
            if (!File.Exists(_sourcePaths[store])) continue;
            await using var source = OpenSource(store);
            var foreignKeys = await ReadRowsAsync(source, "PRAGMA foreign_key_check;");
            foreach (var foreignKey in foreignKeys)
            {
                var table = Convert.ToString(foreignKey.GetValueOrDefault("table"), CultureInfo.InvariantCulture) ?? "unknown";
                var rowId = Convert.ToString(foreignKey.GetValueOrDefault("rowid"), CultureInfo.InvariantCulture) ?? "unknown";
                var parent = Convert.ToString(foreignKey.GetValueOrDefault("parent"), CultureInfo.InvariantCulture) ?? "unknown";
                var detail = $"SQLite foreign-key check reports row {rowId} in {table} without a valid parent {parent}.";
                discrepancies.Add($"{store}: {detail}");
                findings.Add(new(store, table, rowId, $"foreign key -> {parent}", "ReferentialIntegrity", "Blocking", "Blocking", detail, "Do not repair automatically; identify the authoritative source record and obtain operator approval.", true));
            }
        }

        var orphanChecks = new[]
        {
            ("pages", "PageFile", "PageId", "Page", "Id", "C.PageId", "PageFile->Page"),
            ("pages", "PageRevision", "PageId", "Page", "Id", "C.PageId || ':' || C.Revision", "PageRevision->Page"),
            ("pages", "PageComment", "PageId", "Page", "Id", "C.Id", "PageComment->Page"),
            ("pages", "PageTag", "PageId", "Page", "Id", "C.PageId || ':' || C.Tag", "PageTag->Page"),
            ("pages", "PageToken", "PageId", "Page", "Id", "C.PageId || ':' || C.Token", "PageToken->Page"),
            ("pages", "PageProcessingInstruction", "PageId", "Page", "Id", "C.PageId || ':' || C.Instruction", "PageProcessingInstruction->Page"),
            ("deleted-pages", "PageFile", "PageId", "Page", "Id", "C.PageId", "PageFile->Page"),
            ("deleted-pages", "PageRevision", "PageId", "Page", "Id", "C.PageId || ':' || C.Revision", "PageRevision->Page"),
            ("deleted-pages", "PageComment", "PageId", "Page", "Id", "C.Id", "PageComment->Page"),
            ("deleted-pages", "PageTag", "PageId", "Page", "Id", "C.PageId || ':' || C.Tag", "PageTag->Page"),
            ("deleted-pages", "PageToken", "PageId", "Page", "Id", "C.PageId || ':' || C.Token", "PageToken->Page"),
            ("deleted-pages", "PageProcessingInstruction", "PageId", "Page", "Id", "C.PageId || ':' || C.Instruction", "PageProcessingInstruction->Page")
        };
        foreach (var check in orphanChecks)
            await ValidateOrphanRowsAsync(check.Item1, check.Item2, check.Item3, check.Item4, check.Item5, check.Item6, check.Item7, discrepancies, findings, repairPlan);
    }

    private async Task ValidateOrphanRowsAsync(string store, string childTable, string childColumn, string parentTable, string parentColumn, string recordExpression, string relationship, List<string> discrepancies, List<IntegrityFinding> findings, List<RepairPlanEntry> repairPlan)
    {
        await using var source = OpenSource(store);
        IReadOnlyList<Dictionary<string, object?>> rows;
        try
        {
            rows = await ReadRowsAsync(source, $"SELECT {recordExpression} AS RecordKey FROM {QuoteIdentifier(childTable)} C LEFT JOIN {QuoteIdentifier(parentTable)} P ON P.{QuoteIdentifier(parentColumn)} = C.{QuoteIdentifier(childColumn)} WHERE P.{QuoteIdentifier(parentColumn)} IS NULL ORDER BY RecordKey;");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Integrity query {store}.{childTable}->{parentTable} failed: {exception.Message}", exception);
        }
        foreach (var row in rows)
        {
            var recordKey = Convert.ToString(row["RecordKey"], CultureInfo.InvariantCulture) ?? "unknown";
            var derivedMetadata = childTable is "PageToken" or "PageProcessingInstruction" && store == "deleted-pages";
            var classification = derivedMetadata ? "ExplicitlyExcludable" : "Blocking";
            var proposedAction = derivedMetadata
                ? "On an isolated copy only, exclude this orphaned derived-metadata row from migration after operator approval; never modify the original SQLite source."
                : "Do not repair automatically; identify the authoritative parent record and obtain operator approval."
                ;
            var detail = $"{store}.{childTable} record {recordKey} references missing {parentTable}.{parentColumn}.";
            discrepancies.Add($"{detail} Migration is blocked rather than inventing a replacement.");
            findings.Add(new(store, childTable, recordKey, relationship, "OrphanedRecord", classification, "BlockingUntilApproved", detail, proposedAction, true));
            if (derivedMetadata)
                repairPlan.Add(new(store, childTable, recordKey, detail, "Exclude only this orphaned derived-metadata row from the repaired-copy input.", "The parent page is absent from both live and deleted page stores; no authoritative relationship can be recovered.", "The row is omitted from the target; no page content or identity relationship is changed.", "The original remains byte-preserved; the isolated copy can be recreated from its pre-repair checksum.", "Loss is limited to stale derived metadata, but deletion can mask user-visible metadata if the parent is later recovered.", true));
        }
    }

    private async Task ValidateStatisticsIntegrityAsync(List<string> discrepancies, List<IntegrityFinding> findings, List<RepairPlanEntry> repairPlan)
    {
        await using var source = OpenSource("statistics");
        var rows = await ReadRowsAsync(source, "SELECT PageId FROM PageStatistics;");
        foreach (var row in rows.Where(row => !_pageNavigations.ContainsKey(Convert.ToInt32(row["PageId"], CultureInfo.InvariantCulture))))
        {
            var recordKey = Convert.ToString(row["PageId"], CultureInfo.InvariantCulture) ?? "unknown";
            var detail = $"statistics.PageStatistics record PageId={recordKey} references no live or deleted source page.";
            discrepancies.Add($"{detail} Recomputed metadata cannot be attached safely.");
            findings.Add(new("statistics", "PageStatistics", recordKey, "PageStatistics->Page", "OrphanedRecord", "ExplicitlyExcludable", "BlockingUntilApproved", detail, "On an isolated copy only, exclude this stale recomputed-metadata row after operator approval; never modify the original SQLite source.", true));
            repairPlan.Add(new("statistics", "PageStatistics", recordKey, detail, "Exclude only this orphaned recomputed-metadata row from the repaired-copy input.", "No live or deleted source page has the referenced identifier; statistics are derived and can be recomputed after migration.", "The row is omitted from the target; statistics can be regenerated after migration.", "The original remains byte-preserved; the isolated copy can be recreated from its pre-repair checksum.", "Loss is limited to a stale counter row; retaining it would create a foreign-key failure or attach metrics to the wrong page.", true));
        }
    }

    private void ValidateOptions()
    {
        if (_options.DestinationConnectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) || _options.DestinationConnectionString.Contains("Filename=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Destination must be a MariaDB connection string; SQLite destinations are rejected.");
        if (string.Equals(_options.SourceRoot, "/", StringComparison.Ordinal) || string.Equals(_options.SourceRoot, Environment.CurrentDirectory, StringComparison.Ordinal))
            throw new ArgumentException("Source root is too broad; pass the explicit TightWiki Data directory.");
    }

    private async Task<string> ComputeSourceRootHashAsync()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in _sourcePaths.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase).Select(value => value.Value))
        {
            await using var stream = File.OpenRead(path);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string QuoteIdentifier(string value) => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";
    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Sanitize(string message) => message.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Replace("Password=", "Password=<redacted>", StringComparison.OrdinalIgnoreCase);

    private static string CleanNamespaceNavigation(string value)
    {
        var parts = value.Split("::", StringSplitOptions.None);
        return parts.Length == 2 ? $"{CleanNavigation(parts[0])}::{CleanNavigation(parts[1])}" : CleanNavigation(value);
    }

    private static string CleanNavigation(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Trim().Trim(':').Replace('\\', '/'))
        {
            if (char.IsWhiteSpace(character) || character == '.') builder.Append('_');
            else if (char.IsLetterOrDigit(character) || character is '_' or '/' or '-') builder.Append(character);
        }
        return Regex.Replace(Regex.Replace(builder.ToString(), "_{2,}", "_"), "/{2,}", "/").Trim('/').ToLowerInvariant();
    }
}
