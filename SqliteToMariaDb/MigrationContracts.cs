using System.Text.Json.Serialization;

namespace ContinuumWiki.SqliteToMariaDb;

public sealed record MigrationOptions(
    string Operation,
    string SourceRoot,
    string TranslationDatabase,
    string DefaultsDatabase,
    string DestinationConnectionString,
    string ReportPath)
{
    public static MigrationOptions Parse(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("Usage: <integrity|preflight|dry-run|execute|verify> --source-root <Data> --translations <Translations.db> --defaults <defaults.db> --destination <MariaDB connection string> --report <report.json>");

        var operation = args[0].Trim().ToLowerInvariant();
        if (operation is not ("integrity" or "preflight" or "dry-run" or "execute" or "verify"))
            throw new ArgumentException($"Unknown operation '{args[0]}'. Expected integrity, preflight, dry-run, execute, or verify.");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException($"Invalid option '{args[i]}'. Options require a value.");
            values[args[i][2..]] = args[++i];
        }

        static string Required(IReadOnlyDictionary<string, string> values, string name)
            => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"Missing required option '--{name}'.");

        return new(
            operation,
            Path.GetFullPath(Required(values, "source-root")),
            Path.GetFullPath(Required(values, "translations")),
            Path.GetFullPath(Required(values, "defaults")),
            Required(values, "destination"),
            Path.GetFullPath(Required(values, "report")));
    }
}

public sealed record MigrationMapping(
    string SourceStore,
    string SourceTable,
    string TargetTable,
    IReadOnlyList<string>? Columns,
    IReadOnlyList<string> PrimaryKey,
    string Classification,
    string? Transformation = null,
    string? SelectSql = null);

public sealed record SourceStoreReport(
    string Name,
    string Path,
    bool ReadOnly,
    bool Readable,
    IReadOnlyDictionary<string, long> TableCounts,
    string? Error);

public sealed record MappingReport(
    string SourceStore,
    string SourceTable,
    string TargetTable,
    string Classification,
    string? Transformation,
    long SourceCount,
    long PlannedCount,
    long? DestinationCount,
    long BinaryBytes,
    string? SourceBinarySha256,
    string? DestinationBinarySha256,
    string Status,
    string? Detail);

public sealed record MigrationReport(
    string Operation,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string TargetProvider,
    string? TargetServerVersion,
    string? TargetServerComment,
    string? TargetCollation,
    string? CompletionMarker,
    IReadOnlyList<SourceStoreReport> SourceStores,
    IReadOnlyList<MappingReport> Mappings,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Unsupported,
    IReadOnlyList<string> Exclusions,
    IReadOnlyList<string> Discrepancies,
    IReadOnlyList<string> MigrationOrder,
    long BinaryBytes,
    bool DestinationMutated,
    bool Succeeded,
    IReadOnlyList<IntegrityFinding>? IntegrityFindings = null,
    IReadOnlyList<RepairPlanEntry>? RepairPlan = null);

public sealed record IntegrityFinding(
    string SourceStore,
    string SourceTable,
    string RecordKey,
    string Relationship,
    string Category,
    string Classification,
    string Severity,
    string Observed,
    string ProposedAction,
    bool RequiresOperatorApproval);

public sealed record RepairPlanEntry(
    string SourceStore,
    string SourceTable,
    string RecordKey,
    string ObservedDefect,
    string ProposedTransformation,
    string Evidence,
    string RelationalEffect,
    string Reversibility,
    string Risk,
    bool RequiresOperatorApproval);

public sealed record DestinationInfo(string ServerVersion, string ServerComment, string Collation);
