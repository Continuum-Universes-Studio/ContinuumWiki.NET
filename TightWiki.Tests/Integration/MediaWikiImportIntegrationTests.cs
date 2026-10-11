using Dapper;
using System.Security.Cryptography;
using System.Text.Json;
using MediaWikiImport;
using MediaWikiInventory;
using MySqlConnector;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

public sealed class MediaWikiImportIntegrationTests
{
    [MySqlFact]
    public async Task DisposableMariaDbMediaImportPreservesBinaryHistoryReferencesAndRetryGuard()
    {
        var source = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION")
            ?? throw new InvalidOperationException("MySQL test connection is missing.");
        var databaseName = $"cw_m3_003b_{Guid.NewGuid():N}";
        var server = new MySqlConnectionStringBuilder(source) { Database = string.Empty };
        await using (var connection = new MySqlConnection(server.ConnectionString))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync($"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");
        }
        var root = Path.Combine(Path.GetTempPath(), "cw-m3-003b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "media"));
        try
        {
            var targetConnection = new MySqlConnectionStringBuilder(source) { Database = databaseName };
            var factory = new MySqlDatabaseConnectionFactory(targetConnection.ConnectionString);
            await new MySqlSchemaInitializer().InitializeAsync(factory);
            var target = new MediaWikiImportTargetConfiguration(targetConnection.ConnectionString, databaseName, "disposable-mariadb");
            var executor = new MediaWikiImportExecutor(factory, target);
            await executor.InitializeImportSchemaAsync();
            var fixture = CreateMediaManifest(root);
            var input = Fixture("media-fixture.xml");
            var plan = MediaWikiImportPlanner.Create(input, NamespaceMappingConfiguration.Defaults, fixture.ManifestPath, root);
            Assert.NotNull(plan.Media);
            Assert.DoesNotContain(plan.Media!.Findings, finding => finding.Severity == PlanFindingSeverity.Blocking);
            var result = await executor.ImportAsync(plan, input, NamespaceMappingConfiguration.Defaults, fixture.ManifestPath, root);

            Assert.True(result.Reconciliation.IsSuccessful);
            Assert.Equal(3, result.Reconciliation.ImportedMediaCount);
            Assert.Equal(4, result.Reconciliation.ImportedMediaRevisionCount);
            await using var session = await factory.OpenSessionAsync();
            Assert.Equal(3, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MediaWikiImportMedia WHERE ImportBatchId = @Batch;", new { Batch = result.ImportBatchId }));
            Assert.Equal(4, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MediaWikiImportMediaRevision WHERE ImportBatchId = @Batch;", new { Batch = result.ImportBatchId }));
            Assert.Equal(6, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MediaWikiImportMediaReference WHERE ImportBatchId = @Batch AND ResolutionStatus = 'Resolved';", new { Batch = result.ImportBatchId }));
            Assert.Equal(4, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageFileRevision;"));
            Assert.Equal(fixture.ExampleCurrentSha256, await session.Connection.ExecuteScalarAsync<string>("SELECT LOWER(SHA2(Data, 256)) FROM PageFileRevision PFR JOIN PageFile PF ON PF.Id = PFR.PageFileId WHERE PF.Name = 'Example.png' AND PFR.Revision = 1;"));
            Assert.Equal("Media author", await session.Connection.ExecuteScalarAsync<string>("SELECT ContributorDisplayName FROM MediaWikiImportMediaRevision WHERE SourceMediaRevisionId = '6001';"));
            await Assert.ThrowsAsync<MediaWikiImportPreflightException>(() => executor.ImportAsync(plan, input, NamespaceMappingConfiguration.Defaults, fixture.ManifestPath, root));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            await using var cleanup = new MySqlConnection(server.ConnectionString);
            await cleanup.OpenAsync();
            await cleanup.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`;");
        }
    }

    [MySqlFact]
    public async Task DisposableMariaDbImportPreservesProvenanceHistoryAttributionAndRedirects()
    {
        var source = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION")
            ?? throw new InvalidOperationException("MySQL test connection is missing.");
        var databaseName = $"cw_m3_003a_{Guid.NewGuid():N}";
        var server = new MySqlConnectionStringBuilder(source) { Database = string.Empty };
        await using (var connection = new MySqlConnection(server.ConnectionString))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync($"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");
        }

        try
        {
            var targetConnection = new MySqlConnectionStringBuilder(source) { Database = databaseName };
            var factory = new MySqlDatabaseConnectionFactory(targetConnection.ConnectionString);
            await new MySqlSchemaInitializer().InitializeAsync(factory);
            var target = new MediaWikiImportTargetConfiguration(targetConnection.ConnectionString, databaseName, "disposable-mariadb");
            var executor = new MediaWikiImportExecutor(factory, target);
            await executor.InitializeImportSchemaAsync();

            var input = Fixture("persistence-fixture.xml");
            var plan = MediaWikiImportPlanner.Create(input, NamespaceMappingConfiguration.Defaults);
            var result = await executor.ImportAsync(plan, input, NamespaceMappingConfiguration.Defaults);

            Assert.Equal(MediaWikiImportBatchStatus.Completed, result.Status);
            Assert.True(result.Reconciliation.IsSuccessful);
            Assert.Equal(4, result.Reconciliation.ImportedPageCount);
            Assert.Equal(5, result.Reconciliation.ImportedRevisionCount);

            await using var session = await factory.OpenSessionAsync();
            Assert.Equal(4, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MediaWikiImportPage WHERE ImportBatchId = @ImportBatchId;", new { ImportBatchId = result.ImportBatchId }));
            Assert.Equal(5, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MediaWikiImportRevision WHERE ImportBatchId = @ImportBatchId;", new { ImportBatchId = result.ImportBatchId }));
            Assert.Equal(2, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MediaWikiImportRevision WHERE ImportBatchId = @ImportBatchId AND ContributorKind = 'AnonymousIp';", new { ImportBatchId = result.ImportBatchId }));
            Assert.Equal(1, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageReference WHERE ReferencesPageId IS NOT NULL AND ReferencesPageName = '世界';"));
            Assert.Equal("Second {{Infobox}} [[Category:Lore]] [[File:Map.png]] 世界", await session.Connection.ExecuteScalarAsync<string>("SELECT Body FROM PageRevision WHERE PageId = (SELECT TargetPageId FROM MediaWikiImportPage WHERE ImportBatchId = @ImportBatchId AND SourcePageId = '100') AND Revision = 2;", new { ImportBatchId = result.ImportBatchId }));
        }
        finally
        {
            await using var cleanup = new MySqlConnection(server.ConnectionString);
            await cleanup.OpenAsync();
            await cleanup.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`;");
        }
    }

    private static string Fixture(string name)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "MediaWikiInventory", "Fixtures", name);
        return File.Exists(outputPath) ? outputPath : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures", name));
    }

    private static MediaFixture CreateMediaManifest(string root)
    {
        var contributor = new MediaWikiContributor(MediaWikiContributorKind.NamedUser, "21", "Media author", null, false);
        var ip = new MediaWikiContributor(MediaWikiContributorKind.AnonymousIp, null, null, "192.0.2.51", false);
        var map = new MediaWikiContributor(MediaWikiContributorKind.NamedUser, "22", "Map author", null, false);
        var anonymous = new MediaWikiContributor(MediaWikiContributorKind.AnonymousIp, null, null, "192.0.2.52", false);
        var example1 = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };
        var example2 = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 5, 6, 7, 8 };
        var jpeg = new byte[] { 255, 216, 255, 224, 0, 16, 74, 70, 73, 70 };
        var unicode = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 9, 10 };
        Write(root, "media/example-v1.bin", example1);
        Write(root, "media/example-v2.bin", example2);
        Write(root, "media/map.bin", jpeg);
        Write(root, "media/unicode.bin", unicode);
        MediaWikiMediaRevision Revision(string id, int ordinal, string path, string mime, byte[] data, MediaWikiContributor uploader)
            => new(id, ordinal, DateTimeOffset.Parse($"2025-03-0{ordinal}T01:00:00Z"), uploader, mime, data.LongLength, Hash(data), null, path, "synthetic");
        var manifest = new MediaWikiMediaManifest("cw-m3-003b-fixture-1", "Continuum Synthetic Media Wiki|https://media.synthetic.invalid/wiki|MediaWiki 1.42", [
            new("Example.png", "600", "image/png", example2.LongLength, Hash(example2), null, "media/example-v2.bin", DateTimeOffset.Parse("2025-03-01T01:00:00Z"), contributor, "synthetic", [Revision("6001", 1, "media/example-v1.bin", "image/png", example1, contributor), Revision("6002", 2, "media/example-v2.bin", "image/png", example2, ip)]),
            new("地图.jpg", "601", "image/jpeg", jpeg.LongLength, Hash(jpeg), null, "media/map.bin", DateTimeOffset.Parse("2025-03-01T02:00:00Z"), map, "synthetic", [Revision("6011", 1, "media/map.bin", "image/jpeg", jpeg, map)]),
            new("ファイル.png", "602", "image/png", unicode.LongLength, Hash(unicode), null, "media/unicode.bin", DateTimeOffset.Parse("2025-03-01T03:00:00Z"), anonymous, "synthetic", [Revision("6021", 1, "media/unicode.bin", "image/png", unicode, anonymous)])
        ]);
        var path = Path.Combine(root, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return new(path, Hash(example2));
    }

    private static void Write(string root, string relative, byte[] bytes) => File.WriteAllBytes(Path.Combine(root, relative), bytes);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record MediaFixture(string ManifestPath, string ExampleCurrentSha256);
}
