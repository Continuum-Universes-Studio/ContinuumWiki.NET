using System.Text.Json;

namespace TightWiki.Engine.MediaWiki;

/// <summary>
/// Diagnostic-only provider over an independently verified private snapshot.
/// It is current-state-only and is never selected by application defaults.
/// </summary>
public sealed class MediaWikiCargoSnapshotQueryProvider : IMediaWikiCargoQueryProvider
{
    private readonly MediaWikiCargoSnapshotManifest _manifest;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> _rows;
    private readonly string _sourceIdentity;
    private readonly string _sourceWikiIdentity;

    private MediaWikiCargoSnapshotQueryProvider(
        MediaWikiCargoSnapshotManifest manifest,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> rows)
    {
        _manifest = manifest;
        _rows = rows;
        _sourceIdentity = manifest.SourceIdentity;
        _sourceWikiIdentity = manifest.SourceWikiIdentity;
    }

    public sealed record DiagnosticAuthorization(
        string SnapshotId,
        string SourceIdentity,
        string SourceWikiIdentity,
        string SchemaColumnsSha256,
        string SchemaIndexesSha256,
        bool ExplicitlyAuthorized);

    public static Task<MediaWikiCargoSnapshotQueryProvider> OpenAuthorizedAsync(
        string snapshotPath,
        DiagnosticAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!authorization.ExplicitlyAuthorized)
            throw new InvalidOperationException("Positive Cargo activation requires explicit diagnostic authorization.");
        return OpenVerifiedAsync(snapshotPath, authorization.SourceIdentity, authorization.SourceWikiIdentity,
            authorization.SnapshotId, authorization.SchemaColumnsSha256, authorization.SchemaIndexesSha256, cancellationToken);
    }

    public static async Task<MediaWikiCargoSnapshotQueryProvider> OpenVerifiedAsync(
        string snapshotPath,
        string expectedSourceIdentity,
        string expectedSourceWikiIdentity,
        string expectedSnapshotId,
        string expectedColumnsHash,
        string expectedIndexesHash,
        CancellationToken cancellationToken = default)
    {
        var validation = await MediaWikiCargoSnapshotStore.VerifyAsync(snapshotPath, expectedSourceIdentity, expectedColumnsHash, expectedIndexesHash, cancellationToken);
        if (!validation.IsValid || !validation.ContentIntegrityVerified || !validation.SchemaVerified || !validation.ConsistencyVerified)
            throw new InvalidDataException("The Cargo snapshot failed independent verification and cannot be activated.");
        await using var manifestStream = File.OpenRead(Path.Combine(snapshotPath, "manifest.json"));
        var manifest = await JsonSerializer.DeserializeAsync<MediaWikiCargoSnapshotManifest>(manifestStream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("The Cargo snapshot manifest was empty.");
        if (!string.Equals(manifest.DatasetClassification, "AUTHENTIC_SOURCE_CURRENT_STATE", StringComparison.Ordinal)
            || manifest.HistoricalCorrelationVerified)
            throw new InvalidDataException("Only an explicitly current-state, non-historical snapshot may be used by this diagnostic provider.");
        if (!string.Equals(manifest.SourceWikiIdentity, expectedSourceWikiIdentity, StringComparison.Ordinal)
            || !string.Equals(manifest.SnapshotId, expectedSnapshotId, StringComparison.Ordinal))
            throw new InvalidDataException("The snapshot identity did not match the authorized diagnostic dataset.");
        var rows = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(StringComparer.Ordinal);
        foreach (var table in manifest.Tables)
        {
            var tableRows = new List<IReadOnlyDictionary<string, string?>>();
            foreach (var line in await File.ReadAllLinesAsync(Path.Combine(snapshotPath, MediaWikiCargoSnapshotStore.FileName(table.LogicalTable)), cancellationToken))
            {
                if (line.Length == 0) continue;
                using var document = JsonDocument.Parse(line);
                var row = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var cell in document.RootElement.EnumerateObject())
                    row[cell.Name] = cell.Value.ValueKind == JsonValueKind.Null ? null : cell.Value.GetString();
                tableRows.Add(row);
            }
            rows.Add(table.LogicalTable, tableRows);
        }
        return new(manifest, rows);
    }

    public Task<MediaWikiCargoProviderResult> QueryAsync(MediaWikiCargoQueryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = MediaWikiCargoQueryValidator.Validate(request, SchemaVerifiedMediaWikiCargoQueryProvider.VerifiedScalarAllowlist);
        if (!string.Equals(request.Identity.DatasetIdentity, _manifest.SnapshotId, StringComparison.Ordinal)
            || !string.Equals(request.Identity.SourceWikiIdentity, _sourceWikiIdentity, StringComparison.Ordinal)
            || !string.Equals(request.Identity.SourcePageId, "current-state", StringComparison.Ordinal)
            || !string.Equals(request.Identity.SourceRevisionId, "current-state", StringComparison.Ordinal))
            validation = validation.Append(new(MediaWikiCargoProviderFailure.HistoricalProvenanceUnavailable, "The verified snapshot is current-state only and cannot answer a historical revision query.")).ToArray();
        if (validation.Count > 0) return Task.FromResult(new MediaWikiCargoProviderResult(null, validation[0]));
        if (!_rows.TryGetValue(request.Table.Name, out var sourceRows))
            return Task.FromResult(new MediaWikiCargoProviderResult(null, new(MediaWikiCargoProviderFailure.InvalidQuery, "The Cargo table is not present in the verified snapshot.")));

        IEnumerable<IReadOnlyDictionary<string, string?>> rows = sourceRows;
        foreach (var predicate in request.Predicates)
        {
            rows = rows.Where(row => Matches(row.TryGetValue(predicate.Field.Name, out var value) ? value : null, predicate));
        }
        IOrderedEnumerable<IReadOnlyDictionary<string, string?>>? ordered = null;
        foreach (var sort in request.Sort)
            ordered = ordered is null
                ? (sort.Descending ? rows.OrderByDescending(row => SortValue(row, sort.Field.Name), StringComparer.Ordinal) : rows.OrderBy(row => SortValue(row, sort.Field.Name), StringComparer.Ordinal))
                : (sort.Descending ? ordered.ThenByDescending(row => SortValue(row, sort.Field.Name), StringComparer.Ordinal) : ordered.ThenBy(row => SortValue(row, sort.Field.Name), StringComparer.Ordinal));
        rows = ordered ?? rows;
        rows = rows.Skip(request.Offset).Take(request.Limit);
        var resultRows = new List<IReadOnlyList<MediaWikiCargoResultColumn>>();
        var outputCharacters = 0;
        foreach (var row in rows)
        {
            var resultRow = new List<MediaWikiCargoResultColumn>();
            foreach (var field in request.Fields)
            {
                var value = row.TryGetValue(field.Name, out var raw) ? raw ?? string.Empty : string.Empty;
                outputCharacters += field.Name.Length + value.Length;
                if (outputCharacters > request.Budget.MaxOutputCharacters)
                    return Task.FromResult(new MediaWikiCargoProviderResult(null, new(MediaWikiCargoProviderFailure.BudgetExceeded, "Cargo output exceeded the configured character budget.")));
                resultRow.Add(new(field.Name, value));
            }
            resultRows.Add(resultRow);
        }
        return Task.FromResult(new MediaWikiCargoProviderResult(
            new MediaWikiCargoQueryResult(resultRows, request.Identity, $"current:{_manifest.SnapshotId}"), null));
    }

    private static string SortValue(IReadOnlyDictionary<string, string?> row, string field) => row.TryGetValue(field, out var value) ? value ?? string.Empty : string.Empty;
    private static bool Matches(string? value, MediaWikiCargoPredicate predicate)
        => predicate.Operator switch
        {
            MediaWikiCargoPredicateOperator.Equals => string.Equals(value, predicate.Value, StringComparison.Ordinal),
            MediaWikiCargoPredicateOperator.NotEquals => !string.Equals(value, predicate.Value, StringComparison.Ordinal),
            MediaWikiCargoPredicateOperator.Contains => value?.Contains(predicate.Value ?? string.Empty, StringComparison.Ordinal) == true,
            MediaWikiCargoPredicateOperator.StartsWith => value?.StartsWith(predicate.Value ?? string.Empty, StringComparison.Ordinal) == true,
            MediaWikiCargoPredicateOperator.IsNull => value is null,
            MediaWikiCargoPredicateOperator.IsNotNull => value is not null,
            _ => false
        };
}
