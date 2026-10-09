using Dapper;
using TightWiki.Library;
using TightWiki.Library.Database;
using TightWiki.Library.Security;
using TightWiki.Plugin.Models;
using TightWiki.Plugin.Interfaces.Persistence;

namespace TightWiki.Repository.Helpers
{
    /// <summary>
    /// Independently testable MySQL configuration slice. It is not wired into the
    /// application until all repositories can participate in the selected provider.
    /// </summary>
    public sealed class MySqlConfigurationRepository : ITwApplicationConfigurationRepository
    {
        private readonly IDatabaseConnectionFactory _connectionFactory;

        public MySqlConfigurationRepository(IDatabaseConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<IReadOnlyList<TwConfigurationEntry>> GetEntriesByGroupNameAsync(string groupName, CancellationToken cancellationToken = default)
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            var sql = EmbeddedResourceReader.LoadText("MySql.Configuration.GetEntriesByGroupName.sql");
            var command = new CommandDefinition(sql, new { GroupName = groupName }, cancellationToken: cancellationToken);
            var entries = (await session.Connection.QueryAsync<TwConfigurationEntry>(command)).AsList();
            foreach (var entry in entries)
                Decrypt(entry);
            return entries;
        }

        public async Task EnsureGroupAsync(string groupName, string? description = null, CancellationToken cancellationToken = default)
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            var command = new CommandDefinition(
                "INSERT INTO ConfigurationGroup (Name, Description) VALUES (@Name, @Description) ON DUPLICATE KEY UPDATE Description = VALUES(Description);",
                new { Name = groupName, Description = description },
                cancellationToken: cancellationToken);
            await session.Connection.ExecuteAsync(command);
        }

        public async Task SaveEntryAsync(string groupName, string entryName, string value, CancellationToken cancellationToken = default)
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken);
            var encrypted = await session.Connection.ExecuteScalarAsync<bool?>(new CommandDefinition("SELECT CE.IsEncrypted FROM ConfigurationEntry CE INNER JOIN ConfigurationGroup CG ON CG.Id = CE.ConfigurationGroupId WHERE CG.Name = @GroupName AND CE.Name = @EntryName;", new { GroupName = groupName, EntryName = entryName }, transaction.Transaction, cancellationToken: cancellationToken));
            if (encrypted == true)
                value = SecurityUtility.EncryptString(SecurityUtility.MachineKey, value);
            var command = new CommandDefinition(
                EmbeddedResourceReader.LoadText("MySql.Configuration.SaveEntry.sql"),
                new { GroupName = groupName, EntryName = entryName, Value = value },
                transaction.Transaction,
                cancellationToken: cancellationToken);
            await session.Connection.ExecuteAsync(command);
            await transaction.CommitAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<TwTheme>> GetThemesAsync(CancellationToken cancellationToken = default)
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            var themes = (await session.Connection.QueryAsync<TwTheme>(new CommandDefinition("SELECT Name, DelimitedFiles, ClassNavBar, ClassNavLink, ClassDropdown, ClassBranding, EditorTheme FROM Theme ORDER BY Name;", cancellationToken: cancellationToken))).AsList();
            foreach (var theme in themes)
                theme.Files = theme.DelimitedFiles.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            return themes;
        }

        public async Task<IReadOnlyList<TwMenuItem>> GetMenuItemsAsync(CancellationToken cancellationToken = default)
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            return (await session.Connection.QueryAsync<TwMenuItem>(new CommandDefinition("SELECT Id, Name, Link, Ordinal FROM MenuItem ORDER BY Ordinal, Id;", cancellationToken: cancellationToken))).AsList();
        }

        public async Task<IReadOnlyList<TwConfigurationFlat>> GetFlatConfigurationAsync(CancellationToken cancellationToken = default)
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            var rows = (await session.Connection.QueryAsync<TwConfigurationFlat>(new CommandDefinition("""
                SELECT CG.Id AS GroupId, CG.Name AS GroupName, CG.Description AS GroupDescription,
                       CE.Id AS EntryId, CE.Name AS EntryName, CE.Value AS EntryValue,
                       CE.Description AS EntryDescription, CE.IsEncrypted, CE.IsRequired,
                       COALESCE(DT.Name, '') AS DataType
                FROM ConfigurationGroup CG
                INNER JOIN ConfigurationEntry CE ON CE.ConfigurationGroupId = CG.Id
                LEFT JOIN DataType DT ON DT.Id = CE.DataTypeId
                ORDER BY CG.Id, CE.Name;
                """, cancellationToken: cancellationToken))).AsList();
            foreach (var row in rows)
                if (row.IsEncrypted)
                    row.EntryValue = Decrypt(row.EntryValue);
            return rows;
        }

        public async Task<IReadOnlyList<TwConfigurationNest>> GetConfigurationNestAsync(CancellationToken cancellationToken = default)
        {
            var flat = await GetFlatConfigurationAsync(cancellationToken);
            return flat.GroupBy(row => new { row.GroupId, row.GroupName, row.GroupDescription })
                .Select(group => new TwConfigurationNest
                {
                    Id = group.Key.GroupId,
                    Name = group.Key.GroupName,
                    Description = group.Key.GroupDescription,
                    Entries = group.Select(row => new TwConfigurationEntry
                    {
                        Id = row.EntryId, ConfigurationGroupId = row.GroupId, Name = row.EntryName,
                        Value = row.EntryValue, Description = row.EntryDescription, IsEncrypted = row.IsEncrypted,
                        DataType = row.DataType.ToLowerInvariant()
                    }).ToList()
                }).ToList();
        }

        public async Task<TwMenuItem?> GetMenuItemByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            return await session.Connection.QuerySingleOrDefaultAsync<TwMenuItem>(new CommandDefinition("SELECT Id, Name, Link, Ordinal FROM MenuItem WHERE Id = @Id;", new { Id = id }, cancellationToken: cancellationToken));
        }

        public async Task<int> InsertMenuItemAsync(TwMenuItem menuItem, CancellationToken cancellationToken = default)
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("INSERT INTO MenuItem (Name, Link, Ordinal) VALUES (@Name, @Link, @Ordinal); SELECT LAST_INSERT_ID();", menuItem, cancellationToken: cancellationToken));
        }

        public async Task<int> UpdateMenuItemAsync(TwMenuItem menuItem, CancellationToken cancellationToken = default)
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await session.Connection.ExecuteAsync(new CommandDefinition("UPDATE MenuItem SET Name = @Name, Link = @Link, Ordinal = @Ordinal WHERE Id = @Id;", menuItem, cancellationToken: cancellationToken));
            return menuItem.Id;
        }

        public async Task DeleteMenuItemAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await session.Connection.ExecuteAsync(new CommandDefinition("DELETE FROM MenuItem WHERE Id = @Id;", new { Id = id }, cancellationToken: cancellationToken));
        }

        public async Task<string?> GetEntryValueAsync(string groupName, string entryName, CancellationToken cancellationToken = default)
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            var row = await session.Connection.QuerySingleOrDefaultAsync<TwConfigurationEntry>(new CommandDefinition("SELECT CE.Value, CE.IsEncrypted FROM ConfigurationEntry CE INNER JOIN ConfigurationGroup CG ON CG.Id = CE.ConfigurationGroupId WHERE CG.Name = @GroupName AND CE.Name = @EntryName;", new { GroupName = groupName, EntryName = entryName }, cancellationToken: cancellationToken));
            return row is null ? null : Decrypt(row).Value;
        }

        public Task<TwWikiDatabaseStatistics?> GetDatabaseMetricsAsync(CancellationToken cancellationToken = default)
            => Task.FromException<TwWikiDatabaseStatistics?>(new NotSupportedException("Database metrics require the remaining MySQL operations migration."));

        private static TwConfigurationEntry Decrypt(TwConfigurationEntry entry)
        {
            if (entry.IsEncrypted) entry.Value = Decrypt(entry.Value);
            return entry;
        }

        private static string Decrypt(string value)
        {
            try { return SecurityUtility.DecryptString(SecurityUtility.MachineKey, value); }
            catch { return string.Empty; }
        }
    }
}
