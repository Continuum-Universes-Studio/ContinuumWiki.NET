using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using MediaWikiInventory;
using MySqlConnector;
using TightWiki.Library;
using TightWiki.Library.Database;
using TightWiki.Library.Security;
using TightWiki.Plugin;
using TightWiki.Repository.Helpers;

namespace MediaWikiImport;

public enum MediaWikiImportBatchStatus
{
    Running,
    Completed,
    RolledBack,
    Failed
}

public sealed record MediaWikiImportTargetConfiguration(
    string ConnectionString,
    string DatabaseName,
    string Purpose,
    bool RequireEmptyTarget = true)
{
    public static MediaWikiImportTargetConfiguration Load(string path)
    {
        var json = File.ReadAllText(path);
        var value = JsonSerializer.Deserialize<MediaWikiImportTargetConfiguration>(json, JsonOptions());
        if (value is null || string.IsNullOrWhiteSpace(value.DatabaseName) || string.IsNullOrWhiteSpace(value.Purpose))
            throw new InvalidDataException("Target configuration must specify DatabaseName and Purpose.");

        if (string.IsNullOrWhiteSpace(value.ConnectionString))
        {
            var environmentVariable = JsonSerializer.Deserialize<TargetEnvironmentConfiguration>(json, JsonOptions())?.ConnectionStringEnvironmentVariable;
            if (string.IsNullOrWhiteSpace(environmentVariable) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
                throw new InvalidDataException("Target configuration must provide ConnectionString or a populated ConnectionStringEnvironmentVariable.");
            return value with { ConnectionString = Environment.GetEnvironmentVariable(environmentVariable)! };
        }

        return value;
    }

    private sealed record TargetEnvironmentConfiguration(string? ConnectionStringEnvironmentVariable);
    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };
}

public sealed record MediaWikiImportPreflightResult(
    bool IsReady,
    string DatabaseName,
    int ExistingPageCount,
    int ExistingRevisionCount,
    IReadOnlyList<string> Diagnostics);

public sealed record MediaWikiReconciliationDifference(
    string Code,
    string Message,
    string? SourcePageId = null,
    string? SourceRevisionId = null);

public sealed record MediaWikiReconciliationReport(
    long ImportBatchId,
    string Verdict,
    int SourcePageCount,
    int ImportedPageCount,
    int SourceRevisionCount,
    int ImportedRevisionCount,
    IReadOnlyList<MediaWikiReconciliationDifference> Differences,
    string DeterministicHash)
{
    public int SourceMediaCount { get; init; }
    public int ImportedMediaCount { get; init; }
    public int SourceMediaRevisionCount { get; init; }
    public int ImportedMediaRevisionCount { get; init; }
    public bool IsSuccessful => string.Equals(Verdict, "VERIFIED", StringComparison.Ordinal);
}

public sealed record MediaWikiImportExecutionResult(
    long ImportBatchId,
    MediaWikiImportBatchStatus Status,
    MediaWikiReconciliationReport Reconciliation);

public sealed record MediaWikiImportStatus(
    long ImportBatchId,
    MediaWikiImportBatchStatus Status,
    string? Diagnostics);

public sealed class MediaWikiImportPreflightException(IReadOnlyList<string> diagnostics)
    : InvalidOperationException("MediaWiki import preflight failed: " + string.Join("; ", diagnostics))
{
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
}

public sealed class MediaWikiImportPlanStaleException(string message) : InvalidOperationException(message);

/// <summary>
/// Executes a validated MediaWiki plan against an explicitly named disposable
/// MariaDB target. It never creates historical login accounts: all source
/// attribution is stored in the import provenance tables, while target page
/// rows use <see cref="ImportActorUserId"/> as an explicit system marker.
/// </summary>
public sealed class MediaWikiImportExecutor
{
    public static readonly Guid ImportActorUserId = Guid.Empty;
    private const string DisposablePurpose = "disposable-mariadb";
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly MediaWikiImportTargetConfiguration _target;

    public MediaWikiImportExecutor(IDatabaseConnectionFactory connectionFactory, MediaWikiImportTargetConfiguration target)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public Task InitializeImportSchemaAsync(CancellationToken cancellationToken = default)
        => new MySqlMediaWikiImportSchemaInitializer().InitializeAsync(_connectionFactory, cancellationToken);

    public async Task<MediaWikiImportPreflightResult> PreflightAsync(
        MediaWikiImportPlan plan,
        string inputPath,
        NamespaceMappingConfiguration mapping,
        string? mediaManifestPath = null,
        string? mediaRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(mapping);
        var diagnostics = new List<string>();
        diagnostics.AddRange(ImportPlanValidator.Validate(plan, inputPath, mapping, mediaManifestPath, mediaRoot).Select(finding => finding.Code + ": " + finding.Message));
        if (plan.OverallDisposition == ImportDisposition.Blocked)
            diagnostics.Add("The import plan has a blocking disposition.");
        if (!_target.Purpose.Equals(DisposablePurpose, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add($"Target purpose must be '{DisposablePurpose}'.");
        if ((plan.Source.SourceWikiIdentity ?? string.Empty).Length > 512)
            diagnostics.Add("Source wiki identity exceeds the provenance schema limit.");
        foreach (var page in plan.Pages)
        {
            if (page.SourcePageId.Length > 128) diagnostics.Add($"Source page id {page.SourcePageId} exceeds the provenance schema limit.");
            if (page.OriginalTitle.Length > 512) diagnostics.Add($"Source page {page.SourcePageId} title exceeds the provenance schema limit.");
            if (page.ProposedTargetNavigation is null) diagnostics.Add($"Source page {page.SourcePageId} has no target navigation.");
            else
            {
                try
                {
                    if (TwNamespaceNavigation.CleanAndValidate(page.ProposedTargetNavigation, lowerCase: false).Length > 128)
                        diagnostics.Add($"Target navigation for source page {page.SourcePageId} exceeds the target schema limit.");
                }
                catch (Exception exception)
                {
                    diagnostics.Add($"Target navigation for source page {page.SourcePageId} is invalid: {exception.Message}");
                }
            }
            if (page.Revisions.Count == 0) diagnostics.Add($"Source page {page.SourcePageId} has no revisions.");
            foreach (var revision in page.Revisions)
            {
                if (revision.SourceRevisionId.Length > 128) diagnostics.Add($"Source revision id {revision.SourceRevisionId} exceeds the provenance schema limit.");
                if (revision.Timestamp is null) diagnostics.Add($"Source revision {revision.SourceRevisionId} has no timestamp; the target timestamp would have to be fabricated.");
            }
        }

        var builder = new MySqlConnectionStringBuilder(_target.ConnectionString);
        if (!string.Equals(builder.Database, _target.DatabaseName, StringComparison.Ordinal))
            diagnostics.Add($"Target database '{builder.Database}' does not match the explicitly authorized database '{_target.DatabaseName}'.");

        var existingPageCount = 0;
        var existingRevisionCount = 0;
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        foreach (var table in new[] { "Page", "PageRevision", "PageFile", "PageFileRevision", "MediaWikiImportSchemaVersion", "MediaWikiImportBatch", "MediaWikiImportMedia", "MediaWikiImportMediaRevision", "MediaWikiImportMediaReference" })
        {
            var exists = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @TableName;",
                new { TableName = table }, cancellationToken: cancellationToken));
            if (exists == 0) diagnostics.Add($"Required target table '{table}' is missing.");
        }

        if (!diagnostics.Any(value => value.StartsWith("Required target table", StringComparison.Ordinal)))
        {
            existingPageCount = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM Page;", cancellationToken: cancellationToken));
            existingRevisionCount = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM PageRevision;", cancellationToken: cancellationToken));
            var existingFileCount = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM PageFile;", cancellationToken: cancellationToken));
            var existingFileRevisionCount = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM PageFileRevision;", cancellationToken: cancellationToken));
            if (_target.RequireEmptyTarget && (existingPageCount != 0 || existingRevisionCount != 0 || existingFileCount != 0 || existingFileRevisionCount != 0))
                diagnostics.Add($"Disposable target is not empty: Page={existingPageCount}, PageRevision={existingRevisionCount}, PageFile={existingFileCount}, PageFileRevision={existingFileRevisionCount}.");

            var duplicate = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM MediaWikiImportBatch
                WHERE SourceWikiIdentity = @SourceWikiIdentity
                  AND SourceExportSha256 = @SourceExportSha256
                  AND MappingConfigurationIdentity = @MappingConfigurationIdentity
                  AND Status IN ('Running', 'Completed');
                """, new
            {
                SourceWikiIdentity = plan.Source.SourceWikiIdentity ?? string.Empty,
                SourceExportSha256 = plan.Source.Sha256,
                MappingConfigurationIdentity = plan.MappingConfigurationIdentity
            }, cancellationToken: cancellationToken));
            if (duplicate > 0) diagnostics.Add("This source export and mapping already has a running or completed import batch.");
        }

        return new(diagnostics.Count == 0, builder.Database, existingPageCount, existingRevisionCount, diagnostics.Order(StringComparer.Ordinal).ToArray());
    }

    public async Task<MediaWikiImportExecutionResult> ImportAsync(
        MediaWikiImportPlan plan,
        string inputPath,
        NamespaceMappingConfiguration mapping,
        string? mediaManifestPath = null,
        string? mediaRoot = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeImportSchemaAsync(cancellationToken);
        var preflight = await PreflightAsync(plan, inputPath, mapping, mediaManifestPath, mediaRoot, cancellationToken);
        if (!preflight.IsReady) throw new MediaWikiImportPreflightException(preflight.Diagnostics);

        var batchId = await CreateBatchAsync(plan, cancellationToken);
        try
        {
            MediaWikiReconciliationReport inTransactionReport;
            await using (var session = await _connectionFactory.OpenSessionAsync(cancellationToken))
            await using (var transaction = await session.BeginTransactionAsync(cancellationToken))
            {
                await ImportPagesAsync(session.Connection, transaction.Transaction, plan, inputPath, batchId, cancellationToken);
                if (plan.Media is not null)
                    await ImportMediaAsync(session.Connection, transaction.Transaction, plan, batchId, mediaManifestPath!, mediaRoot!, cancellationToken);
                inTransactionReport = await ReconcileCoreAsync(session.Connection, transaction.Transaction, plan, batchId, cancellationToken);
                if (!inTransactionReport.IsSuccessful)
                    throw new InvalidOperationException("Import reconciliation failed before commit: " + string.Join("; ", inTransactionReport.Differences.Select(difference => difference.Message)));
                var reportJson = JsonSerializer.Serialize(inTransactionReport, JsonOptions());
                await session.Connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE MediaWikiImportBatch
                    SET Status = 'Completed', CompletedAtUtc = UTC_TIMESTAMP(6),
                        ImportedPageCount = @ImportedPageCount, ImportedRevisionCount = @ImportedRevisionCount,
                        ReconciliationJson = @ReconciliationJson
                    WHERE ImportBatchId = @ImportBatchId AND Status = 'Running';
                    """, new { ImportBatchId = batchId, inTransactionReport.ImportedPageCount, inTransactionReport.ImportedRevisionCount, ReconciliationJson = reportJson }, transaction.Transaction, cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);
            }

            var committedReport = await ReconcileAsync(plan, batchId, cancellationToken);
            if (!committedReport.IsSuccessful)
                throw new InvalidOperationException("Committed import reconciliation failed: " + string.Join("; ", committedReport.Differences.Select(difference => difference.Message)));
            return new(batchId, MediaWikiImportBatchStatus.Completed, committedReport);
        }
        catch (Exception exception)
        {
            await MarkBatchRolledBackAsync(batchId, exception);
            throw;
        }
    }

    public async Task<MediaWikiReconciliationReport> ReconcileAsync(MediaWikiImportPlan plan, long batchId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await ReconcileCoreAsync(session.Connection, null, plan, batchId, cancellationToken);
    }

    public async Task<MediaWikiImportStatus> GetStatusAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var row = await session.Connection.QuerySingleOrDefaultAsync<BatchStatusRow>(new CommandDefinition("SELECT ImportBatchId, Status, DiagnosticsJson FROM MediaWikiImportBatch WHERE ImportBatchId = @ImportBatchId;", new { ImportBatchId = batchId }, cancellationToken: cancellationToken));
        if (row is null) throw new KeyNotFoundException($"Import batch {batchId} was not found.");
        return new(row.ImportBatchId, Enum.Parse<MediaWikiImportBatchStatus>(row.Status, true), row.DiagnosticsJson);
    }

    private async Task<long> CreateBatchAsync(MediaWikiImportPlan plan, CancellationToken cancellationToken)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO MediaWikiImportBatch
                (SourceWikiIdentity, SourceExportSha256, SourceExportByteLength, ExportVersion,
                 PlannerVersion, PlanSchemaVersion, MappingConfigurationIdentity, Status,
                 PlannedPageCount, PlannedRevisionCount, StartedAtUtc)
            VALUES (@SourceWikiIdentity, @SourceExportSha256, @SourceExportByteLength, @ExportVersion,
                    @PlannerVersion, @PlanSchemaVersion, @MappingConfigurationIdentity, 'Running',
                    @PlannedPageCount, @PlannedRevisionCount, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """, new
        {
            SourceWikiIdentity = plan.Source.SourceWikiIdentity ?? string.Empty,
            SourceExportSha256 = plan.Source.Sha256,
            SourceExportByteLength = plan.Source.ByteLength,
            plan.Source.ExportVersion,
            plan.PlannerVersion,
            plan.PlanSchemaVersion,
            plan.MappingConfigurationIdentity,
            plan.PlannedPageCount,
            plan.PlannedRevisionCount
        }, cancellationToken: cancellationToken));
    }

    private async Task ImportPagesAsync(DbConnection connection, DbTransaction transaction, MediaWikiImportPlan plan, string inputPath, long batchId, CancellationToken cancellationToken)
    {
        var plans = plan.Pages.ToDictionary(page => page.SourcePageId, StringComparer.Ordinal);
        var importedPages = new Dictionary<string, int>(StringComparer.Ordinal);
        var sourcePageCount = 0;
        var sourceRevisionCount = 0;
        using var stream = new MediaWikiXmlReader().Open(inputPath);
        foreach (var sourcePage in stream.ReadPages())
        {
            sourcePageCount++;
            if (!plans.TryGetValue(sourcePage.SourcePageId, out var pagePlan))
                throw new MediaWikiImportPlanStaleException($"Source page {sourcePage.SourcePageId} is absent from the validated plan.");
            if (sourcePage.Revisions.Count != pagePlan.Revisions.Count)
                throw new MediaWikiImportPlanStaleException($"Revision count for source page {sourcePage.SourcePageId} changed after planning.");
            for (var index = 0; index < sourcePage.Revisions.Count; index++)
            {
                var sourceRevision = sourcePage.Revisions[index];
                var plannedRevision = pagePlan.Revisions[index];
                if (!string.Equals(sourceRevision.SourceRevisionId, plannedRevision.SourceRevisionId, StringComparison.Ordinal)
                    || !string.Equals(Hashing.Sha256(Encoding.UTF8.GetBytes(sourceRevision.Wikitext)), plannedRevision.WikitextSha256, StringComparison.OrdinalIgnoreCase))
                    throw new MediaWikiImportPlanStaleException($"Source revision {sourceRevision.SourceRevisionId} for page {sourcePage.SourcePageId} changed after planning.");
                sourceRevisionCount++;
            }

            if (pagePlan.ProposedTargetNavigation is null || pagePlan.Revisions.Count == 0)
                throw new InvalidOperationException($"Source page {pagePlan.SourcePageId} has no importable target navigation or revisions.");
            var targetNavigation = TwNamespaceNavigation.CleanAndValidate(pagePlan.ProposedTargetNavigation, lowerCase: false);
            if (targetNavigation.Length == 0 || targetNavigation.Length > 128)
                throw new InvalidOperationException($"Target navigation for source page {pagePlan.SourcePageId} is not representable by the MariaDB page schema.");
            var firstTimestamp = pagePlan.Revisions[0].Timestamp?.UtcDateTime
                ?? throw new InvalidOperationException($"Source page {pagePlan.SourcePageId} has no timestamp for its first revision.");
            if (pagePlan.Revisions.Any(revision => revision.Timestamp is null))
                throw new InvalidOperationException($"Source page {pagePlan.SourcePageId} contains a revision without a timestamp; no target timestamp is fabricated.");

            var currentRevision = pagePlan.Revisions.Count;
            var targetPageId = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                INSERT INTO Page
                    (Name, Namespace, Navigation, ActiveNavigation, Description, Revision,
                     CreatedByUserId, CreatedDate, ModifiedByUserId, ModifiedDate)
                VALUES (@Name, @Namespace, @Navigation, @Navigation, '', @Revision,
                        @Actor, @CreatedDate, @Actor, @ModifiedDate);
                SELECT LAST_INSERT_ID();
                """, new
            {
                Name = pagePlan.ProposedTargetNavigation,
                Namespace = pagePlan.TargetNamespace ?? string.Empty,
                Navigation = targetNavigation,
                Revision = currentRevision,
                Actor = ImportActorUserId.ToString("D"),
                CreatedDate = firstTimestamp,
                ModifiedDate = pagePlan.Revisions[^1].Timestamp!.Value.UtcDateTime
            }, transaction, cancellationToken: cancellationToken));
            importedPages.Add(pagePlan.SourcePageId, targetPageId);

            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO MediaWikiImportPage
                    (ImportBatchId, SourcePageId, TargetPageId, SourceNamespaceId, SourceNamespaceName,
                     OriginalTitle, TargetNamespace, TargetNavigation, IsRedirect,
                     RedirectTargetSourceTitle, RedirectTargetNavigation, Status, DiagnosticsJson)
                VALUES (@ImportBatchId, @SourcePageId, @TargetPageId, @SourceNamespaceId, @SourceNamespaceName,
                        @OriginalTitle, @TargetNamespace, @TargetNavigation, @IsRedirect,
                        @RedirectTargetSourceTitle, @RedirectTargetNavigation, 'Imported', @DiagnosticsJson);
                """, new
            {
                ImportBatchId = batchId,
                pagePlan.SourcePageId,
                TargetPageId = targetPageId,
                pagePlan.SourceNamespaceId,
                pagePlan.SourceNamespaceName,
                pagePlan.OriginalTitle,
                pagePlan.TargetNamespace,
                TargetNavigation = targetNavigation,
                pagePlan.IsRedirect,
                pagePlan.RedirectTargetSourceTitle,
                RedirectTargetNavigation = pagePlan.RedirectTargetNavigation is null ? null : TwNamespaceNavigation.CleanAndValidate(pagePlan.RedirectTargetNavigation, lowerCase: false),
                DiagnosticsJson = JsonSerializer.Serialize(pagePlan.Findings, JsonOptions())
            }, transaction, cancellationToken: cancellationToken));

            foreach (var plannedRevision in pagePlan.Revisions)
            {
                var targetRevision = plannedRevision.RevisionOrdinal;
                var modifiedDate = plannedRevision.Timestamp!.Value.UtcDateTime;
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO PageRevision
                        (PageId, Revision, Name, Namespace, Navigation, Description, Body, DataHash,
                         ChangeSummary, ModifiedByUserId, ModifiedDate)
                    VALUES (@PageId, @Revision, @Name, @Namespace, @Navigation, '', @Body, @DataHash,
                            @ChangeSummary, @Actor, @ModifiedDate);
                    """, new
                {
                    PageId = targetPageId,
                    Revision = targetRevision,
                    Name = pagePlan.ProposedTargetNavigation,
                    Namespace = pagePlan.TargetNamespace ?? string.Empty,
                    Navigation = targetNavigation,
                    Body = plannedRevision.Wikitext,
                    DataHash = SecurityUtility.Crc32(plannedRevision.Wikitext),
                    ChangeSummary = plannedRevision.Comment ?? string.Empty,
                    Actor = ImportActorUserId.ToString("D"),
                    ModifiedDate = modifiedDate
                }, transaction, cancellationToken: cancellationToken));

                var sourceTextState = plannedRevision.IsSuppressed ? "Suppressed" : plannedRevision.IsDeleted ? "Deleted" : plannedRevision.WikitextPresent ? "Present" : "Missing";
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO MediaWikiImportRevision
                        (ImportBatchId, SourcePageId, SourceRevisionId, TargetPageId, TargetRevision,
                         ParentSourceRevisionId, SourceTimestamp, SourceComment, ContributorKind,
                         ContributorSourceId, ContributorDisplayName, ContributorIpAddress, ContributorResolution,
                         ContentModel, ContentFormat, SourceSha1, IsMinor, IsDeleted, IsSuppressed,
                         SourceTextPresent, SourceTextState, WikitextSha256, DependenciesJson, Status, DiagnosticsJson)
                    VALUES (@ImportBatchId, @SourcePageId, @SourceRevisionId, @TargetPageId, @TargetRevision,
                            @ParentSourceRevisionId, @SourceTimestamp, @SourceComment, @ContributorKind,
                            @ContributorSourceId, @ContributorDisplayName, @ContributorIpAddress, @ContributorResolution,
                            @ContentModel, @ContentFormat, @SourceSha1, @IsMinor, @IsDeleted, @IsSuppressed,
                            @SourceTextPresent, @SourceTextState, @WikitextSha256, @DependenciesJson, 'Imported', @DiagnosticsJson);
                    """, new
                {
                    ImportBatchId = batchId,
                    SourcePageId = pagePlan.SourcePageId,
                    SourceRevisionId = plannedRevision.SourceRevisionId,
                    TargetPageId = targetPageId,
                    TargetRevision = targetRevision,
                    ParentSourceRevisionId = plannedRevision.ParentRevisionId,
                    SourceTimestamp = modifiedDate,
                    SourceComment = plannedRevision.Comment,
                    ContributorKind = plannedRevision.Contributor.Kind.ToString(),
                    ContributorSourceId = plannedRevision.Contributor.SourceId,
                    ContributorDisplayName = plannedRevision.Contributor.DisplayName,
                    ContributorIpAddress = plannedRevision.Contributor.IpAddress,
                    ContributorResolution = plannedRevision.Contributor.IdentityResolution,
                    ContentModel = plannedRevision.ContentModel,
                    ContentFormat = plannedRevision.ContentFormat,
                    SourceSha1 = plannedRevision.Sha1,
                    IsMinor = plannedRevision.IsMinor,
                    IsDeleted = plannedRevision.IsDeleted,
                    IsSuppressed = plannedRevision.IsSuppressed,
                    SourceTextPresent = plannedRevision.WikitextPresent,
                    SourceTextState = sourceTextState,
                    WikitextSha256 = plannedRevision.WikitextSha256,
                    DependenciesJson = JsonSerializer.Serialize(plannedRevision.Dependencies, JsonOptions()),
                    DiagnosticsJson = JsonSerializer.Serialize(plannedRevision.Findings, JsonOptions())
                }, transaction, cancellationToken: cancellationToken));

                foreach (var dependency in DependencyRows(plannedRevision))
                    await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO MediaWikiImportDependency
                            (ImportBatchId, SourcePageId, SourceRevisionId, DependencyType, DependencyName, ResolutionStatus)
                        VALUES (@ImportBatchId, @SourcePageId, @SourceRevisionId, @DependencyType, @DependencyName, @ResolutionStatus);
                        """, new
                    {
                        ImportBatchId = batchId,
                        SourcePageId = pagePlan.SourcePageId,
                        SourceRevisionId = plannedRevision.SourceRevisionId,
                        DependencyType = dependency.Type,
                        DependencyName = dependency.Name,
                        ResolutionStatus = dependency.Status
                    }, transaction, cancellationToken: cancellationToken));
            }
        }

        if (sourcePageCount != plan.PlannedPageCount || sourceRevisionCount != plan.PlannedRevisionCount || importedPages.Count != plan.Pages.Count)
            throw new MediaWikiImportPlanStaleException("Source counts changed after planning.");

        foreach (var page in plan.Pages.Where(page => page.IsRedirect && page.RedirectTargetNavigation is not null))
        {
            if (!importedPages.TryGetValue(page.SourcePageId, out var sourceTargetPageId))
                throw new InvalidOperationException($"Redirect source page {page.SourcePageId} was not imported.");
            var targetPage = plan.Pages.FirstOrDefault(candidate => string.Equals(candidate.ProposedTargetNavigation, page.RedirectTargetNavigation, StringComparison.OrdinalIgnoreCase));
            if (targetPage is null || !importedPages.TryGetValue(targetPage.SourcePageId, out var redirectTargetPageId))
                throw new InvalidOperationException($"Redirect target for source page {page.SourcePageId} was not imported.");
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO PageReference (PageId, ReferencesPageName, ReferencesPageNavigation, ReferencesPageId)
                VALUES (@PageId, @ReferencesPageName, @ReferencesPageNavigation, @ReferencesPageId);
                """, new
            {
                PageId = sourceTargetPageId,
                ReferencesPageName = page.RedirectTargetSourceTitle ?? string.Empty,
                ReferencesPageNavigation = TwNamespaceNavigation.CleanAndValidate(page.RedirectTargetNavigation, lowerCase: false),
                ReferencesPageId = redirectTargetPageId
            }, transaction, cancellationToken: cancellationToken));
        }
    }

    private async Task ImportMediaAsync(DbConnection connection, DbTransaction transaction, MediaWikiImportPlan plan, long batchId, string manifestPath, string mediaRoot, CancellationToken cancellationToken)
    {
        var mediaPlan = plan.Media ?? throw new InvalidOperationException("A media plan is required for media import.");
        var manifest = MediaWikiMediaManifestReader.ReadAndValidate(manifestPath);
        if (!string.Equals(manifest.ManifestSha256, mediaPlan.ManifestSha256, StringComparison.OrdinalIgnoreCase)
            || manifest.ManifestByteLength != mediaPlan.ManifestByteLength)
            throw new MediaWikiImportPlanStaleException("The media manifest changed after planning.");
        if (!string.Equals(manifest.Manifest.SourceWikiIdentity, plan.Source.SourceWikiIdentity, StringComparison.Ordinal))
            throw new MediaWikiImportPlanStaleException("The media manifest source wiki identity does not match the page export.");

        var resolver = new MediaWikiMediaBinaryResolver(mediaRoot);
        var entries = manifest.Manifest.Entries.ToDictionary(entry => entry.SourceFilePageId, StringComparer.Ordinal);
        foreach (var planned in mediaPlan.Entries.OrderBy(entry => entry.SourceFilePageId, StringComparer.Ordinal))
        {
            if (!entries.TryGetValue(planned.SourceFilePageId, out var source))
                throw new MediaWikiImportPlanStaleException($"Media file-page {planned.SourceFilePageId} is absent from the validated manifest.");
            var page = await connection.QuerySingleOrDefaultAsync<ImportPageRow>(Command("SELECT SourcePageId, TargetPageId, TargetNavigation, OriginalTitle FROM MediaWikiImportPage WHERE ImportBatchId = @ImportBatchId AND SourcePageId = @SourcePageId;", new { ImportBatchId = batchId, SourcePageId = planned.SourceFilePageId }, transaction, cancellationToken));
            if (page is null) throw new MediaWikiImportPlanStaleException($"Media file-page {planned.SourceFilePageId} was not imported into the target.");
            var existing = await connection.ExecuteScalarAsync<int?>(Command("SELECT Id FROM PageFile WHERE PageId = @PageId AND Navigation = @Navigation LIMIT 1;", new { PageId = page.TargetPageId, Navigation = planned.TargetFileNavigation }, transaction, cancellationToken));
            if (existing.HasValue) throw new InvalidOperationException($"Target attachment collision for '{planned.OriginalFileName}' on page {page.TargetPageId}; existing attachments are never overwritten.");
            if (source.Revisions.Count != planned.Revisions.Count) throw new MediaWikiImportPlanStaleException($"Media revision count for '{planned.OriginalFileName}' changed after planning.");

            var firstTimestamp = source.Revisions[0].Timestamp?.UtcDateTime ?? source.OriginalTimestamp?.UtcDateTime ?? throw new InvalidDataException($"Media file '{source.OriginalFileName}' has no source timestamp.");
            var pageFileId = await connection.ExecuteScalarAsync<int>(Command("INSERT INTO PageFile (PageId, Name, Navigation, Revision, CreatedDate) VALUES (@PageId, @Name, @Navigation, @Revision, @CreatedDate); SELECT LAST_INSERT_ID();", new { PageId = page.TargetPageId, Name = source.OriginalFileName, Navigation = planned.TargetFileNavigation, Revision = source.Revisions.Count - 1, CreatedDate = firstTimestamp }, transaction, cancellationToken));
            await connection.ExecuteAsync(Command("INSERT INTO MediaWikiImportMedia (ImportBatchId, SourceFilePageId, SourceFileName, TargetPageId, TargetPageFileId, SourceSha1, OriginalMimeType, OriginalSize, OriginalSha256, ArchiveMetadata, Status) VALUES (@ImportBatchId, @SourceFilePageId, @SourceFileName, @TargetPageId, @TargetPageFileId, @SourceSha1, @OriginalMimeType, @OriginalSize, @OriginalSha256, @ArchiveMetadata, 'Imported');", new
            {
                ImportBatchId = batchId, SourceFilePageId = planned.SourceFilePageId, SourceFileName = source.OriginalFileName,
                TargetPageId = page.TargetPageId, TargetPageFileId = pageFileId, SourceSha1 = source.Sha1,
                OriginalMimeType = source.MimeType, OriginalSize = source.Size, OriginalSha256 = source.Sha256, ArchiveMetadata = source.ArchiveMetadata
            }, transaction, cancellationToken));
            foreach (var revision in source.Revisions.OrderBy(revision => revision.RevisionOrdinal))
            {
                var bytes = await resolver.ReadVerifiedAsync(revision.RelativeBinaryPath, revision.Size, revision.Sha256, cancellationToken, revision.MimeType ?? source.MimeType);
                var targetRevision = revision.RevisionOrdinal - 1;
                var createdDate = revision.Timestamp?.UtcDateTime ?? throw new InvalidDataException($"Media revision '{revision.SourceMediaRevisionId}' has no source timestamp.");
                await connection.ExecuteAsync(Command("INSERT INTO PageFileRevision (PageFileId, ContentType, Size, CreatedByUserId, CreatedDate, Data, Revision, DataHash) VALUES (@PageFileId, @ContentType, @Size, @Actor, @CreatedDate, @Data, @Revision, @DataHash);", new { PageFileId = pageFileId, ContentType = revision.MimeType ?? source.MimeType ?? "application/octet-stream", Size = bytes.LongLength, Actor = ImportActorUserId.ToString("D"), CreatedDate = createdDate, Data = bytes, Revision = targetRevision, DataHash = SecurityUtility.Crc32(bytes) }, transaction, cancellationToken));
                await connection.ExecuteAsync(Command("INSERT INTO MediaWikiImportMediaRevision (ImportBatchId, SourceFilePageId, SourceMediaRevisionId, TargetPageFileId, TargetFileRevision, SourceTimestamp, ContributorKind, ContributorSourceId, ContributorDisplayName, ContributorIpAddress, SourceSha1, MimeType, Size, Sha256, RelativeBinaryPath, ArchiveMetadata, Status) VALUES (@ImportBatchId, @SourceFilePageId, @SourceMediaRevisionId, @TargetPageFileId, @TargetFileRevision, @SourceTimestamp, @ContributorKind, @ContributorSourceId, @ContributorDisplayName, @ContributorIpAddress, @SourceSha1, @MimeType, @Size, @Sha256, @RelativeBinaryPath, @ArchiveMetadata, 'Imported');", new
                {
                    ImportBatchId = batchId, SourceFilePageId = planned.SourceFilePageId, SourceMediaRevisionId = revision.SourceMediaRevisionId,
                    TargetPageFileId = pageFileId, TargetFileRevision = targetRevision, SourceTimestamp = createdDate,
                    ContributorKind = revision.Uploader.Kind.ToString(), ContributorSourceId = revision.Uploader.SourceId,
                    ContributorDisplayName = revision.Uploader.DisplayName, ContributorIpAddress = revision.Uploader.IpAddress,
                    SourceSha1 = revision.Sha1, MimeType = revision.MimeType ?? source.MimeType, Size = bytes.LongLength,
                    Sha256 = revision.Sha256, RelativeBinaryPath = revision.RelativeBinaryPath, ArchiveMetadata = revision.ArchiveMetadata
                }, transaction, cancellationToken));
                var filePageRevision = revision.RevisionOrdinal;
                var hasPageRevision = await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*) FROM PageRevision WHERE PageId = @PageId AND Revision = @Revision;", new { PageId = page.TargetPageId, Revision = filePageRevision }, transaction, cancellationToken));
                if (hasPageRevision != 0)
                    await connection.ExecuteAsync(Command("INSERT INTO PageRevisionAttachment (PageId, PageFileId, FileRevision, PageRevision) VALUES (@PageId, @PageFileId, @FileRevision, @PageRevision);", new { PageId = page.TargetPageId, PageFileId = pageFileId, FileRevision = targetRevision, PageRevision = filePageRevision }, transaction, cancellationToken));
            }
        }

        foreach (var pagePlan in plan.Pages)
        {
            var targetPage = await connection.QuerySingleAsync<ImportPageRow>(Command("SELECT SourcePageId, TargetPageId, TargetNavigation, OriginalTitle FROM MediaWikiImportPage WHERE ImportBatchId = @ImportBatchId AND SourcePageId = @SourcePageId;", new { ImportBatchId = batchId, SourcePageId = pagePlan.SourcePageId }, transaction, cancellationToken));
            foreach (var revision in pagePlan.Revisions)
                foreach (var fileName in revision.Dependencies.Files)
                {
                    var media = mediaPlan.Entries.FirstOrDefault(entry => string.Equals(MediaWikiMediaManifestReader.NormalizeFileName(entry.OriginalFileName), MediaWikiMediaManifestReader.NormalizeFileName(fileName), StringComparison.OrdinalIgnoreCase));
                    var targetFile = media is null ? null : await connection.QuerySingleOrDefaultAsync<MediaFileRow>(Command("SELECT TargetPageFileId, SourceFilePageId FROM MediaWikiImportMedia WHERE ImportBatchId = @ImportBatchId AND SourceFilePageId = @SourceFilePageId;", new { ImportBatchId = batchId, SourceFilePageId = media.SourceFilePageId }, transaction, cancellationToken));
                    await connection.ExecuteAsync(Command("INSERT INTO MediaWikiImportMediaReference (ImportBatchId, SourcePageId, SourceRevisionId, SourceFileName, SourceFilePageId, TargetPageId, TargetPageRevision, TargetPageFileId, ResolutionStatus) VALUES (@ImportBatchId, @SourcePageId, @SourceRevisionId, @SourceFileName, @SourceFilePageId, @TargetPageId, @TargetPageRevision, @TargetPageFileId, @ResolutionStatus);", new
                    {
                        ImportBatchId = batchId, SourcePageId = pagePlan.SourcePageId, SourceRevisionId = revision.SourceRevisionId,
                        SourceFileName = fileName, SourceFilePageId = media?.SourceFilePageId, TargetPageId = targetPage.TargetPageId,
                        TargetPageRevision = revision.RevisionOrdinal, TargetPageFileId = targetFile?.TargetPageFileId,
                        ResolutionStatus = targetFile is null ? "Unresolved" : "Resolved"
                    }, transaction, cancellationToken));
                    if (targetFile is not null)
                        await connection.ExecuteAsync(Command("UPDATE MediaWikiImportDependency SET ResolutionStatus = 'Resolved' WHERE ImportBatchId = @ImportBatchId AND SourcePageId = @SourcePageId AND SourceRevisionId = @SourceRevisionId AND DependencyType = 'File' AND DependencyName = @DependencyName;", new { ImportBatchId = batchId, SourcePageId = pagePlan.SourcePageId, SourceRevisionId = revision.SourceRevisionId, DependencyName = fileName }, transaction, cancellationToken));
                }
        }
    }

    private async Task<MediaWikiReconciliationReport> ReconcileCoreAsync(DbConnection connection, DbTransaction? transaction, MediaWikiImportPlan plan, long batchId, CancellationToken cancellationToken)
    {
        var differences = new List<MediaWikiReconciliationDifference>();
        var importedPageCount = await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*) FROM MediaWikiImportPage WHERE ImportBatchId = @ImportBatchId;", new { ImportBatchId = batchId }, transaction, cancellationToken));
        var importedRevisionCount = await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*) FROM MediaWikiImportRevision WHERE ImportBatchId = @ImportBatchId;", new { ImportBatchId = batchId }, transaction, cancellationToken));
        foreach (var pagePlan in plan.Pages.OrderBy(page => page.SourcePageId, StringComparer.Ordinal))
        {
            var page = await connection.QuerySingleOrDefaultAsync<ImportPageRow>(Command("SELECT SourcePageId, TargetPageId, TargetNavigation, OriginalTitle FROM MediaWikiImportPage WHERE ImportBatchId = @ImportBatchId AND SourcePageId = @SourcePageId;", new { ImportBatchId = batchId, pagePlan.SourcePageId }, transaction, cancellationToken));
            if (page is null)
            {
                differences.Add(new("MissingPageMapping", "No target page mapping was committed.", pagePlan.SourcePageId));
                continue;
            }
            if (!string.Equals(page.OriginalTitle, pagePlan.OriginalTitle, StringComparison.Ordinal) || page.TargetPageId <= 0)
                differences.Add(new("PageProvenanceMismatch", "Source title or target page identity does not match the plan.", pagePlan.SourcePageId));
            var target = await connection.QuerySingleOrDefaultAsync<TargetPageRow>(Command("""
                SELECT P.Id, P.Revision, P.Navigation, R.Body, R.ModifiedDate
                FROM Page P JOIN PageRevision R ON R.PageId = P.Id AND R.Revision = P.Revision
                WHERE P.Id = @TargetPageId;
                """, new { page.TargetPageId }, transaction, cancellationToken));
            if (target is null)
            {
                differences.Add(new("MissingTargetPage", "The mapped target page is missing.", pagePlan.SourcePageId));
                continue;
            }
            var expectedNavigation = TwNamespaceNavigation.CleanAndValidate(pagePlan.ProposedTargetNavigation!, lowerCase: false);
            if (!string.Equals(target.Navigation, expectedNavigation, StringComparison.Ordinal))
                differences.Add(new("NavigationMismatch", "The target navigation does not match the deterministic mapping.", pagePlan.SourcePageId));
            foreach (var revisionPlan in pagePlan.Revisions)
            {
                var revision = await connection.QuerySingleOrDefaultAsync<TargetRevisionRow>(Command("""
                    SELECT IR.SourceRevisionId, IR.TargetRevision, IR.WikitextSha256, PR.Body, PR.ModifiedDate,
                           IR.ContributorKind, IR.ContributorSourceId, IR.ContributorDisplayName, IR.ContributorIpAddress
                    FROM MediaWikiImportRevision IR
                    JOIN PageRevision PR ON PR.PageId = IR.TargetPageId AND PR.Revision = IR.TargetRevision
                    WHERE IR.ImportBatchId = @ImportBatchId AND IR.SourcePageId = @SourcePageId AND IR.SourceRevisionId = @SourceRevisionId;
                    """, new { ImportBatchId = batchId, pagePlan.SourcePageId, revisionPlan.SourceRevisionId }, transaction, cancellationToken));
                if (revision is null)
                {
                    differences.Add(new("MissingRevisionMapping", "No target revision mapping was committed.", pagePlan.SourcePageId, revisionPlan.SourceRevisionId));
                    continue;
                }
                var hash = Hashing.Sha256(Encoding.UTF8.GetBytes(revision.Body));
                if (!string.Equals(hash, revisionPlan.WikitextSha256, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(hash, revision.WikitextSha256, StringComparison.OrdinalIgnoreCase)
                    || revision.TargetRevision != revisionPlan.RevisionOrdinal)
                    differences.Add(new("WikitextOrRevisionMismatch", "Target revision ordering or Wikitext hash differs from the source plan.", pagePlan.SourcePageId, revisionPlan.SourceRevisionId));
                if (revisionPlan.Timestamp is not null && revision.ModifiedDate != revisionPlan.Timestamp.Value.UtcDateTime)
                    differences.Add(new("TimestampMismatch", "Target revision timestamp differs from the source timestamp.", pagePlan.SourcePageId, revisionPlan.SourceRevisionId));
                if (!string.Equals(revision.ContributorKind, revisionPlan.Contributor.Kind.ToString(), StringComparison.Ordinal)
                    || !string.Equals(revision.ContributorSourceId, revisionPlan.Contributor.SourceId, StringComparison.Ordinal)
                    || !string.Equals(revision.ContributorDisplayName, revisionPlan.Contributor.DisplayName, StringComparison.Ordinal)
                    || !string.Equals(revision.ContributorIpAddress, revisionPlan.Contributor.IpAddress, StringComparison.Ordinal))
                    differences.Add(new("ContributorMismatch", "Contributor provenance differs from the source plan.", pagePlan.SourcePageId, revisionPlan.SourceRevisionId));
            }
        }

        var sourceMediaCount = plan.Media?.Entries.Count ?? 0;
        var sourceMediaRevisionCount = plan.Media?.Entries.Sum(entry => entry.Revisions.Count) ?? 0;
        var importedMediaCount = 0;
        var importedMediaRevisionCount = 0;
        if (plan.Media is not null)
        {
            importedMediaCount = await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*) FROM MediaWikiImportMedia WHERE ImportBatchId = @ImportBatchId;", new { ImportBatchId = batchId }, transaction, cancellationToken));
            importedMediaRevisionCount = await connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*) FROM MediaWikiImportMediaRevision WHERE ImportBatchId = @ImportBatchId;", new { ImportBatchId = batchId }, transaction, cancellationToken));
            foreach (var media in plan.Media.Entries.OrderBy(entry => entry.SourceFilePageId, StringComparer.Ordinal))
            {
                var imported = await connection.QuerySingleOrDefaultAsync<ImportedMediaRow>(Command("SELECT M.TargetPageFileId, M.SourceFileName, M.OriginalSha256, PFR.Sha256 FROM MediaWikiImportMedia M LEFT JOIN (SELECT R.ImportBatchId, R.SourceFilePageId, R.Sha256 FROM MediaWikiImportMediaRevision R WHERE R.TargetFileRevision = (SELECT MAX(R2.TargetFileRevision) FROM MediaWikiImportMediaRevision R2 WHERE R2.ImportBatchId = R.ImportBatchId AND R2.SourceFilePageId = R.SourceFilePageId)) PFR ON PFR.ImportBatchId = M.ImportBatchId AND PFR.SourceFilePageId = M.SourceFilePageId WHERE M.ImportBatchId = @ImportBatchId AND M.SourceFilePageId = @SourceFilePageId;", new { ImportBatchId = batchId, SourceFilePageId = media.SourceFilePageId }, transaction, cancellationToken));
                if (imported is null)
                {
                    differences.Add(new("MissingMediaMapping", "No target attachment mapping was committed.", media.SourceFilePageId));
                    continue;
                }
                if (!string.Equals(imported.SourceFileName, media.OriginalFileName, StringComparison.Ordinal) || !string.Equals(imported.OriginalSha256, media.Sha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(imported.Sha256, media.Revisions[^1].Sha256, StringComparison.OrdinalIgnoreCase))
                    differences.Add(new("MediaMetadataMismatch", "Media filename or source checksum provenance differs from the media plan.", media.SourceFilePageId));
                for (var index = 0; index < media.Revisions.Count; index++)
                {
                    var revision = await connection.QuerySingleOrDefaultAsync<ImportedMediaRevisionRow>(Command("SELECT R.SourceMediaRevisionId, R.TargetFileRevision, R.Sha256, R.Size, LOWER(SHA2(PFR.Data, 256)) AS ActualSha256, PFR.CreatedDate FROM MediaWikiImportMediaRevision R JOIN PageFileRevision PFR ON PFR.PageFileId = R.TargetPageFileId AND PFR.Revision = R.TargetFileRevision WHERE R.ImportBatchId = @ImportBatchId AND R.SourceFilePageId = @SourceFilePageId AND R.TargetFileRevision = @TargetFileRevision;", new { ImportBatchId = batchId, SourceFilePageId = media.SourceFilePageId, TargetFileRevision = index }, transaction, cancellationToken));
                    if (revision is null || !string.Equals(revision.SourceMediaRevisionId, media.Revisions[index].SourceMediaRevisionId, StringComparison.Ordinal) || !string.Equals(revision.Sha256, media.Revisions[index].Sha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(revision.ActualSha256, media.Revisions[index].Sha256, StringComparison.OrdinalIgnoreCase) || revision.Size != media.Revisions[index].Size)
                        differences.Add(new("MediaBinaryMismatch", "Imported media revision metadata or binary SHA-256 differs from the source manifest.", media.SourceFilePageId, media.Revisions[index].SourceMediaRevisionId));
                }
            }
        }

        differences = differences.OrderBy(difference => difference.Code, StringComparer.Ordinal)
            .ThenBy(difference => difference.SourcePageId, StringComparer.Ordinal)
            .ThenBy(difference => difference.SourceRevisionId, StringComparer.Ordinal).ToList();
        var report = new MediaWikiReconciliationReport(batchId, differences.Count == 0 ? "VERIFIED" : "FAILED",
            plan.PlannedPageCount, importedPageCount, plan.PlannedRevisionCount, importedRevisionCount, differences, string.Empty)
        {
            SourceMediaCount = sourceMediaCount,
            ImportedMediaCount = importedMediaCount,
            SourceMediaRevisionCount = sourceMediaRevisionCount,
            ImportedMediaRevisionCount = importedMediaRevisionCount
        };
        var stableJson = JsonSerializer.Serialize(report, JsonOptions());
        return report with { DeterministicHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableJson))).ToLowerInvariant() };
    }

    private async Task MarkBatchRolledBackAsync(long batchId, Exception exception)
    {
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(CancellationToken.None);
            await session.Connection.ExecuteAsync(new CommandDefinition("UPDATE MediaWikiImportBatch SET Status = 'RolledBack', CompletedAtUtc = UTC_TIMESTAMP(6), DiagnosticsJson = @DiagnosticsJson WHERE ImportBatchId = @ImportBatchId AND Status = 'Running';", new { ImportBatchId = batchId, DiagnosticsJson = JsonSerializer.Serialize(new[] { exception.GetBaseException().Message }, JsonOptions()) }, cancellationToken: CancellationToken.None));
        }
        catch
        {
            // Preserve the original import exception; an unavailable diagnostic update
            // must not make a rollback appear successful.
        }
    }

    private static IEnumerable<(string Type, string Name, string Status)> DependencyRows(PlannedRevision revision)
    {
        foreach (var value in revision.Dependencies.Templates) yield return ("Template", value, FindingStatus(revision, "MissingTemplateDependency", value) ? "Missing" : "InventoryOnly");
        foreach (var value in revision.Dependencies.ParserFunctions) yield return ("ParserFunction", value, "Unresolved");
        foreach (var value in revision.Dependencies.Modules) yield return ("Module", value, FindingStatus(revision, "MissingModuleDependency", value) ? "Missing" : "InventoryOnly");
        foreach (var value in revision.Dependencies.Categories) yield return ("Category", value, "Unresolved");
        foreach (var value in revision.Dependencies.InternalLinks) yield return ("InternalLink", value, "Unresolved");
        foreach (var value in revision.Dependencies.Files) yield return ("File", value, "Unresolved");
        for (var index = 1; index <= revision.Dependencies.ReferenceTagCount; index++) yield return ("Reference", $"ref#{index}", "Unresolved");
        foreach (var value in revision.Dependencies.ExtensionTags) yield return ("ExtensionTag", value, "Unsupported");
    }

    private static bool FindingStatus(PlannedRevision revision, string code, string value)
        => revision.Findings.Any(finding => finding.Code == code && finding.Message.Contains(value, StringComparison.Ordinal));

    private static CommandDefinition Command(string sql, object parameters, DbTransaction? transaction, CancellationToken cancellationToken)
        => new(sql, parameters, transaction, cancellationToken: cancellationToken);

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class BatchStatusRow
    {
        public long ImportBatchId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? DiagnosticsJson { get; set; }
    }

    private sealed class ImportPageRow
    {
        public string SourcePageId { get; set; } = string.Empty;
        public int TargetPageId { get; set; }
        public string TargetNavigation { get; set; } = string.Empty;
        public string OriginalTitle { get; set; } = string.Empty;
    }

    private sealed class TargetPageRow
    {
        public int Id { get; set; }
        public int Revision { get; set; }
        public string Navigation { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime ModifiedDate { get; set; }
    }

    private sealed class TargetRevisionRow
    {
        public string SourceRevisionId { get; set; } = string.Empty;
        public int TargetRevision { get; set; }
        public string WikitextSha256 { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime ModifiedDate { get; set; }
        public string ContributorKind { get; set; } = string.Empty;
        public string? ContributorSourceId { get; set; }
        public string? ContributorDisplayName { get; set; }
        public string? ContributorIpAddress { get; set; }
    }

    private sealed class MediaFileRow
    {
        public int TargetPageFileId { get; set; }
        public string SourceFilePageId { get; set; } = string.Empty;
    }

    private sealed class ImportedMediaRow
    {
        public int TargetPageFileId { get; set; }
        public string SourceFileName { get; set; } = string.Empty;
        public string OriginalSha256 { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class ImportedMediaRevisionRow
    {
        public string SourceMediaRevisionId { get; set; } = string.Empty;
        public int TargetFileRevision { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public long Size { get; set; }
        public string ActualSha256 { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }
    }

    private static class Hashing
    {
        public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
