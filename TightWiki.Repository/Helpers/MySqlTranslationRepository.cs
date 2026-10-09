using Dapper;
using System.Globalization;
using TightWiki.Library;
using TightWiki.Library.Database;
using TightWiki.Plugin.Interfaces.Persistence;

namespace TightWiki.Repository.Helpers;

/// <summary>Read-only MySQL translation lookup with a whitelist for identifier-based language columns.</summary>
public sealed class MySqlTranslationRepository : ITwApplicationTranslationRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly SupportedCultures _supportedCultures = new();

    public MySqlTranslationRepository(IDatabaseConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<string?> GetTranslationAsync(string language, string english, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var languageNames = ResolveLanguageNames(language).ToList();
        if (languageNames.Count == 0) return null;

        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        foreach (var languageName in languageNames)
        {
            if (languageName.Equals("English", StringComparison.OrdinalIgnoreCase)) return english;
            var column = $"`{languageName.Replace("`", "``", StringComparison.Ordinal)}`";
            var translation = await session.Connection.ExecuteScalarAsync<string?>(new CommandDefinition($"SELECT {column} FROM Translation WHERE English = @English LIMIT 1;", new { English = english }, cancellationToken: cancellationToken));
            if (!string.IsNullOrEmpty(translation)) return translation;
        }

        return null;
    }

    private IEnumerable<string> ResolveLanguageNames(string language)
    {
        if (_supportedCultures.TryGetByName(language, out var named))
        {
            yield return named.Name;
            yield break;
        }

        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { yield break; }

        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            if (_supportedCultures.TryGetByCode(current.Name, out var supported))
                yield return supported.Name;
        }
    }
}
