using TightWiki.Plugin.Interfaces.Persistence;
using System.Globalization;

namespace TightWiki.Translations;

/// <summary>
/// Provider-neutral translation adapter for the existing read-only SQLite
/// translation store. The SQLite-specific file access remains confined to this
/// adapter rather than the localizer.
/// </summary>
public sealed class SqliteTranslationRepository(TranslationDatabase database) : ITwApplicationTranslationRepository
{
    public Task<string?> GetTranslationAsync(string language, string english, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var culture = string.IsNullOrWhiteSpace(language) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language);
        return Task.FromResult(database.TryGetTranslation(culture, english, out var translation) ? translation : null);
    }
}
