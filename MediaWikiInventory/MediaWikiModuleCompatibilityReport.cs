using System.Text.Json;

namespace MediaWikiInventory;

public sealed record MediaWikiModuleCompatibilityReport(
    string ReportVersion,
    string SourceExportSha256,
    string ExecutionMode,
    string ReferenceStatus,
    string StorageImportStatus,
    IReadOnlyList<MediaWikiModuleCompatibilityCase> Cases,
    IReadOnlyList<string> UnevaluatedModules,
    IReadOnlyList<MediaWikiCompatibilityGap> Gaps);

public sealed record MediaWikiModuleCompatibilityCase(
    string CaseId,
    string ModuleTitle,
    string Category,
    string Status,
    string? Invocation,
    string? ExpectedOutputProvenance,
    string? ActualOutput,
    IReadOnlyList<string> Diagnostics,
    string Notes);

public sealed record MediaWikiCompatibilityGap(
    string ModuleTitle,
    string Classification,
    string RequiredCorrection,
    string Priority);

public static class MediaWikiModuleCompatibilityReportBuilder
{
    public static MediaWikiModuleCompatibilityReport Create(MediaWikiModuleInventoryReport inventory, string storageImportStatus = "NOT_RUN")
    {
        var cases = inventory.RepresentativeModules.Select((selection, index) =>
        {
            var module = inventory.Modules.Single(value => string.Equals(value.Title, selection.Title, StringComparison.OrdinalIgnoreCase));
            var invocation = module.InvocationSites.FirstOrDefault();
            var status = module.UnsupportedApiReferences.Count > 0 ? "UNSUPPORTED" : "NOT_EVALUATED";
            var diagnostics = module.UnsupportedApiReferences.Count > 0
                ? module.UnsupportedApiReferences.Select(value => "UnsupportedApi:" + value).ToArray()
                : new[] { "ReferenceOutputMissing", "RealInvocationMissing" };
            return new MediaWikiModuleCompatibilityCase(
                $"real-module-{index + 1:00}", selection.Title, selection.Category, status,
                invocation is null ? null : $"#invoke:{selection.Title}|{invocation.Function}", null, null,
                diagnostics,
                "The module has no selected invocation and no independent MediaWiki reference output; storage import alone cannot classify compatibility.");
        }).ToArray();
        var gaps = inventory.Modules.Where(value => value.UnsupportedApiReferences.Count > 0 || value.DynamicDependencyWarnings.Count > 0)
            .Select(value => new MediaWikiCompatibilityGap(value.Title,
                value.UnsupportedApiReferences.Count > 0 ? "Unsupported API" : "Dynamic dependency",
                value.UnsupportedApiReferences.Count > 0 ? "Document or implement a bounded API adapter; do not expand worker capabilities." : "Resolve through an approved contextual dependency snapshot.",
                value.UnsupportedApiReferences.Count > 0 ? "high" : "medium"))
            .OrderBy(value => value.ModuleTitle, StringComparer.OrdinalIgnoreCase).ToArray();
        var known = inventory.Modules.Select(value => MediaWikiScribuntoScanner.NormalizeModule(value.Title)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        gaps = gaps.Concat(inventory.Modules.SelectMany(value => value.StaticDependencies.Where(dependency => !known.Contains(MediaWikiScribuntoScanner.NormalizeModule(dependency)))
                .Select(dependency => new MediaWikiCompatibilityGap(value.Title, "Missing dependency", $"Import or explicitly classify dependency '{dependency}' before execution.", "high"))))
            .GroupBy(value => value.ModuleTitle + "\u001f" + value.Classification, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .OrderBy(value => value.ModuleTitle, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.Classification, StringComparer.Ordinal).ToArray();
        gaps = gaps.Concat(cases.Where(value => value.Invocation is null)
                .Select(value => new MediaWikiCompatibilityGap(value.ModuleTitle, "Invocation input missing", "Capture and review a real invocation with arguments and an independent MediaWiki reference before execution.", "high")))
            .GroupBy(value => value.ModuleTitle + "\u001f" + value.Classification, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .OrderBy(value => value.ModuleTitle, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.Classification, StringComparer.Ordinal).ToArray();
        var selectedTitles = cases.Select(value => value.ModuleTitle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unevaluated = inventory.Modules.Select(value => value.Title).Where(title => !selectedTitles.Contains(title)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new("cw-m3-006c-v1", inventory.SourceSha256,
            storageImportStatus == "VERIFIED" ? "disposable-imported" : "offline-inventory-only",
            "NOT_EVALUATED", storageImportStatus, cases, unevaluated, gaps);
    }

    public static string Serialize(MediaWikiModuleCompatibilityReport report)
        => JsonSerializer.Serialize(report, MediaWikiModuleInventory.JsonOptions());
}
