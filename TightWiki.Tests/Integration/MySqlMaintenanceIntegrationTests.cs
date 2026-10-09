using Dapper;
using MySqlConnector;
using TightWiki.Plugin;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

public sealed class MySqlMaintenanceIntegrationTests
{
    [MySqlFact]
    public async Task HealthReportsInitializationAndSupportedSchemaVersionsReadOnly()
    {
        var factory = Factory();
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        var maintenance = new MySqlApplicationMaintenanceRepository(factory);

        var status = await maintenance.GetStatusAsync();

        Assert.True(status.IsHealthy);
        Assert.True(status.IsInitialized);
        Assert.Equal("mysql", status.Provider);
        Assert.Contains("pages:3", status.SchemaVersion);
    }

    [MySqlFact]
    public async Task FutureSchemaVersionIsReportedAsUnavailableWithoutMigration()
    {
        var source = ConnectionString();
        var databaseName = $"continuumwiki_future_{Guid.NewGuid():N}";
        var server = new MySqlConnectionStringBuilder(source) { Database = string.Empty };
        await using (var connection = new MySqlConnection(server.ConnectionString))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync($"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;");
        }

        try
        {
            var isolated = new MySqlConnectionStringBuilder(source) { Database = databaseName };
            var factory = new MySqlDatabaseConnectionFactory(isolated.ConnectionString);
            await new MySqlSchemaInitializer().InitializeAsync(factory);
            await using (var session = await factory.OpenSessionAsync())
                await session.Connection.ExecuteAsync("UPDATE MySqlPageSchemaVersion SET Version = 99 WHERE Domain = 'pages';");

            var status = await new MySqlApplicationMaintenanceRepository(factory).GetStatusAsync();
            Assert.False(status.IsHealthy);
            Assert.Contains("supports only version 3", status.Message);
        }
        finally
        {
            await using var cleanup = new MySqlConnection(server.ConnectionString);
            await cleanup.OpenAsync();
            await cleanup.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`;");
        }
    }

    [MySqlFact]
    public async Task BackupAndDefaultContentRestoreRejectWithoutSQLiteFallback()
    {
        var factory = Factory();
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        var maintenance = new MySqlApplicationMaintenanceRepository(factory);

        await Assert.ThrowsAsync<NotSupportedException>(() => maintenance.CreateBackupAsync());
        var seed = await maintenance.ApplySeedDataAsync(null!, null!, null!, [], CancellationToken.None);
        Assert.True(seed.Supported);
        Assert.True(seed.Succeeded);
    }

    [MySqlFact]
    public async Task DefaultContentSeedIsIdempotentAndPreservesCustomizedConfiguration()
    {
        var factory = Factory();
        var maintenance = new MySqlApplicationMaintenanceRepository(factory);
        var requested = new[] { TwDefaultDataType.Configurations, TwDefaultDataType.Themes, TwDefaultDataType.FeatureTemplates };

        Assert.True((await maintenance.ApplySeedDataAsync(null!, null!, null!, requested)).Succeeded);
        await using var first = await factory.OpenSessionAsync();
        var firstCounts = await first.Connection.QuerySingleAsync<(int Configurations, int Themes, int Templates)>("SELECT (SELECT COUNT(*) FROM DefaultConfiguration), (SELECT COUNT(*) FROM DefaultThemes), (SELECT COUNT(*) FROM DefaultFeatureTemplates);");
        var configuration = await first.Connection.QuerySingleAsync<(string GroupName, string EntryName)>("SELECT ConfigurationGroupName, ConfigurationEntryName FROM DefaultConfiguration ORDER BY ConfigurationGroupName, ConfigurationEntryName LIMIT 1;");
        await first.Connection.ExecuteAsync("UPDATE ConfigurationEntry CE INNER JOIN ConfigurationGroup CG ON CG.Id = CE.ConfigurationGroupId SET CE.Value = 'customized-by-user' WHERE CG.Name = @GroupName AND CE.Name = @EntryName;", new { configuration.GroupName, configuration.EntryName });

        Assert.True((await maintenance.ApplySeedDataAsync(null!, null!, null!, requested)).Succeeded);
        await using var second = await factory.OpenSessionAsync();
        var secondCounts = await second.Connection.QuerySingleAsync<(int Configurations, int Themes, int Templates)>("SELECT (SELECT COUNT(*) FROM DefaultConfiguration), (SELECT COUNT(*) FROM DefaultThemes), (SELECT COUNT(*) FROM DefaultFeatureTemplates);");
        var customized = await second.Connection.ExecuteScalarAsync<string>("SELECT CE.Value FROM ConfigurationEntry CE INNER JOIN ConfigurationGroup CG ON CG.Id = CE.ConfigurationGroupId WHERE CG.Name = @GroupName AND CE.Name = @EntryName;", new { configuration.GroupName, configuration.EntryName });

        Assert.Equal(firstCounts, secondCounts);
        Assert.Equal("customized-by-user", customized);
    }

    [MySqlFact]
    public async Task BuiltInPageSeedPreservesRevisionRelationshipsAndCustomizedBodies()
    {
        var factory = Factory();
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        await CreateAdministratorAsync(factory);
        var maintenance = new MySqlApplicationMaintenanceRepository(factory);

        Assert.True((await maintenance.ApplySeedDataAsync(null!, null!, null!, [TwDefaultDataType.BuiltinPages])).Succeeded);
        await using var first = await factory.OpenSessionAsync();
        var seeded = await first.Connection.QuerySingleAsync<(int Id, string Navigation, int Revision, string Body)>("SELECT P.Id, P.Navigation, P.Revision, R.Body FROM Page P INNER JOIN PageRevision R ON R.PageId = P.Id AND R.Revision = P.Revision WHERE P.`Namespace` = 'Builtin' ORDER BY P.Id LIMIT 1;");
        var pageCount = await first.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Page WHERE `Namespace` = 'Builtin';");
        await first.Connection.ExecuteAsync("UPDATE PageRevision SET Body = 'customized built-in body' WHERE PageId = @Id AND Revision = @Revision;", new { seeded.Id, seeded.Revision });

        Assert.True((await maintenance.ApplySeedDataAsync(null!, null!, null!, [TwDefaultDataType.BuiltinPages])).Succeeded);
        await using var second = await factory.OpenSessionAsync();
        var preserved = await second.Connection.ExecuteScalarAsync<string>("SELECT Body FROM PageRevision WHERE PageId = @Id AND Revision = @Revision;", new { seeded.Id, seeded.Revision });
        var repeatedCount = await second.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Page WHERE `Namespace` = 'Builtin';");
        var revisionRows = await second.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PageRevision WHERE PageId = @Id AND Revision = @Revision;", new { seeded.Id, seeded.Revision });

        Assert.Equal(pageCount, repeatedCount);
        Assert.Equal(1, revisionRows);
        Assert.Equal("customized built-in body", preserved);
    }

    [MySqlFact]
    public async Task TranslationAndMenuSeedPreservesUnicodeFallbackOrderingAndCustomization()
    {
        var factory = Factory();
        var maintenance = new MySqlApplicationMaintenanceRepository(factory);
        var requested = new[] { TwDefaultDataType.Translations, TwDefaultDataType.Menus };

        Assert.True((await maintenance.ApplySeedDataAsync(null!, null!, null!, requested)).Succeeded);
        await using var first = await factory.OpenSessionAsync();
        var firstTranslationCount = await first.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Translation;");
        var firstMenuCount = await first.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MenuItem;");
        var sourceTranslation = await first.Connection.ExecuteScalarAsync<string>("SELECT French FROM Translation WHERE English = '- or -';");
        Assert.Equal("- ou -", sourceTranslation);
        Assert.Equal("- ou -", await new MySqlTranslationRepository(factory).GetTranslationAsync("fr-CA", "- or -"));
        Assert.Equal("missing phrase", await new MySqlTranslationRepository(factory).GetTranslationAsync("en", "missing phrase"));

        await first.Connection.ExecuteAsync("UPDATE Translation SET French = 'customisé 世界' WHERE English = '- or -';");
        await first.Connection.ExecuteAsync("UPDATE MenuItem SET Name = 'Custom About', Link = '/custom-about' WHERE Id = 3;");

        Assert.True((await maintenance.ApplySeedDataAsync(null!, null!, null!, requested)).Succeeded);
        await using var second = await factory.OpenSessionAsync();
        Assert.Equal(firstTranslationCount, await second.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Translation;"));
        Assert.Equal(firstMenuCount, await second.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MenuItem;"));
        Assert.Equal("customisé 世界", await second.Connection.ExecuteScalarAsync<string>("SELECT French FROM Translation WHERE English = '- or -';"));
        var menus = (await second.Connection.QueryAsync<(int Id, string Name, string Link, int Ordinal)>("SELECT Id, Name, Link, Ordinal FROM MenuItem ORDER BY Ordinal, Id;")).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, menus.Select(menu => menu.Ordinal));
        Assert.Equal("Custom About", menus.Single(menu => menu.Id == 3).Name);
        Assert.Equal("/custom-about", menus.Single(menu => menu.Id == 3).Link);
    }

    private static MySqlDatabaseConnectionFactory Factory()
    {
        return new MySqlDatabaseConnectionFactory(ConnectionString());
    }

    private static string ConnectionString()
        => Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION")
            ?? throw new InvalidOperationException("MySQL test connection is missing.");

    private static async Task CreateAdministratorAsync(MySqlDatabaseConnectionFactory factory)
    {
        var userId = Guid.NewGuid().ToString("D");
        await using var session = await factory.OpenSessionAsync();
        await session.Connection.ExecuteAsync("INSERT INTO AspNetUsers (Id, UserName, NormalizedUserName, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES (@Id, @UserName, @NormalizedUserName, FALSE, FALSE, FALSE, TRUE, 0);", new { Id = userId, UserName = $"seed-admin-{userId[..8]}", NormalizedUserName = $"SEED-ADMIN-{userId[..8]}" });
        await session.Connection.ExecuteAsync("INSERT INTO Profile (UserId, AccountName, Navigation, CreatedDate, ModifiedDate) VALUES (@UserId, @AccountName, @Navigation, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));", new { UserId = userId, AccountName = $"Seed Admin {userId[..8]}", Navigation = $"seed-admin-{userId[..8]}" });
        var roleId = await session.Connection.ExecuteScalarAsync<int>("SELECT Id FROM Role WHERE Name = 'Administrator';");
        await session.Connection.ExecuteAsync("INSERT INTO AccountRole (UserId, RoleId) VALUES (@UserId, @RoleId);", new { UserId = userId, RoleId = roleId });
    }
}
