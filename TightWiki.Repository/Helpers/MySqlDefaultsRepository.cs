using Dapper;
using TightWiki.Library.Database;
using TightWiki.Plugin.Models.Defaults;
using TightWiki.Plugin.Interfaces.Persistence;

namespace TightWiki.Repository.Helpers;

/// <summary>Read-only access to explicitly seeded MySQL default content.</summary>
public sealed class MySqlDefaultsRepository : ITwApplicationDefaultsRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public MySqlDefaultsRepository(IDatabaseConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<IReadOnlyList<TwDefaultConfiguration>> GetConfigurationsAsync(CancellationToken cancellationToken = default)
        => await QueryAsync<TwDefaultConfiguration>("SELECT ConfigurationGroupName, ConfigurationEntryName, Value, DataTypeId, ConfigurationEntryDescription, ConfigurationGroupDescription, IsEncrypted, IsRequired FROM DefaultConfiguration ORDER BY ConfigurationGroupName, ConfigurationEntryName;", cancellationToken);

    public async Task<IReadOnlyList<TwDefaultTheme>> GetThemesAsync(CancellationToken cancellationToken = default)
        => await QueryAsync<TwDefaultTheme>("SELECT Name, DelimitedFiles, ClassNavBar, ClassNavLink, ClassDropdown, ClassBranding, EditorTheme FROM DefaultThemes ORDER BY Name;", cancellationToken);

    public async Task<IReadOnlyList<TwDefaultFeatureTemplate>> GetFeatureTemplatesAsync(CancellationToken cancellationToken = default)
        => await QueryAsync<TwDefaultFeatureTemplate>("SELECT Name, Type, PageName, Description, TemplateText FROM DefaultFeatureTemplates ORDER BY Name, Type;", cancellationToken);

    public async Task<IReadOnlyList<TwDefaultWikiPage>> GetWikiPagesAsync(CancellationToken cancellationToken = default)
        => await QueryAsync<TwDefaultWikiPage>("SELECT Name, Namespace, Navigation, Description, Revision, DataHash, Body FROM DefaultWikiPages ORDER BY Navigation;", cancellationToken);

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, CancellationToken cancellationToken)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return (await session.Connection.QueryAsync<T>(new CommandDefinition(sql, cancellationToken: cancellationToken))).AsList();
    }
}
