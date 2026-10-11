using TightWiki.Engine.MediaWiki;
using TightWiki.Library.Database;
using TightWiki.Repository.Helpers;
using NTDLS.SqliteDapperWrapper;
using System.Text.Json;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiCargoBoundaryTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Allowlist =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Variants"] = new HashSet<string>(["Name", "Species", "GeneticAncestor"], StringComparer.Ordinal)
        };

    private static MediaWikiCargoQueryRequest Request(
        string table = "Variants",
        string field = "Name",
        string? dataset = "cargo-snapshot-1")
        => new(
            new("wiki|https://example.invalid|MediaWiki 1.45.3", "34", "9603", dataset),
            new(table),
            [new(field)],
            [],
            [],
            0,
            10,
            new());

    [Fact]
    public void ValidHistoricalQueryPassesAllowlistValidation()
        => Assert.Empty(MediaWikiCargoQueryValidator.Validate(Request(), Allowlist));

    [Fact]
    public void UnknownTableAndFieldAreRejected()
    {
        var diagnostics = MediaWikiCargoQueryValidator.Validate(Request("Unknown"), Allowlist);
        Assert.Contains(diagnostics, value => value.Failure == MediaWikiCargoProviderFailure.InvalidQuery);
    }

    [Fact]
    public void MissingDatasetProvenanceIsRejected()
    {
        var diagnostics = MediaWikiCargoQueryValidator.Validate(Request(dataset: null), Allowlist);
        Assert.Contains(diagnostics, value => value.Failure == MediaWikiCargoProviderFailure.HistoricalProvenanceUnavailable);
    }

    [Fact]
    public void OversizedPaginationIsRejected()
    {
        var request = Request() with { Limit = 257 };
        var diagnostics = MediaWikiCargoQueryValidator.Validate(request, Allowlist);
        Assert.Contains(diagnostics, value => value.Failure == MediaWikiCargoProviderFailure.BudgetExceeded);
    }

    [Fact]
    public async Task DefaultProviderRejectsWithoutInventingRows()
    {
        var result = await new RejectingMediaWikiCargoQueryProvider().QueryAsync(Request());
        Assert.False(result.Succeeded);
        Assert.Null(result.Result);
        Assert.Equal(MediaWikiCargoProviderFailure.ProviderUnavailable, result.Diagnostic!.Failure);
    }

    [Fact]
    public async Task SchemaVerifiedProviderRejectsHelperFieldsUntilTypedJoinExists()
    {
        var provider = new SchemaVerifiedMediaWikiCargoQueryProvider(
            new ThrowingConnectionFactory(),
            "cargo-current-1",
            "wiki|https://example.invalid|MediaWiki 1.45.3");
        var request = Request("Divines", "Name", "cargo-current-1") with
        {
            Fields = [new MediaWikiCargoFieldReference("Spouses")]
        };

        var result = await provider.QueryAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal(MediaWikiCargoProviderFailure.UnsupportedQuery, result.Diagnostic!.Failure);
    }

    [Fact]
    public async Task SchemaVerifiedProviderReadsOnlyAllowlistedScalarRowsWithBounds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cargo-provider-{Guid.NewGuid():N}.db");
        try
        {
            await using var setup = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            await setup.OpenAsync();
            await using (var command = setup.CreateCommand())
            {
                command.CommandText = "CREATE TABLE cargo__Variants (Name TEXT, Species TEXT, Origin TEXT); INSERT INTO cargo__Variants (Name, Species, Origin) VALUES ('Aurora', 'Human', 'North');";
                await command.ExecuteNonQueryAsync();
            }

            var provider = new SchemaVerifiedMediaWikiCargoQueryProvider(
                new SqliteDatabaseConnectionFactory(new SqliteManagedFactory($"Data Source={path}")),
                "cargo-current-1",
                "wiki|https://example.invalid|MediaWiki 1.45.3");
            var result = await provider.QueryAsync(Request(dataset: "cargo-current-1") with
            {
                Fields = [new("Name"), new("Species")],
                Limit = 1
            });

            Assert.True(result.Succeeded);
            Assert.Equal("Aurora", result.Result!.Rows[0][0].Value);
            Assert.Equal("Human", result.Result.Rows[0][1].Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifiedCurrentSnapshotProviderReturnsAuthenticSnapshotValueWhenEvidenceIsPresent()
    {
        var snapshot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "continuumwiki-validation/cargo-snapshot-authentic-20261011-1");
        if (!Directory.Exists(snapshot)) return;
        var provider = await MediaWikiCargoSnapshotQueryProvider.OpenAuthorizedAsync(
            snapshot,
            new(
                "cargo-authentic-current-20261011-1",
                "continuum_universes_wiki|127.0.0.1:13306|cw_cargo_audit",
                "Continuum Universes MediaWiki",
                "ade3431abf2cc321e333eac35c858d25f8c97a52e0379436aa52917cda1ebc4f",
                "952e50120fa88ed06bb163209afadfed99ec867a8d6dd69de5ea113c166beaeb",
                true));
        var firstLine = (await File.ReadAllLinesAsync(Path.Combine(snapshot, "Variants.jsonl")))[0];
        using var row = JsonDocument.Parse(firstLine);
        var expected = row.RootElement.GetProperty("Name").GetString();
        var result = await provider.QueryAsync(new(
            new("Continuum Universes MediaWiki", "current-state", "current-state", "cargo-authentic-current-20261011-1"),
            new("Variants"), [new("Name")], [], [], 0, 1, new()));

        Assert.True(result.Succeeded, result.Diagnostic?.Message);
        Assert.Equal(expected, result.Result!.Rows[0][0].Value);
    }

    [Fact]
    public async Task CurrentSnapshotProviderRejectsHistoricalIdentity()
    {
        var snapshot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "continuumwiki-validation/cargo-snapshot-authentic-20261011-1");
        if (!Directory.Exists(snapshot)) return;
        var provider = await MediaWikiCargoSnapshotQueryProvider.OpenAuthorizedAsync(snapshot, new(
            "cargo-authentic-current-20261011-1", "continuum_universes_wiki|127.0.0.1:13306|cw_cargo_audit",
            "Continuum Universes MediaWiki", "ade3431abf2cc321e333eac35c858d25f8c97a52e0379436aa52917cda1ebc4f",
            "952e50120fa88ed06bb163209afadfed99ec867a8d6dd69de5ea113c166beaeb", true));
        var result = await provider.QueryAsync(new(
            new("Continuum Universes MediaWiki", "34", "9907", "cargo-authentic-current-20261011-1"),
            new("Variants"), [new("Name")], [], [], 0, 1, new()));
        Assert.False(result.Succeeded);
        Assert.Equal(MediaWikiCargoProviderFailure.HistoricalProvenanceUnavailable, result.Diagnostic!.Failure);
    }

    [Fact]
    public void RelationshipCatalogPreservesSchemaOnlyEvidenceAndLeavesDivineChildrenUnsupported()
    {
        var catalog = MediaWikiCargoRelationshipCatalog.Default;
        Assert.True(catalog.TryGet("Divines.Spouses", out var spouses));
        Assert.Equal(MediaWikiCargoRelationshipReadiness.SchemaOnly, spouses.Readiness);
        Assert.Equal("cargo__Divines__Spouses", spouses.HelperTable);
        Assert.Equal("_rowID", spouses.ParentKeyColumn);
        Assert.Equal("_value", spouses.ValueColumn);
        Assert.Equal("_position", spouses.OrderColumn);
        Assert.False(catalog.TryGet("Divines.Children", out _));
    }

    [Fact]
    public void SyntheticRelationshipProjectorProvidesStableBoundedRowsWithoutAuthenticClaim()
    {
        var definition = MediaWikiCargoRelationshipCatalog.Default.Definitions.Single(value => value.LogicalName == "Divines.Spouses");
        var request = RelationshipRequest("Divines.Spouses") with
        {
            Projections = [MediaWikiCargoRelationshipProjection.Value, MediaWikiCargoRelationshipProjection.Position],
            Offset = 1,
            Limit = 2
        };
        var provenance = SyntheticRelationshipProvenance(request.Identity.DatasetIdentity!, request.Identity.SourceWikiIdentity);
        var result = MediaWikiCargoRelationshipProjector.ProjectSynthetic(request, definition,
        [
            new("Aurora", 2, "Boreas"),
            new("Aurora", 1, "Aster"),
            new("Aurora", 3, null),
            new("Other", 1, "Ignored")
        ], provenance);

        Assert.Equal("Divines.Spouses", result.Relationship);
        Assert.Equal(MediaWikiCargoRelationshipDatasetClassification.Synthetic, result.DatasetProvenance.Classification);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Boreas", result.Rows[0]["value"]);
        Assert.Equal("2", result.Rows[0]["position"]);
        Assert.Null(result.Rows[1]["value"]);
        Assert.Equal("3", result.Rows[1]["position"]);
    }

    [Fact]
    public void SyntheticRelationshipProjectorSupportsEmptyResultsAndValueFiltering()
    {
        var definition = MediaWikiCargoRelationshipCatalog.Default.Definitions.Single(value => value.LogicalName == "Divines.Spouses");
        var request = RelationshipRequest("Divines.Spouses") with { ValueEquals = "NoSuchValue", Limit = 1 };
        var result = MediaWikiCargoRelationshipProjector.ProjectSynthetic(request, definition,
            [new("Aurora", 1, "Aster")],
            SyntheticRelationshipProvenance(request.Identity.DatasetIdentity!, request.Identity.SourceWikiIdentity));
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void SyntheticRelationshipProjectorRejectsProvenanceMismatchAndOutputOverflow()
    {
        var definition = MediaWikiCargoRelationshipCatalog.Default.Definitions.Single(value => value.LogicalName == "Divines.Spouses");
        var request = RelationshipRequest("Divines.Spouses") with
        {
            Budget = new(MaxOutputCharacters: 1)
        };
        Assert.Throws<InvalidDataException>(() => MediaWikiCargoRelationshipProjector.ProjectSynthetic(request, definition,
            [new("Aurora", 1, "Aster")],
            SyntheticRelationshipProvenance("different-dataset", request.Identity.SourceWikiIdentity)));

        var validProvenance = SyntheticRelationshipProvenance(request.Identity.DatasetIdentity!, request.Identity.SourceWikiIdentity);
        Assert.Throws<InvalidDataException>(() => MediaWikiCargoRelationshipProjector.ProjectSynthetic(request, definition,
            [new("Aurora", 1, "Aster")], validProvenance));
    }

    [Fact]
    public async Task RelationshipProviderRejectsUnverifiedHelperWithoutOpeningDatabase()
    {
        var provider = new SchemaVerifiedMediaWikiCargoQueryProvider(
            new ThrowingConnectionFactory(),
            "cargo-authentic-current-20261011-1",
            "Continuum Universes MediaWiki",
            relationshipSchemaIdentity: MediaWikiCargoRelationshipCatalog.AuthorizedCurrentSchema);
        var result = await provider.QueryRelationshipAsync(RelationshipRequest(
            "Divines.Spouses",
            dataset: "cargo-authentic-current-20261011-1",
            sourceWiki: "Continuum Universes MediaWiki",
            sourcePage: "current-state",
            sourceRevision: "current-state",
            classification: MediaWikiCargoRelationshipDatasetClassification.AuthenticSourceCurrentState));
        Assert.False(result.Succeeded);
        Assert.Equal(MediaWikiCargoProviderFailure.RelationshipUnverified, result.Diagnostic!.Failure);
    }

    [Fact]
    public async Task RelationshipProviderRejectsDivineChildrenWithoutOpeningDatabase()
    {
        var provider = new SchemaVerifiedMediaWikiCargoQueryProvider(
            new ThrowingConnectionFactory(),
            "cargo-authentic-current-20261011-1",
            "Continuum Universes MediaWiki",
            relationshipSchemaIdentity: MediaWikiCargoRelationshipCatalog.AuthorizedCurrentSchema);
        var result = await provider.QueryRelationshipAsync(RelationshipRequest(
            "Divines.Children",
            dataset: "cargo-authentic-current-20261011-1",
            sourceWiki: "Continuum Universes MediaWiki",
            sourcePage: "current-state",
            sourceRevision: "current-state",
            classification: MediaWikiCargoRelationshipDatasetClassification.AuthenticSourceCurrentState));
        Assert.False(result.Succeeded);
        Assert.Equal(MediaWikiCargoProviderFailure.UnsupportedQuery, result.Diagnostic!.Failure);
    }

    [Fact]
    public void RelationshipValidationRejectsSchemaMismatchAndHistoricalIdentity()
    {
        var request = RelationshipRequest("Divines.Spouses", classification: MediaWikiCargoRelationshipDatasetClassification.Historical) with
        {
            SchemaIdentity = new(new string('0', 64), new string('1', 64))
        };
        var diagnostics = MediaWikiCargoRelationshipQueryValidator.Validate(
            request, MediaWikiCargoRelationshipCatalog.Default,
            MediaWikiCargoRelationshipCatalog.AuthorizedCurrentSchema, allowSyntheticDataset: false);
        Assert.Contains(diagnostics, value => value.Failure == MediaWikiCargoProviderFailure.SchemaMismatch);
        Assert.Contains(diagnostics, value => value.Failure == MediaWikiCargoProviderFailure.HistoricalProvenanceUnavailable);
    }

    [Fact]
    public void SyntheticRelationshipProjectorRejectsInvalidParentAndFanOut()
    {
        var definition = MediaWikiCargoRelationshipCatalog.Default.Definitions.Single(value => value.LogicalName == "Divines.Spouses");
        var request = RelationshipRequest("Divines.Spouses");
        var provenance = SyntheticRelationshipProvenance(request.Identity.DatasetIdentity!, request.Identity.SourceWikiIdentity);
        Assert.Throws<InvalidDataException>(() => MediaWikiCargoRelationshipProjector.ProjectSynthetic(
            request with { ParentValue = string.Empty }, definition, [], provenance));
        var rows = Enumerable.Range(0, definition.MaxFanOut + 1)
            .Select(value => new MediaWikiCargoRelationshipSourceRow("Aurora", value, value.ToString()))
            .ToArray();
        Assert.Throws<InvalidDataException>(() => MediaWikiCargoRelationshipProjector.ProjectSynthetic(request, definition, rows, provenance));
    }

    private static MediaWikiCargoRelationshipQuery RelationshipRequest(
        string relationship,
        string dataset = "synthetic-cargo-relationship-v1",
        string sourceWiki = "synthetic-source",
        string sourcePage = "page-1",
        string sourceRevision = "revision-1",
        MediaWikiCargoRelationshipDatasetClassification classification = MediaWikiCargoRelationshipDatasetClassification.Synthetic)
        => new(
            new(sourceWiki, sourcePage, sourceRevision, dataset),
            relationship,
            "Aurora",
            [MediaWikiCargoRelationshipProjection.Value],
            null,
            0,
            10,
            new(),
            classification,
            MediaWikiCargoRelationshipCatalog.AuthorizedCurrentSchema);

    private static MediaWikiCargoRelationshipDatasetProvenance SyntheticRelationshipProvenance(string dataset, string sourceWiki)
        => new(dataset, MediaWikiCargoRelationshipDatasetClassification.Synthetic, sourceWiki,
            MediaWikiCargoRelationshipCatalog.AuthorizedCurrentSchema, false);

    private sealed class ThrowingConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<IDatabaseSession> OpenSessionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The database must not be opened for a rejected query.");
    }
}
