using Dapper;
using System.Text.Json;
using MediaWikiImport;
using MySqlConnector;
using TightWiki.Engine.MediaWiki;
using TightWiki.Library;
using TightWiki.Plugin;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

public sealed class MediaWikiReferenceOutputIntegrationTests
{
    [Fact]
    public async Task GenerateContinuumOutputsForCapturedRealMediaWikiReferences()
    {
        // This test is executed by the disposable diagnostic harness. Keep a
        // normal repository-wide test run independent of that short-lived
        // database, while retaining strict validation whenever the harness
        // supplies its complete environment.
        var connectionString = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M3_006C_OUTPUT_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var batchText = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M3_006C_IMPORT_BATCH_ID");
        if (!long.TryParse(batchText, out var batchId) || batchId <= 0)
            throw new InvalidOperationException("CONTINUUMWIKI_M3_006C_IMPORT_BATCH_ID must identify the verified disposable import batch.");
        var socketPath = Environment.GetEnvironmentVariable("CW_SCRIBUNTO_SOCKET_PATH");
        if (string.IsNullOrWhiteSpace(socketPath) || !File.Exists(socketPath) && !File.Exists(Path.GetFullPath(socketPath)))
            throw new InvalidOperationException("CW_SCRIBUNTO_SOCKET_PATH must identify the authorized Unix-domain worker socket.");
        var referenceDirectory = Environment.GetEnvironmentVariable("CW_M3_006C_REFERENCE_DIR")
            ?? throw new InvalidOperationException("CW_M3_006C_REFERENCE_DIR is missing.");
        var outputDirectory = Environment.GetEnvironmentVariable("CW_M3_006C_OUTPUT_DIR")
            ?? throw new InvalidOperationException("CW_M3_006C_OUTPUT_DIR is missing.");
        var corpusPath = Environment.GetEnvironmentVariable("CW_M3_006C_CORPUS")
            ?? throw new InvalidOperationException("CW_M3_006C_CORPUS is missing.");
        using var corpusDocument = JsonDocument.Parse(await File.ReadAllTextAsync(corpusPath));
        var sourcePageIds = corpusDocument.RootElement.GetProperty("Cases").EnumerateArray()
            .ToDictionary(value => value.GetProperty("FixtureId").GetString()!, value => value.GetProperty("SourcePageId").GetString()!, StringComparer.Ordinal);
        Directory.CreateDirectory(outputDirectory);

        var allowlist = (Environment.GetEnvironmentVariable("CW_M3_006C_FIXTURE_IDS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var references = Directory.EnumerateFiles(referenceDirectory, "mw-invoke-*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => JsonSerializer.Deserialize<ReferenceCapture>(File.ReadAllText(path), JsonOptions())
                ?? throw new InvalidDataException($"Reference capture '{path}' was empty."))
            .Where(value => allowlist.Count == 0 || allowlist.Contains(value.FixtureId))
            .ToArray();
        if (references.Length == 0)
            throw new InvalidDataException("The selected reference fixture set is empty.");
        if (references.Any(value => string.IsNullOrWhiteSpace(value.FixtureId)
            || string.IsNullOrWhiteSpace(value.RenderedHtml)
            || value.RequestedRevisionId is null
            || value.RenderedRevisionId != value.RequestedRevisionId
            || string.IsNullOrWhiteSpace(value.RawApiResponseSha256)))
            throw new InvalidDataException("Every reference capture must have matching requested/rendered revision IDs, HTML, and a raw response hash.");

        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        var repository = new MySqlMediaWikiImportRepository(factory);
        var renderer = new MediaWikiWikitextRenderer(repository, new TwConfiguration { BasePath = "/wiki" }, new MediaWikiWikitextRenderOptions
        {
            MaxExpansionOperations = 100_000,
            MaxExpansionOutputCharacters = 100_000,
            ScribuntoExecutor = new MediaWikiScribuntoSocketExecutor(new MediaWikiScribuntoUnixSocketClient(socketPath, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)))
        });

        await using var session = await factory.OpenSessionAsync();
        foreach (var reference in references)
        {
            if (!sourcePageIds.TryGetValue(reference.FixtureId, out var sourcePageId))
                throw new InvalidDataException($"Fixture {reference.FixtureId} is not present in the compatibility corpus.");
            var source = await session.Connection.QuerySingleOrDefaultAsync<ImportedRevision>(new CommandDefinition("""
                SELECT R.TargetPageId, R.TargetRevision, R.SourcePageId, R.SourceRevisionId,
                       R.SourceTimestamp, R.WikitextSha256, PR.Body
                FROM MediaWikiImportRevision R
                INNER JOIN PageRevision PR ON PR.PageId = R.TargetPageId AND PR.Revision = R.TargetRevision
                WHERE R.ImportBatchId = @ImportBatchId
                  AND R.SourcePageId = @SourcePageId
                  AND R.SourceRevisionId = @SourceRevisionId
                LIMIT 1;
                """, new
            {
                ImportBatchId = batchId,
                SourcePageId = sourcePageId,
                SourceRevisionId = reference.RequestedRevisionId.ToString()
            }));
            if (source is null)
                throw new InvalidDataException($"Imported revision {reference.RequestedRevisionId} for source page {sourcePageId} was not found in batch {batchId}.");
            if (!source.Body.Contains(reference.OriginalInvocation, StringComparison.Ordinal))
                throw new InvalidDataException($"Fixture {reference.FixtureId} invocation was not present in the imported source revision.");

            var provenance = await repository.GetRenderSourceAsync(
                checked((int)source.TargetPageId), checked((int)source.TargetRevision));
            if (provenance.ImportBatchId != batchId || provenance.SourceRevisionId != source.SourceRevisionId)
                throw new InvalidDataException($"Revision provenance mismatch for fixture {reference.FixtureId}.");
            var result = await renderer.RenderAsync(source.Body, provenance);
            var output = new ContinuumOutput(
                "cw-m3-006c.4-continuum-output-v1",
                reference.FixtureId,
                reference.PageTitle,
                reference.RequestedRevisionId,
                reference.RenderedRevisionId,
                reference.OriginalInvocation,
                source.SourcePageId,
                source.SourceRevisionId,
                source.SourceTimestamp,
                source.WikitextSha256,
                provenance,
                result.HtmlResult,
                result.Diagnostics.Select(value => new Diagnostic(value.Code, value.Message, value.Severity.ToString())).ToArray(),
                result.ScribuntoDependencies ?? [],
                Environment.GetEnvironmentVariable("CW_SCRIBUNTO_WORKER_PATH") ?? "socket-activated-deployed-worker",
                socketPath,
                batchId);
            var outputPath = Path.Combine(outputDirectory, reference.FixtureId + ".json");
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(output, JsonOptions(indented: true)) + Environment.NewLine);
        }
    }

    private static JsonSerializerOptions JsonOptions(bool indented = false) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = indented,
        PropertyNamingPolicy = null
    };

    private sealed record ReferenceCapture(string FixtureId, string PageTitle, string? SourcePageId, long? RequestedRevisionId,
        long? RenderedRevisionId, string OriginalInvocation, string RenderedHtml, string RawApiResponseSha256);

    // MySQL unsigned integer columns are materialized by the connector as UInt32.
    // Keep the DTO aligned with that provider type, then narrow only at the repository API boundary.
    private sealed record ImportedRevision(uint TargetPageId, uint TargetRevision, string SourcePageId,
        string SourceRevisionId, DateTime SourceTimestamp, string WikitextSha256, string Body);

    private sealed record Diagnostic(string Code, string Message, string Severity);

    private sealed record ContinuumOutput(string ReportVersion, string FixtureId, string PageTitle,
        long? RequestedRevisionId, long? RenderedRevisionId, string OriginalInvocation, string SourcePageId,
        string SourceRevisionId, DateTime SourceTimestamp, string WikitextSha256, TwMediaWikiRenderSource Provenance,
        string RenderedHtml, IReadOnlyList<Diagnostic> Diagnostics,
        IReadOnlyList<MediaWikiScribuntoDependency> ScribuntoDependencies,
        string WorkerAssembly, string SocketPath, long ImportBatchId);
}
