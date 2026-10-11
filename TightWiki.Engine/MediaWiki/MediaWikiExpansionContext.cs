using System.Globalization;
using TightWiki.Plugin;
using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

/// <summary>
/// Immutable context for one imported page render. The viewed page remains
/// stable while a template is expanded; template provenance is tracked
/// separately in the dependency result.
/// </summary>
public sealed record MediaWikiExpansionContext(
    string CurrentPageTitle,
    string? CurrentNamespaceId,
    string? CurrentNamespaceName,
    string? SourceWikiIdentity,
    string? SourcePageId,
    string? SourceRevisionId,
    int? TargetPageId,
    int? TargetRevision,
    DateTime? RevisionTimestampUtc,
    string? ContentModel,
    CultureInfo Culture,
    TimeZoneInfo TimeZone,
    DateTimeOffset NowUtc,
    string SiteName,
    string ContentLanguage)
{
    public static MediaWikiExpansionContext From(
        TwMediaWikiRenderSource provenance,
        TwConfiguration configuration,
        TimeProvider timeProvider,
        CultureInfo? culture = null,
        TimeZoneInfo? timeZone = null)
    {
        var selectedCulture = culture ?? CultureInfo.CurrentUICulture;
        var selectedTimeZone = timeZone ?? ResolveTimeZone(configuration.DefaultTimeZone);
        return new(
            provenance.SourceTitle ?? string.Empty,
            provenance.SourceNamespaceId,
            provenance.SourceNamespaceName,
            provenance.SourceWikiIdentity,
            provenance.SourcePageId,
            provenance.SourceRevisionId,
            provenance.TargetPageId,
            provenance.TargetRevision,
            provenance.SourceTimestampUtc,
            provenance.ContentModel,
            selectedCulture,
            selectedTimeZone,
            timeProvider.GetUtcNow(),
            string.IsNullOrWhiteSpace(configuration.Name) ? configuration.ApplicationName : configuration.Name,
            string.IsNullOrWhiteSpace(configuration.ContentLanguage) ? "en" : configuration.ContentLanguage);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
