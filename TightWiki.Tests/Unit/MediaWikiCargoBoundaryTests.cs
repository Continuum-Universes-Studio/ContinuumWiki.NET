using TightWiki.Engine.MediaWiki;
using TightWiki.Library.Database;
using TightWiki.Repository.Helpers;
using NTDLS.SqliteDapperWrapper;

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

    private sealed class ThrowingConnectionFactory : IDatabaseConnectionFactory
    {
        public ValueTask<IDatabaseSession> OpenSessionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The database must not be opened for a rejected query.");
    }
}
