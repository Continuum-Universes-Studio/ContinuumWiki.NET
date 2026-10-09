using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Interfaces.Repository;
using TightWiki.Plugin.Models;

namespace TightWiki.Repository.Helpers;

/// <summary>Thin provider-neutral configuration read adapter for SQLite consumers.</summary>
public sealed class SqliteApplicationConfigurationRepository(ITwConfigurationRepository repository) : ITwApplicationConfigurationRepository
{
    public async Task<IReadOnlyList<TwConfigurationEntry>> GetEntriesByGroupNameAsync(string groupName, CancellationToken cancellationToken = default)
        => (await repository.GetConfigurationEntryValuesByGroupName(groupName).WaitAsync(cancellationToken)).Collection;
    public Task EnsureGroupAsync(string groupName, string? description = null, CancellationToken cancellationToken = default) => Unsupported();
    public Task SaveEntryAsync(string groupName, string entryName, string value, CancellationToken cancellationToken = default) => Unsupported();
    public async Task<IReadOnlyList<TwTheme>> GetThemesAsync(CancellationToken cancellationToken = default)
        => await repository.GetAllThemes().WaitAsync(cancellationToken);
    public async Task<IReadOnlyList<TwMenuItem>> GetMenuItemsAsync(CancellationToken cancellationToken = default)
        => await repository.GetAllMenuItems().WaitAsync(cancellationToken);
    public async Task<IReadOnlyList<TwConfigurationFlat>> GetFlatConfigurationAsync(CancellationToken cancellationToken = default)
        => await repository.GetFlatConfiguration().WaitAsync(cancellationToken);
    public async Task<IReadOnlyList<TwConfigurationNest>> GetConfigurationNestAsync(CancellationToken cancellationToken = default)
        => await repository.GetConfigurationNest().WaitAsync(cancellationToken);
    public async Task<TwMenuItem?> GetMenuItemByIdAsync(int id, CancellationToken cancellationToken = default)
        => await repository.GetMenuItemById(id).WaitAsync(cancellationToken);
    public async Task<int> InsertMenuItemAsync(TwMenuItem menuItem, CancellationToken cancellationToken = default)
        => await repository.InsertMenuItem(menuItem).WaitAsync(cancellationToken);
    public async Task<int> UpdateMenuItemAsync(TwMenuItem menuItem, CancellationToken cancellationToken = default)
        => await repository.UpdateMenuItemById(menuItem).WaitAsync(cancellationToken);
    public Task DeleteMenuItemAsync(int id, CancellationToken cancellationToken = default)
        => repository.DeleteMenuItemById(id).WaitAsync(cancellationToken);
    public async Task<string?> GetEntryValueAsync(string groupName, string entryName, CancellationToken cancellationToken = default)
        => await repository.GetConfigurationEntryValuesByGroupNameAndEntryName(groupName, entryName).WaitAsync(cancellationToken);
    public async Task<TwWikiDatabaseStatistics?> GetDatabaseMetricsAsync(CancellationToken cancellationToken = default)
        => await repository.GetWikiDatabaseMetrics().WaitAsync(cancellationToken);
    private static Task Unsupported() => Task.FromException(new NotSupportedException("This SQLite compatibility adapter exposes only configuration reads required by metadata rebuilding."));
    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("This SQLite compatibility adapter exposes only configuration reads required by metadata rebuilding."));
}
