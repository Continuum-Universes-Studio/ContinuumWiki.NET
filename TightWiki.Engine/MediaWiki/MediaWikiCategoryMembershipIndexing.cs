using TightWiki.Plugin;
using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

/// <summary>Canonical identity for a MediaWiki Category namespace target.</summary>
public static class MediaWikiCategoryIdentity
{
    public const string NamespaceName = "Category";

    /// <summary>
    /// Uses the application's existing namespace navigation rules. This is
    /// deliberately not a second MediaWiki title-normalization algorithm.
    /// </summary>
    public static bool TryCanonicalize(string? sourceTitle, out string normalizedTitle, out string canonicalNavigation)
    {
        normalizedTitle = string.Empty;
        canonicalNavigation = string.Empty;
        if (!TwCategoryIdentity.TryCreate(sourceTitle, out var identity)) return false;
        normalizedTitle = identity.NormalizedTitle;
        canonicalNavigation = identity.CanonicalNavigation;
        return true;
    }
}

public enum MediaWikiCategoryIndexCompleteness
{
    Complete,
    Incomplete
}

/// <summary>Immutable source/provenance carried by a category directive.</summary>
public sealed record MediaWikiCategoryMembership(
    string SourcePageId,
    string SourceRevisionId,
    string SourcePageTitle,
    string CategoryTitle,
    string CategoryNavigation,
    string? SortKey,
    long? ImportBatchId,
    string? SourceWikiIdentity,
    int Offset);

/// <summary>
/// A source-only indexing plan. It never writes storage. Incomplete plans
/// explicitly require the caller to preserve the previously verified index.
/// </summary>
public sealed record MediaWikiCategoryIndexPlan(
    MediaWikiCategoryIndexCompleteness Completeness,
    IReadOnlyList<MediaWikiCategoryMembership> DesiredMemberships,
    IReadOnlyList<string> BlockingDiagnostics,
    bool PreserveExistingMembership,
    bool HasCompleteSourceProvenance)
{
    public bool CanReplaceExistingMembership
        => Completeness == MediaWikiCategoryIndexCompleteness.Complete
            && HasCompleteSourceProvenance
            && !PreserveExistingMembership;
}

/// <summary>Builds bounded, immutable category index plans from render output.</summary>
public static class MediaWikiCategoryIndexPlanner
{
    private static readonly HashSet<string> IncompleteDiagnosticCodes = new(StringComparer.Ordinal)
    {
        "AmbiguousTemplate",
        "ExpansionBudgetExceeded",
        "ExpansionDepthExceeded",
        "ExpansionOutputLimitExceeded",
        "ExpansionTimeLimitExceeded",
        "FunctionOutputLimitExceeded",
        "InputLimitExceeded",
        "MissingTemplate",
        "RecursiveScribuntoInvocation",
        "RecursiveTemplate",
        "RestrictedTemplate",
        "ScribuntoArgumentBudgetExceeded",
        "ScribuntoArgumentCountBudgetExceeded",
        "ScribuntoDependencyDepthExceeded",
        "ScribuntoDependencySourceBudgetExceeded",
        "ScribuntoExecutionRejected",
        "ScribuntoIndividualArgumentBudgetExceeded",
        "ScribuntoInvocationBudgetExceeded",
        "ScribuntoModuleSourceBudgetExceeded",
        "ScribuntoResultBudgetExceeded",
        "ScribuntoModuleMissing",
        "ScribuntoWorkerProtocolError",
        "ScribuntoWorkerUnavailable",
        "TemplateResolutionFailed",
        "TemplateSourceUnavailable",
        "UnsupportedExtensionFunction",
        "UnsupportedExtensionTag",
        "UnsupportedParserFunction",
        "UnsupportedTemplateSyntax",
        "UnsupportedContinuumInfobox"
    };

    public static MediaWikiCategoryIndexPlan Build(
        TwMediaWikiRenderSource provenance,
        MediaWikiWikitextRenderResult renderResult)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(renderResult);

        var memberships = renderResult.Categories
            .Where(category => !string.IsNullOrWhiteSpace(category.CanonicalNavigation))
            .Select(category => new MediaWikiCategoryMembership(
                provenance.SourcePageId ?? string.Empty,
                provenance.SourceRevisionId ?? string.Empty,
                provenance.SourceTitle ?? string.Empty,
                category.NormalizedTitle,
                category.CanonicalNavigation,
                category.SortKey,
                provenance.ImportBatchId,
                provenance.SourceWikiIdentity,
                category.Offset))
            .GroupBy(value => $"{value.CategoryNavigation}\u001f{value.SortKey ?? string.Empty}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(value => value.CategoryNavigation, StringComparer.Ordinal)
            .ThenBy(value => value.SortKey, StringComparer.Ordinal)
            .ThenBy(value => value.Offset)
            .ToList();

        var blockingDiagnostics = renderResult.Diagnostics
            .Where(diagnostic => diagnostic.Severity == MediaWikiDiagnosticSeverity.Error
                || IncompleteDiagnosticCodes.Contains(diagnostic.Code))
            .Select(diagnostic => diagnostic.Code)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        if (renderResult.Categories.Any(category => category.SortKey?.Length > TwCategoryMembershipLimits.MaxSortKeyCharacters))
            blockingDiagnostics.Add("CategorySortKeyLimitExceeded");

        var hasCompleteSourceProvenance = !string.IsNullOrWhiteSpace(provenance.SourcePageId)
            && !string.IsNullOrWhiteSpace(provenance.SourceRevisionId)
            && !string.IsNullOrWhiteSpace(provenance.SourceTitle);
        if (!hasCompleteSourceProvenance)
            blockingDiagnostics.Add("MissingSourceProvenance");

        var complete = blockingDiagnostics.Count == 0;
        return new(
            complete ? MediaWikiCategoryIndexCompleteness.Complete : MediaWikiCategoryIndexCompleteness.Incomplete,
            memberships,
            blockingDiagnostics,
            PreserveExistingMembership: !complete,
            hasCompleteSourceProvenance);
    }

    /// <summary>
    /// Converts a complete imported render plan into the future provider
    /// mutation contract. This is a pure adapter and performs no persistence.
    /// </summary>
    public static bool TryCreateReplacementRequest(
        TwMediaWikiRenderSource provenance,
        MediaWikiCategoryIndexPlan plan,
        Guid indexGeneration,
        DateTime indexedAtUtc,
        out TwCategoryMembershipReplacementRequest request)
    {
        request = null!;
        if (!plan.CanReplaceExistingMembership
            || provenance.TargetPageId is not > 0
            || provenance.TargetRevision is not > 0
            || indexGeneration == Guid.Empty
            || indexedAtUtc.Kind != DateTimeKind.Utc)
            return false;

        var memberships = new List<TwCategoryMembershipRecord>(plan.DesiredMemberships.Count);
        foreach (var membership in plan.DesiredMemberships)
        {
            if (!TwCategoryIdentity.TryCreate(membership.CategoryTitle, out var category)) return false;
            memberships.Add(new(
                provenance.TargetPageId.Value,
                category,
                membership.SortKey,
                provenance.TargetRevision.Value,
                membership.SourcePageId,
                membership.SourceRevisionId,
                membership.SourceWikiIdentity,
                indexGeneration,
                indexedAtUtc));
        }

        request = new(
            provenance.TargetPageId.Value,
            provenance.TargetRevision.Value,
            memberships,
            true,
            indexGeneration,
            indexedAtUtc,
            provenance.SourcePageId,
            provenance.SourceRevisionId,
            provenance.SourceWikiIdentity);
        return TwCategoryMembershipReplacementPolicy.Validate(request) is null;
    }
}
