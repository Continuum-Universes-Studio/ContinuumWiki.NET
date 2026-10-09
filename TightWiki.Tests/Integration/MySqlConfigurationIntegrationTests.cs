using Dapper;
using TightWiki.Repository.Helpers;
namespace TightWiki.Tests.Integration
{
    public sealed class MySqlFactAttribute : FactAttribute
    {
        public MySqlFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("CONTINUUMWIKI_RUN_MYSQL_TESTS"), "1", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION")))
            {
                Skip = "Requires CONTINUUMWIKI_RUN_MYSQL_TESTS=1 and an explicitly disposable MySQL database connection.";
            }
        }
    }

    public class MySqlConfigurationIntegrationTests
    {
        [MySqlFact]
        public async Task DisposableMySqlConfigurationSliceSupportsUnicodeCrud()
        {
            var connectionString = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION");
            if (!string.Equals(Environment.GetEnvironmentVariable("CONTINUUMWIKI_RUN_MYSQL_TESTS"), "1", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            var factory = new MySqlDatabaseConnectionFactory(connectionString);
            await new MySqlConfigurationSchemaInitializer().InitializeAsync(factory);
            var repository = new MySqlConfigurationRepository(factory);

            await repository.EnsureGroupAsync("Integration");
            await repository.SaveEntryAsync("Integration", "Unicode", "Continuum 世界 🌌");
            var entries = await repository.GetEntriesByGroupNameAsync("Integration");

            Assert.Contains(entries, entry => entry.Name == "Unicode" && entry.Value == "Continuum 世界 🌌");
            Assert.Equal("Continuum 世界 🌌", await repository.GetEntryValueAsync("Integration", "Unicode"));
            Assert.Contains(await repository.GetFlatConfigurationAsync(), entry => entry.GroupName == "Integration" && entry.EntryName == "Unicode");
            Assert.Contains(await repository.GetConfigurationNestAsync(), group => group.Name == "Integration");

            await using var session = await factory.OpenSessionAsync();
            await session.Connection.ExecuteAsync("INSERT INTO Theme (Name, DelimitedFiles, ClassNavBar, ClassNavLink, ClassDropdown, ClassBranding, EditorTheme) VALUES ('Integration', 'a.css;b.css', 'nav', 'link', 'dropdown', 'brand', 'editor');");
            await session.Connection.ExecuteAsync("INSERT INTO MenuItem (Name, Link, Ordinal) VALUES ('世界', '/world', 1);");
            Assert.Equal(new[] { "a.css", "b.css" }, (await repository.GetThemesAsync()).Single().Files);
            var menu = (await repository.GetMenuItemsAsync()).Single();
            Assert.Equal("世界", menu.Name);
            Assert.Equal(menu.Id, (await repository.GetMenuItemByIdAsync(menu.Id))!.Id);
            menu.Name = "世界 updated";
            Assert.Equal(menu.Id, await repository.UpdateMenuItemAsync(menu));
            await repository.DeleteMenuItemAsync(menu.Id);
            Assert.Empty(await repository.GetMenuItemsAsync());
        }
    }
}
