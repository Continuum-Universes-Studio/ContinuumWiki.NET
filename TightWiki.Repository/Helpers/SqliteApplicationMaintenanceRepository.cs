using Microsoft.AspNetCore.Identity;
using TightWiki.Plugin.Interfaces;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;
using TightWiki.Plugin.Models.Defaults;
using TightWiki.Plugin;

namespace TightWiki.Repository.Helpers;

/// <summary>Provider-aware adapter that preserves the existing SQLite maintenance implementation.</summary>
public sealed class SqliteApplicationMaintenanceRepository : ITwApplicationMaintenanceRepository
{
    private readonly ITwDatabaseManager _databaseManager;

    public SqliteApplicationMaintenanceRepository(ITwDatabaseManager databaseManager) => _databaseManager = databaseManager;

    public string BackupPath => _databaseManager.BackupPath;

    public async Task<IReadOnlyList<TwDatabaseInfo>> GetDatabaseInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var versions = await _databaseManager.GetDatabaseVersions();
        var counts = await _databaseManager.GetDatabasePageCounts();
        var sizes = await _databaseManager.GetDatabasePageSizes();
        return versions.Select(version =>
        {
            var pageCount = counts.FirstOrDefault(value => value.Name == version.Name).PageCount;
            var pageSize = sizes.FirstOrDefault(value => value.Name == version.Name).PageSize;
            return new TwDatabaseInfo { Name = version.Name, Version = version.Version, PageCount = pageCount, PageSize = pageSize, DatabaseSize = pageCount * pageSize };
        }).ToList();
    }

    public async Task<TwDatabaseMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetDatabaseInfoAsync(cancellationToken);
        return new TwDatabaseMaintenanceStatus
        {
            Provider = "sqlite",
            DatabaseName = "SQLite",
            IsHealthy = true,
            IsInitialized = info.Count > 0,
            SchemaVersion = string.Join(", ", info.Select(value => $"{value.Name}:{value.Version}")),
            SeedStatus = "SQLite seed orchestration is available.",
            Message = "SQLite database maintenance is available."
        };
    }

    public async Task<TwMaintenanceResult> ExecuteAsync(TwMaintenanceAction action, string databaseName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = action switch
        {
            TwMaintenanceAction.Optimize => await _databaseManager.OptimizeDatabase(databaseName),
            TwMaintenanceAction.Vacuum => await _databaseManager.VacuumDatabase(databaseName),
            TwMaintenanceAction.Verify => await _databaseManager.IntegrityCheckDatabase(databaseName),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return TwMaintenanceResult.Success(message);
    }

    public async Task<TwDatabaseBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _databaseManager.BackupDatabases();
    }

    public IReadOnlyList<TwDatabaseBackup> GetBackups() => _databaseManager.GetDatabaseBackups();

    public void DeleteBackup(string fileName) => _databaseManager.DeleteDatabaseBackup(fileName);

    public async Task<TwMaintenanceResult> ApplySeedDataAsync(ITwSharedLocalizationText localizer, UserManager<IdentityUser> userManager, ITwEngine tightEngine, TwDefaultDataType[] defaultDataTypes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _databaseManager.ApplyAllSeedData(localizer, userManager, tightEngine, defaultDataTypes);
        return TwMaintenanceResult.Success("SQLite seed data was applied idempotently.");
    }
}
