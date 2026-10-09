using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using System.Reflection;
using TightWiki.Library;
using TightWiki.Library.Database;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces;
using TightWiki.Plugin.Models.Defaults;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Imports typed records from the canonical defaults artifact into MySQL. The
/// artifact is read-only seed input; it is never attached to or used as the
/// application's runtime database.
/// </summary>
public sealed class MySqlDefaultContentSeeder
{
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private static readonly string[] TranslationColumns = new SupportedCultures().Collection.Select(value => value.Name).ToArray();

    public MySqlDefaultContentSeeder(IDatabaseConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<string> SeedAsync(IReadOnlyCollection<TwDefaultDataType> requested, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        await new MySqlSchemaInitializer().InitializeAsync(_connectionFactory, cancellationToken);

        var configurations = requested.Contains(TwDefaultDataType.Configurations) ? ReadConfigurations() : [];
        var themes = requested.Contains(TwDefaultDataType.Themes) ? ReadThemes() : [];
        var featureTemplates = requested.Contains(TwDefaultDataType.FeatureTemplates) ? ReadFeatureTemplates() : [];
        var translations = requested.Contains(TwDefaultDataType.Translations) ? ReadTranslations() : [];
        var menus = requested.Contains(TwDefaultDataType.Menus) ? ReadMenus() : [];
        var wikiPages = ReadWikiPages(requested);
        string? authorId = null;
        if (wikiPages.Count > 0)
            authorId = await FindAdministratorAsync(cancellationToken)
                ?? throw new InvalidOperationException("MySQL default pages require an existing administrator account; no administrator was found and no account was created.");

        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var configuration in configurations)
            {
                await session.Connection.ExecuteAsync(Command("""
                    INSERT IGNORE INTO DefaultConfiguration
                        (ConfigurationGroupName, ConfigurationEntryName, Value, DataTypeId,
                         ConfigurationEntryDescription, ConfigurationGroupDescription, IsEncrypted, IsRequired)
                    VALUES (@GroupName, @EntryName, @Value, @DataTypeId, @EntryDescription,
                            @GroupDescription, @IsEncrypted, @IsRequired);
                    """, new
                {
                    GroupName = configuration.ConfigurationGroupName,
                    EntryName = configuration.ConfigurationEntryName,
                    configuration.Value,
                    configuration.DataTypeId,
                    EntryDescription = configuration.ConfigurationEntryDescription,
                    GroupDescription = configuration.ConfigurationGroupDescription,
                    configuration.IsEncrypted,
                    configuration.IsRequired
                }, transaction.Transaction, cancellationToken));
                await session.Connection.ExecuteAsync(Command("INSERT IGNORE INTO DataType (Id) VALUES (@Id);", new { Id = configuration.DataTypeId }, transaction.Transaction, cancellationToken));
                await session.Connection.ExecuteAsync(Command("INSERT IGNORE INTO ConfigurationGroup (Name, Description) VALUES (@Name, @Description);", new { Name = configuration.ConfigurationGroupName, Description = configuration.ConfigurationGroupDescription }, transaction.Transaction, cancellationToken));
                await session.Connection.ExecuteAsync(Command("""
                    INSERT IGNORE INTO ConfigurationEntry
                        (ConfigurationGroupId, Name, Value, DataTypeId, Description, IsEncrypted, IsRequired)
                    SELECT Id, @EntryName, @Value, @DataTypeId, @Description, @IsEncrypted, @IsRequired
                    FROM ConfigurationGroup WHERE Name = @GroupName;
                    """, new
                {
                    GroupName = configuration.ConfigurationGroupName,
                    EntryName = configuration.ConfigurationEntryName,
                    configuration.Value,
                    configuration.DataTypeId,
                    Description = configuration.ConfigurationEntryDescription,
                    configuration.IsEncrypted,
                    configuration.IsRequired
                }, transaction.Transaction, cancellationToken));
            }

            foreach (var theme in themes)
            {
                await session.Connection.ExecuteAsync(Command("""
                    INSERT IGNORE INTO DefaultThemes
                        (Name, DelimitedFiles, ClassNavBar, ClassNavLink, ClassDropdown, ClassBranding, EditorTheme)
                    VALUES (@Name, @DelimitedFiles, @ClassNavBar, @ClassNavLink, @ClassDropdown, @ClassBranding, @EditorTheme);
                    INSERT IGNORE INTO Theme
                        (Name, DelimitedFiles, ClassNavBar, ClassNavLink, ClassDropdown, ClassBranding, EditorTheme)
                    VALUES (@Name, @DelimitedFiles, @ClassNavBar, @ClassNavLink, @ClassDropdown, @ClassBranding, @EditorTheme);
                    """, theme, transaction.Transaction, cancellationToken));
            }

            foreach (var template in featureTemplates)
            {
                await session.Connection.ExecuteAsync(Command("INSERT IGNORE INTO DefaultFeatureTemplates (Name, Type, PageName, Description, TemplateText) VALUES (@Name, @Type, @PageName, @Description, @TemplateText);", template, transaction.Transaction, cancellationToken));
            }

            foreach (var translation in translations)
            {
                var parameters = new DynamicParameters();
                foreach (var column in TranslationColumns) parameters.Add(ParameterName(column), translation.Values.GetValueOrDefault(column));
                var columns = string.Join(", ", TranslationColumns.Select(QuoteIdentifier));
                var values = string.Join(", ", TranslationColumns.Select(ParameterName));
                await session.Connection.ExecuteAsync(Command($"INSERT IGNORE INTO Translation ({columns}) VALUES ({values});", parameters, transaction.Transaction, cancellationToken));
            }

            foreach (var menu in menus)
            {
                await session.Connection.ExecuteAsync(Command("INSERT IGNORE INTO MenuItem (Id, Name, Link, Ordinal) VALUES (@Id, @Name, @Link, @Ordinal);", menu, transaction.Transaction, cancellationToken));
            }

            foreach (var page in wikiPages)
            {
                await session.Connection.ExecuteAsync(Command("INSERT IGNORE INTO DefaultWikiPages (Name, Namespace, Navigation, Description, Revision, DataHash, Body) VALUES (@Name, @Namespace, @Navigation, @Description, @Revision, @DataHash, @Body);", page, transaction.Transaction, cancellationToken));
                await session.Connection.ExecuteAsync(Command("""
                    INSERT INTO Page
                        (Name, `Namespace`, Navigation, ActiveNavigation, Description, Revision,
                         CreatedByUserId, CreatedDate, ModifiedByUserId, ModifiedDate)
                    SELECT @Name, @Namespace, @Navigation, @Navigation, @Description, @Revision,
                           @AuthorId, @Now, @AuthorId, @Now
                    FROM DUAL
                    WHERE NOT EXISTS (SELECT 1 FROM Page WHERE Navigation = @Navigation OR ActiveNavigation = @Navigation);
                    """, new { page.Name, page.Namespace, page.Navigation, page.Description, page.Revision, AuthorId = authorId, Now = DateTime.UtcNow }, transaction.Transaction, cancellationToken));
                await session.Connection.ExecuteAsync(Command("""
                    INSERT INTO PageRevision
                        (PageId, Revision, Name, `Namespace`, Navigation, Description, Body, DataHash, ChangeSummary, ModifiedByUserId, ModifiedDate)
                    SELECT P.Id, @Revision, @Name, @Namespace, @Navigation, @Description, @Body, @DataHash, 'Built-in default content', @AuthorId, @Now
                    FROM Page P
                    WHERE P.Navigation = @Navigation
                      AND P.Revision = @Revision
                      AND NOT EXISTS (SELECT 1 FROM PageRevision R WHERE R.PageId = P.Id AND R.Revision = @Revision);
                    """, new { page.Name, page.Namespace, page.Navigation, page.Description, page.Revision, page.Body, page.DataHash, AuthorId = authorId, Now = DateTime.UtcNow }, transaction.Transaction, cancellationToken));
            }

            var sourceVersion = typeof(DefaultsRepository).Assembly.GetName().Version?.ToString() ?? "unknown";
            foreach (var seedKey in requested.Distinct().Select(value => value.ToString()))
                await session.Connection.ExecuteAsync(Command("INSERT INTO MySqlSeedState (SeedKey, SourceVersion, AppliedAtUtc) VALUES (@SeedKey, @SourceVersion, @AppliedAtUtc) ON DUPLICATE KEY UPDATE SourceVersion = VALUES(SourceVersion), AppliedAtUtc = VALUES(AppliedAtUtc);", new { SeedKey = seedKey, SourceVersion = sourceVersion, AppliedAtUtc = DateTime.UtcNow }, transaction.Transaction, cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            return $"MySQL default content seed completed for: {string.Join(", ", requested.Distinct())}.";
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            DeleteSeedSource();
        }
    }

    private async Task<string?> FindAdministratorAsync(CancellationToken cancellationToken)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT AR.UserId FROM AccountRole AR INNER JOIN Role R ON R.Id = AR.RoleId INNER JOIN AspNetUsers U ON U.Id = AR.UserId WHERE R.Name = 'Administrator' ORDER BY AR.UserId LIMIT 1;", cancellationToken: cancellationToken));
    }

    private static List<TwDefaultConfiguration> ReadConfigurations()
        => WithSeedSource(repository => repository.DefaultsFactory.Query<TwDefaultConfiguration>("Scripts\\Defaults\\GetDefaultConfigurations.sql").ToList());

    private static List<TwDefaultTheme> ReadThemes()
        => WithSeedSource(repository => repository.DefaultsFactory.Query<TwDefaultTheme>("Scripts\\Defaults\\GetDefaultThemes.sql").ToList());

    private static List<TwDefaultFeatureTemplate> ReadFeatureTemplates()
        => WithSeedSource(repository => repository.DefaultsFactory.Query<TwDefaultFeatureTemplate>("Scripts\\Defaults\\GetDefaultFeatureTemplates.sql").ToList());

    private static List<CanonicalTranslation> ReadTranslations()
        => WithSqliteSource("SeedSources\\Translations.db", path =>
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", TranslationColumns.Select(QuoteIdentifier))} FROM Translation;";
            using var reader = command.ExecuteReader();
            var results = new List<CanonicalTranslation>();
            while (reader.Read())
            {
                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < TranslationColumns.Length; index++)
                    values[TranslationColumns[index]] = reader.IsDBNull(index) ? null : reader.GetString(index);
                results.Add(new CanonicalTranslation(values));
            }
            return results;
        });

    private static List<CanonicalMenu> ReadMenus()
        => WithSqliteSource("SeedSources\\config.db", path =>
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, Link, Ordinal FROM MenuItem ORDER BY Ordinal, Id;";
            using var reader = command.ExecuteReader();
            var results = new List<CanonicalMenu>();
            while (reader.Read()) results.Add(new CanonicalMenu(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
            return results;
        });

    private static List<TwDefaultWikiPage> ReadWikiPages(IReadOnlyCollection<TwDefaultDataType> requested)
    {
        var pages = new List<TwDefaultWikiPage>();
        foreach (var (type, ns) in new[]
        {
            (TwDefaultDataType.HelpPages, "Wiki Help"),
            (TwDefaultDataType.IncludePages, "Include"),
            (TwDefaultDataType.BuiltinPages, "Builtin")
        })
        {
            if (requested.Contains(type))
                pages.AddRange(WithSeedSource(repository => repository.DefaultsFactory.Query<TwDefaultWikiPage>("Scripts\\Defaults\\GetDefaultWikiPages.sql", new { Namespace = ns }).ToList()));
        }
        return pages;
    }

    private static T WithSeedSource<T>(Func<DefaultsRepository, T> read)
    {
        var path = Path.Combine(Path.GetTempPath(), $"continuumwiki-defaults-{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllBytes(path, EmbeddedResourceReader.LoadBytes("Defaults\\defaults.db"));
            return read(new DefaultsRepository(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static T WithSqliteSource<T>(string resourceName, Func<string, T> read)
    {
        var path = Path.Combine(Path.GetTempPath(), $"continuumwiki-seed-{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllBytes(path, EmbeddedResourceReader.LoadBytes(resourceName));
            return read(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string QuoteIdentifier(string identifier) => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    private static string ParameterName(string identifier) => $"@p_{identifier.Replace(" ", "_", StringComparison.Ordinal)}";

    private sealed record CanonicalTranslation(IReadOnlyDictionary<string, string?> Values);
    private sealed record CanonicalMenu(int Id, string Name, string Link, int Ordinal);

    private static void DeleteSeedSource()
    {
        // Source files are deleted by WithSeedSource immediately after each typed read.
    }

    private static CommandDefinition Command(string sql, object parameters, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
        => new(sql, parameters, transaction, cancellationToken: cancellationToken);
}
