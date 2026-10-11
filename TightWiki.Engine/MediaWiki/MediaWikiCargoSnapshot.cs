using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Data;
using TightWiki.Library.Database;

namespace TightWiki.Engine.MediaWiki;

public sealed record MediaWikiCargoSnapshotCell(string Name, string? Value);

public sealed record MediaWikiCargoSnapshotTable(
    string LogicalTable,
    string PhysicalTable,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<MediaWikiCargoSnapshotCell>> Rows,
    IReadOnlyList<string> HelperRelationships);

public sealed record MediaWikiCargoSnapshotAcquisitionTable(
    string LogicalTable,
    string PhysicalTable,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> HelperRelationships);

public sealed record MediaWikiCargoSnapshotAcquisitionRequest(
    string Destination,
    string SnapshotId,
    string SourceIdentity,
    string SourceWikiIdentity,
    string CargoTablePrefix,
    string SchemaColumnsSha256,
    string SchemaIndexesSha256,
    IReadOnlyList<MediaWikiCargoSnapshotAcquisitionTable> Tables,
    string DatasetClassification = "CURRENT_STATE_UNCORRELATED",
    int MaxRowsPerTable = 100_000,
    long MaxOutputBytes = 256_000_000,
    TimeSpan? MaxExecutionTime = null);

public sealed record MediaWikiCargoSnapshotTableManifest(
    string LogicalTable,
    string PhysicalTable,
    long ExactRowCount,
    string ContentSha256,
    string RowIntegritySha256,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> HelperRelationships);

public sealed record MediaWikiCargoSnapshotManifest(
    string FormatVersion,
    string SnapshotId,
    DateTimeOffset AcquiredAtUtc,
    string SourceIdentity,
    string SourceWikiIdentity,
    string CargoTablePrefix,
    string SchemaColumnsSha256,
    string SchemaIndexesSha256,
    IReadOnlyList<MediaWikiCargoSnapshotTableManifest> Tables,
    string ConsistencyMethod,
    string DatasetClassification,
    string CanonicalizationVersion,
    string AcquisitionToolVersion,
    bool Complete,
    bool HistoricalCorrelationVerified);

public sealed record MediaWikiCargoSnapshotValidationResult(
    bool IsValid,
    IReadOnlyList<string> ValidationFailures,
    bool SourceIdentityVerified,
    bool SchemaVerified,
    bool ContentIntegrityVerified,
    bool ConsistencyVerified,
    bool HistoricalCorrelationVerified);

public static class MediaWikiCargoSnapshotStore
{
    public const string FormatVersion = "cw-m3-006f4b-cargo-snapshot-v1";
    public const string CanonicalizationVersion = "utf8-json-lines-field-order-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static async Task CaptureAsync(
        IDatabaseConnectionFactory connectionFactory,
        MediaWikiCargoSnapshotAcquisitionRequest request,
        string acquisitionToolVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        if (request.Tables.Count == 0 || request.MaxRowsPerTable <= 0 || request.MaxOutputBytes <= 0)
            throw new ArgumentException("A bounded, non-empty Cargo table scope is required.", nameof(request));
        var started = DateTime.UtcNow;
        await using var session = await connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var tables = new List<MediaWikiCargoSnapshotTable>();
        foreach (var specification in request.Tables)
        {
            ValidateIdentifier(specification.LogicalTable);
            ValidateIdentifier(specification.PhysicalTable);
            if (specification.Columns.Count == 0 || specification.Columns.Any(value => !IsIdentifier(value)))
                throw new InvalidDataException($"The column allowlist for {specification.LogicalTable} is invalid.");
            if (!specification.Columns.Contains("_ID", StringComparer.Ordinal))
                throw new InvalidDataException($"The table {specification.LogicalTable} must have the verified _ID ordering key.");
            var command = session.Connection.CreateCommand();
            await using (command)
            {
                command.Transaction = transaction;
                command.CommandText = $"SELECT {string.Join(", ", specification.Columns.Select(value => $"`{value}`"))} FROM `{specification.PhysicalTable}` ORDER BY `_ID` ASC";
                if (request.MaxExecutionTime is { } timeout) command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                var rows = new List<IReadOnlyList<MediaWikiCargoSnapshotCell>>();
                long estimatedBytes = 0;
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (rows.Count >= request.MaxRowsPerTable) throw new InvalidDataException($"Table {specification.LogicalTable} exceeded the row budget.");
                    var row = new List<MediaWikiCargoSnapshotCell>(specification.Columns.Count);
                    for (var i = 0; i < specification.Columns.Count; i++)
                    {
                        var value = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                        estimatedBytes += (value?.Length ?? 0) + specification.Columns[i].Length + 8;
                        if (estimatedBytes > request.MaxOutputBytes) throw new InvalidDataException("Cargo snapshot exceeded the output byte budget.");
                        row.Add(new(specification.Columns[i], value));
                    }
                    rows.Add(row);
                    if (request.MaxExecutionTime is { } max && DateTime.UtcNow - started > max) throw new TimeoutException("Cargo snapshot exceeded the execution-time budget.");
                }
                tables.Add(new(specification.LogicalTable, specification.PhysicalTable, specification.Columns, rows, specification.HelperRelationships));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        var tableManifests = tables.Select(table =>
        {
            var bytes = CanonicalTableBytes(table);
            var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return new MediaWikiCargoSnapshotTableManifest(table.LogicalTable, table.PhysicalTable, table.Rows.Count,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant(),
                table.Columns, table.HelperRelationships);
        }).ToArray();
        var manifest = new MediaWikiCargoSnapshotManifest(FormatVersion, request.SnapshotId, DateTimeOffset.UtcNow,
            request.SourceIdentity, request.SourceWikiIdentity, request.CargoTablePrefix,
            request.SchemaColumnsSha256, request.SchemaIndexesSha256, tableManifests,
            "MariaDB_REPEATABLE_READ_transaction", request.DatasetClassification, CanonicalizationVersion,
            acquisitionToolVersion, true, false);
        await WriteAsync(request.Destination, manifest, tables, cancellationToken);
    }

    public static async Task WriteAsync(
        string destination,
        MediaWikiCargoSnapshotManifest manifest,
        IReadOnlyList<MediaWikiCargoSnapshotTable> tables,
        CancellationToken cancellationToken = default)
    {
        if (!manifest.Complete) throw new InvalidDataException("Only complete snapshots may be finalized.");
        if (tables.Count != manifest.Tables.Count) throw new InvalidDataException("Manifest table count does not match snapshot data.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        var temporary = destination + ".partial." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        RestrictDirectory(temporary);
        try
        {
            foreach (var table in tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tableManifest = manifest.Tables.SingleOrDefault(value => value.LogicalTable == table.LogicalTable);
                if (tableManifest is null) throw new InvalidDataException($"Table {table.LogicalTable} is not present in the manifest.");
                var bytes = CanonicalTableBytes(table);
                await File.WriteAllBytesAsync(Path.Combine(temporary, FileName(table.LogicalTable)), bytes, cancellationToken);
                RestrictFile(Path.Combine(temporary, FileName(table.LogicalTable)));
                var contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (contentHash != tableManifest.ContentSha256 || table.Rows.Count != tableManifest.ExactRowCount)
                    throw new InvalidDataException($"Table {table.LogicalTable} manifest integrity does not match its content.");
            }
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            await File.WriteAllBytesAsync(Path.Combine(temporary, "manifest.json"), manifestBytes, cancellationToken);
            RestrictFile(Path.Combine(temporary, "manifest.json"));
            if (Directory.Exists(destination)) throw new IOException("A snapshot already exists; refusing to replace immutable evidence.");
            Directory.Move(temporary, destination);
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    public static async Task<MediaWikiCargoSnapshotValidationResult> VerifyAsync(
        string snapshotPath,
        string expectedSourceIdentity,
        string expectedColumnsHash,
        string expectedIndexesHash,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        MediaWikiCargoSnapshotManifest? manifest = null;
        try
        {
            await using var stream = File.OpenRead(Path.Combine(snapshotPath, "manifest.json"));
            manifest = await JsonSerializer.DeserializeAsync<MediaWikiCargoSnapshotManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            failures.Add("manifest.json could not be read or parsed");
        }
        if (manifest is null) return Result(failures, false, false, false, false, false);
        var sourceOk = manifest.SourceIdentity == expectedSourceIdentity;
        var schemaOk = manifest.SchemaColumnsSha256 == expectedColumnsHash && manifest.SchemaIndexesSha256 == expectedIndexesHash;
        var consistencyOk = manifest.Complete && manifest.ConsistencyMethod.Length > 0;
        if (manifest.FormatVersion != FormatVersion) failures.Add("unsupported snapshot format");
        if (!sourceOk) failures.Add("source identity mismatch");
        if (!schemaOk) failures.Add("schema hash mismatch");
        if (!consistencyOk) failures.Add("snapshot is incomplete or has no consistency evidence");
        var contentOk = true;
        foreach (var table in manifest.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = Path.Combine(snapshotPath, FileName(table.LogicalTable));
            if (!File.Exists(file)) { failures.Add($"missing table file: {table.LogicalTable}"); contentOk = false; continue; }
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), table.ContentSha256, StringComparison.Ordinal))
            { failures.Add($"content hash mismatch: {table.LogicalTable}"); contentOk = false; }
            var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.LongLength != table.ExactRowCount) { failures.Add($"row count mismatch: {table.LogicalTable}"); contentOk = false; }
            var rowIntegrity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
            if (rowIntegrity != table.RowIntegritySha256) { failures.Add($"row integrity mismatch: {table.LogicalTable}"); contentOk = false; }
        }
        return Result(failures, sourceOk, schemaOk, contentOk, consistencyOk, manifest.HistoricalCorrelationVerified);
    }

    public static byte[] CanonicalTableBytes(MediaWikiCargoSnapshotTable table)
    {
        using var output = new MemoryStream();
        foreach (var row in table.Rows)
        {
            var values = row.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
            if (values.Count != table.Columns.Count || !table.Columns.SequenceEqual(values.Keys))
                throw new InvalidDataException($"Table {table.LogicalTable} contains an invalid or reordered row.");
            var json = JsonSerializer.Serialize(values, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            output.Write(bytes);
        }
        return output.ToArray();
    }

    public static string FileName(string logicalTable) => logicalTable + ".jsonl";

    private static bool IsIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && value.All(value => char.IsLetterOrDigit(value) || value == '_');
    private static void ValidateIdentifier(string value)
    {
        if (!IsIdentifier(value)) throw new InvalidDataException("A Cargo identifier failed validation.");
    }

    private static MediaWikiCargoSnapshotValidationResult Result(List<string> failures, bool source, bool schema, bool content, bool consistency, bool historical)
        => new(failures.Count == 0, failures, source, schema, content, consistency, historical);

    private static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
