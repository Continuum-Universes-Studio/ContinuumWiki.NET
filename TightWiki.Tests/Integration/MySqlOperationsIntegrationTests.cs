using Dapper;
using Microsoft.Extensions.Logging;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

public sealed class MySqlOperationsIntegrationTests
{
    [MySqlFact]
    public async Task OperationsSchemaAndRepositoriesPreserveUnicodeAndAtomicUpdates()
    {
        var connectionString = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        var repository = new MySqlOperationsRepository(factory);

        await using (var translationSession = await factory.OpenSessionAsync())
        {
            await translationSession.Connection.ExecuteAsync("INSERT INTO Translation (English, French) VALUES (@English, @French) ON DUPLICATE KEY UPDATE French = VALUES(French);", new { English = "世界 greeting", French = "bonjour 世界" });
        }
        Assert.Equal("bonjour 世界", await new MySqlTranslationRepository(factory).GetTranslationAsync("French", "世界 greeting"));

        await using (var defaultsSession = await factory.OpenSessionAsync())
        {
            await defaultsSession.Connection.ExecuteAsync("INSERT INTO DefaultConfiguration (ConfigurationGroupName, ConfigurationEntryName, Value, DataTypeId, IsEncrypted, IsRequired) VALUES ('Test', 'Unicode', '世界', 1, FALSE, FALSE);");
        }
        Assert.Contains(await new MySqlDefaultsRepository(factory).GetConfigurationsAsync(), o => o.ConfigurationEntryName == "Unicode" && o.Value == "世界");

        await repository.WriteLogAsync(LogLevel.Error, "世界 🌌", "exception", "stack");
        Assert.Equal(1, await repository.GetExceptionCountAsync());

        var page = new TwPage
        {
            Name = $"Operations::{Guid.NewGuid():N} 世界",
            Body = "body",
            CreatedByUserId = Guid.NewGuid(),
            ModifiedByUserId = Guid.NewGuid()
        };
        await new MySqlPageRepository(factory).CreatePageAsync(page);
        await repository.IncrementPageViewCountAsync(page.Id);
        await repository.MergePageCompilationStatisticsAsync(page.Id, 1.25, 2, 1, 3, 4, 5, 6);
        Assert.Equal(1, await repository.GetPageTotalViewCountAsync(page.Id));

        var emoji = new TwUpsertEmoji { Name = $"world-{Guid.NewGuid():N}", MimeType = "image/png", ImageData = [1, 2, 3], Categories = ["世界", "test"] };
        var emojiId = await repository.UpsertEmojiAsync(emoji);
        Assert.Equal(emojiId, emoji.Id);

        await using var session = await factory.OpenSessionAsync();
        Assert.Equal(2, await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EmojiCategory WHERE EmojiId = @Id", new { Id = emojiId }));
    }
}
