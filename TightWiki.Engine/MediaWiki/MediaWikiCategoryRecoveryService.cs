using Microsoft.Extensions.Logging;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

public interface ITwCategoryRecoveryPageSource
{
    Task<TwCategoryIndexPageSourceResult> ResolveCurrentAsync(
        TwCategoryRecoveryTarget target,
        TwCategoryIndexMutationKind mutationKind,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves an active page and then passes its authoritative current revision
/// through the existing category source boundary. It never accepts a client
/// revision as authoritative.
/// </summary>
public sealed class ApplicationTwCategoryRecoveryPageSource(
    ITwApplicationPageRepository pageRepository,
    ITwCategoryIndexPageSource categoryPageSource) : ITwCategoryRecoveryPageSource
{
    public async Task<TwCategoryIndexPageSourceResult> ResolveCurrentAsync(
        TwCategoryRecoveryTarget target,
        TwCategoryIndexMutationKind mutationKind,
        CancellationToken cancellationToken = default)
    {
        if (target is null || target.PageId <= 0)
            return new(TwCategoryIndexPageSourceDisposition.InvalidRequest, DiagnosticCode: "InvalidPageId");

        var page = await pageRepository.GetPageByIdAsync(target.PageId, cancellationToken: cancellationToken);
        if (page is null || page.Revision <= 0 || page.MostCurrentRevision != page.Revision)
            return new(TwCategoryIndexPageSourceDisposition.SourceUnavailable, DiagnosticCode: "SourcePageUnavailable");

        string navigation;
        try
        {
            navigation = new TwNamespaceNavigation(page.Navigation).Canonical;
        }
        catch
        {
            return new(TwCategoryIndexPageSourceDisposition.InvalidRequest, CurrentRevision: page.Revision, DiagnosticCode: "InvalidPageNavigation");
        }
        if (!string.IsNullOrWhiteSpace(target.ExpectedNavigation)
            && !string.Equals(navigation, target.ExpectedNavigation, StringComparison.OrdinalIgnoreCase))
            return new(TwCategoryIndexPageSourceDisposition.InvalidRequest, CurrentRevision: page.Revision, DiagnosticCode: "PageNavigationMismatch");

        return await categoryPageSource.ResolveAsync(
            new TwCategoryIndexingRequest(target.PageId, navigation, page.Revision, mutationKind),
            cancellationToken);
    }
}

public interface ITwCategoryRecoveryService
{
    Task<TwCategoryRecoveryPlan> PlanAsync(
        TwCategoryRecoveryPlanRequest request,
        Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage,
        CancellationToken cancellationToken = default);

    Task<TwCategoryRecoveryExecutionResult> ExecuteAsync(
        TwCategoryRecoveryExecutionRequest request,
        Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Source-only category recovery orchestration. Planning is read-only. Execute
/// re-authorizes the operation and every target, re-resolves the current page,
/// and delegates writes to the existing reconciliation service.
/// </summary>
public sealed class TwCategoryRecoveryService(
    ITwCategoryRecoveryPageSource pageSource,
    ITwCategoryIndexExtractor extractor,
    ITwCategoryMembershipRepository membershipRepository,
    ITwCategoryMembershipApplicationService applicationService,
    ILogger<TwCategoryRecoveryService> logger) : ITwCategoryRecoveryService
{
    public async Task<TwCategoryRecoveryPlan> PlanAsync(
        TwCategoryRecoveryPlanRequest request,
        Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeTargets(request?.Targets, out var targets, out var validationCode))
            return InvalidPlan(validationCode);
        var correlationId = Guid.NewGuid();
        var startedAtUtc = DateTime.UtcNow;
        if (authorizeOperation is null || authorizePage is null)
            return new(TwCategoryRecoveryPlanDisposition.Unauthorized, ScopeFingerprint(targets), [], DiagnosticCode: "RecoveryAuthorizationRequired", Summary: CreatePlanSummary(correlationId, startedAtUtc, targets.Count, []));

        cancellationToken.ThrowIfCancellationRequested();
        if (!await authorizeOperation(new(TwCategoryRecoveryOperation.Preview, request!.ActorUserId), cancellationToken))
            return new(TwCategoryRecoveryPlanDisposition.Unauthorized, ScopeFingerprint(targets), [], DiagnosticCode: "RecoveryAuthorizationRejected", Summary: CreatePlanSummary(correlationId, startedAtUtc, targets.Count, []));

        var fingerprint = ScopeFingerprint(targets);
        var entries = new List<TwCategoryRecoveryPlanEntry>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = await PlanTargetAsync(target, request.ActorUserId, authorizePage, cancellationToken);
            entries.Add(entry);
        }

        var disposition = entries.Any(entry => entry.Disposition is TwCategoryRecoveryPlanDisposition.Unauthorized or TwCategoryRecoveryPlanDisposition.InvalidRequest)
            ? TwCategoryRecoveryPlanDisposition.Unauthorized
            : entries.Any(entry => entry.Disposition == TwCategoryRecoveryPlanDisposition.RepositoryUnavailable)
                ? TwCategoryRecoveryPlanDisposition.RepositoryUnavailable
                : entries.Any(entry => entry.Disposition == TwCategoryRecoveryPlanDisposition.IncompleteExtraction)
                    ? TwCategoryRecoveryPlanDisposition.IncompleteExtraction
                    : entries.Any(entry => entry.Disposition is TwCategoryRecoveryPlanDisposition.NeedsReindex or TwCategoryRecoveryPlanDisposition.Unindexed or TwCategoryRecoveryPlanDisposition.Inconsistent or TwCategoryRecoveryPlanDisposition.Unsupported)
                        ? TwCategoryRecoveryPlanDisposition.NeedsReindex
                        : TwCategoryRecoveryPlanDisposition.NoChange;

        return new(disposition, fingerprint, entries, new(fingerprint, targets.Count), Summary: CreatePlanSummary(correlationId, startedAtUtc, targets.Count, entries));
    }

    public async Task<TwCategoryRecoveryExecutionResult> ExecuteAsync(
        TwCategoryRecoveryExecutionRequest request,
        Func<TwCategoryRecoveryOperationAuthorizationContext, CancellationToken, Task<bool>>? authorizeOperation,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>>? authorizePage,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeTargets(request?.Targets, out var targets, out var validationCode))
            return InvalidExecution(validationCode);
        if (request!.Mode == TwCategoryRecoveryExecutionMode.DurableExecutionRequested)
            return InvalidExecution("DurableExecutionRequiresDurableService");
        var correlationId = Guid.NewGuid();
        var startedAtUtc = DateTime.UtcNow;
        if (authorizeOperation is null || authorizePage is null)
            return new(TwCategoryRecoveryExecutionDisposition.Unauthorized, [], DiagnosticCode: "RecoveryAuthorizationRequired", Summary: CreateExecutionSummary(correlationId, startedAtUtc, targets.Count, []));

        var fingerprint = ScopeFingerprint(targets);
        var start = request!.Checkpoint?.NextTargetIndex ?? 0;
        if (request.Checkpoint is not null
            && (!request.Checkpoint.IsBounded
                || !string.Equals(request.Checkpoint.ScopeFingerprint, fingerprint, StringComparison.Ordinal)
                || start > targets.Count))
            return new(TwCategoryRecoveryExecutionDisposition.InvalidRequest, [], DiagnosticCode: "InvalidRecoveryCheckpoint", Summary: CreateExecutionSummary(correlationId, startedAtUtc, targets.Count, []));

        if (cancellationToken.IsCancellationRequested)
            return new(TwCategoryRecoveryExecutionDisposition.Cancelled, [], DiagnosticCode: "CancellationRequested", Summary: CreateExecutionSummary(correlationId, startedAtUtc, targets.Count, []));
        if (!await authorizeOperation(new(TwCategoryRecoveryOperation.Execute, request.ActorUserId), cancellationToken))
            return new(TwCategoryRecoveryExecutionDisposition.Unauthorized, [], DiagnosticCode: "RecoveryAuthorizationRejected", Summary: CreateExecutionSummary(correlationId, startedAtUtc, targets.Count, []));

        var entries = new List<TwCategoryRecoveryExecutionEntry>();
        for (var index = start; index < targets.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(TwCategoryRecoveryExecutionDisposition.Cancelled, entries, new(fingerprint, index), "CancellationRequested", CreateExecutionSummary(correlationId, startedAtUtc, targets.Count, entries));
            var target = targets[index];
            var entry = await ExecuteTargetAsync(target, request.ActorUserId, authorizePage, cancellationToken);
            entries.Add(entry);
            if (entry.Disposition == TwCategoryRecoveryExecutionDisposition.Cancelled)
                return new(TwCategoryRecoveryExecutionDisposition.Cancelled, entries, new(fingerprint, index + 1), "CancellationRequested", CreateExecutionSummary(correlationId, startedAtUtc, targets.Count, entries));
        }

        var overall = entries.Any(entry => entry.Disposition == TwCategoryRecoveryExecutionDisposition.PersistenceFailure)
            ? TwCategoryRecoveryExecutionDisposition.PersistenceFailure
            : entries.Any(entry => entry.Disposition == TwCategoryRecoveryExecutionDisposition.RepositoryUnavailable)
                ? TwCategoryRecoveryExecutionDisposition.RepositoryUnavailable
                : entries.Any(entry => entry.Disposition == TwCategoryRecoveryExecutionDisposition.RevisionConflict)
                    ? TwCategoryRecoveryExecutionDisposition.RevisionConflict
                    : entries.Any(entry => entry.Disposition == TwCategoryRecoveryExecutionDisposition.IncompleteExtraction)
                        ? TwCategoryRecoveryExecutionDisposition.IncompleteExtraction
                        : entries.Any(entry => entry.Disposition == TwCategoryRecoveryExecutionDisposition.Reindexed)
                            ? TwCategoryRecoveryExecutionDisposition.Reindexed
                    : entries.Any(entry => entry.Disposition == TwCategoryRecoveryExecutionDisposition.PageMissing)
                        ? TwCategoryRecoveryExecutionDisposition.PageMissing
                        : TwCategoryRecoveryExecutionDisposition.NoChange;
        return new(overall, entries, new(fingerprint, targets.Count), Summary: CreateExecutionSummary(correlationId, startedAtUtc, targets.Count, entries));
    }

    private async Task<TwCategoryRecoveryPlanEntry> PlanTargetAsync(
        TwCategoryRecoveryTarget target,
        Guid? actorUserId,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>> authorizePage,
        CancellationToken cancellationToken)
    {
        TwCategoryIndexPageSourceResult source;
        try
        {
            source = await pageSource.ResolveCurrentAsync(target, TwCategoryIndexMutationKind.AuthorizedMaintenance, cancellationToken);
        }
        catch (NotSupportedException)
        {
            return RedactedPlan(TwCategoryRecoveryPlanDisposition.RepositoryUnavailable, "CategorySourceUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return RedactedPlan(TwCategoryRecoveryPlanDisposition.PageUnavailable, "SourcePageUnavailable");
        }

        if (source.Page is null)
        {
            return RedactedPlan(
                source.Disposition == TwCategoryIndexPageSourceDisposition.RevisionConflict
                    ? TwCategoryRecoveryPlanDisposition.RevisionChanged
                    : source.Disposition == TwCategoryIndexPageSourceDisposition.InvalidRequest
                        ? TwCategoryRecoveryPlanDisposition.InvalidRequest
                        : TwCategoryRecoveryPlanDisposition.PageMissing,
                source.DiagnosticCode ?? "SourcePageUnavailable");
        }

        var page = source.Page;
        if (!await authorizePage(new(TwCategoryRecoveryOperation.Preview, page.PageId, page.Navigation, page.Revision, actorUserId), cancellationToken))
            return RedactedPlan(TwCategoryRecoveryPlanDisposition.Unauthorized, "PageAuthorizationRejected");

        try
        {
            var consistency = await membershipRepository.GetIndexConsistencyAsync(page.PageId, cancellationToken);
            var plan = await extractor.ExtractAsync(page, cancellationToken);
            if (!plan.CanReplaceExistingMembership)
            {
                var diagnostic = plan.BlockingDiagnostics.FirstOrDefault() ?? "IncompleteExtraction";
                var disposition = diagnostic.StartsWith("Unsupported", StringComparison.Ordinal)
                    ? TwCategoryRecoveryPlanDisposition.Unsupported
                    : TwCategoryRecoveryPlanDisposition.IncompleteExtraction;
                return new(page.PageId, page.Navigation, page.Revision, disposition, TwCategoryIndexConsistencyState.Incomplete, false, diagnostic);
            }
            if (!MediaWikiCategoryIndexPlanner.TryCreateReplacementRequest(page.Provenance, plan, Guid.NewGuid(), DateTime.UtcNow, out var replacement))
                return new(page.PageId, page.Navigation, page.Revision, TwCategoryRecoveryPlanDisposition.IncompleteExtraction, TwCategoryIndexConsistencyState.Incomplete, false, "ReplacementRequestInvalid");

            if (consistency.State == TwCategoryIndexConsistencyState.Unavailable)
                return new(page.PageId, page.Navigation, page.Revision, TwCategoryRecoveryPlanDisposition.RepositoryUnavailable, consistency.State, false, consistency.DiagnosticCode ?? "CategoryRepositoryUnavailable");
            if (consistency.State != TwCategoryIndexConsistencyState.Current)
            {
                var disposition = consistency.State == TwCategoryIndexConsistencyState.Unindexed
                    ? TwCategoryRecoveryPlanDisposition.Unindexed
                    : TwCategoryRecoveryPlanDisposition.Inconsistent;
                return new(page.PageId, page.Navigation, page.Revision, disposition, consistency.State, true, consistency.DiagnosticCode ?? "CategoryIndexRequiresReconciliation");
            }

            var existing = await membershipRepository.GetMembershipsByPageIdAsync(page.PageId, cancellationToken);
            var replacementDisposition = TwCategoryMembershipReplacementPolicy.Classify(replacement, page.Revision, existing);
            if (replacement.DesiredMemberships.Count == 0 && existing.Count == 0)
                return new(page.PageId, page.Navigation, page.Revision, TwCategoryRecoveryPlanDisposition.IndexedEmpty, consistency.State, true, "VerifiedEmptyMembershipSet");
            return replacementDisposition == TwCategoryMembershipReplacementDisposition.NoChange
                ? new(page.PageId, page.Navigation, page.Revision, TwCategoryRecoveryPlanDisposition.NoChange, consistency.State, true, "VerifiedMembershipsCurrent")
                : new(page.PageId, page.Navigation, page.Revision, TwCategoryRecoveryPlanDisposition.NeedsReindex, consistency.State, true, "MembershipsDifferFromAuthoritativeSource");
        }
        catch (NotSupportedException)
        {
            return new(page.PageId, page.Navigation, page.Revision, TwCategoryRecoveryPlanDisposition.RepositoryUnavailable, TwCategoryIndexConsistencyState.Unavailable, false, "CategoryRepositoryUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(page.PageId, page.Navigation, page.Revision, TwCategoryRecoveryPlanDisposition.RepositoryUnavailable, TwCategoryIndexConsistencyState.Unavailable, false, "CategoryRecoveryPlanningFailed");
        }
    }

    private async Task<TwCategoryRecoveryExecutionEntry> ExecuteTargetAsync(
        TwCategoryRecoveryTarget target,
        Guid? actorUserId,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>> authorizePage,
        CancellationToken cancellationToken)
    {
        TwCategoryIndexPageSourceResult source;
        try
        {
            source = await pageSource.ResolveCurrentAsync(target, TwCategoryIndexMutationKind.AuthorizedMaintenance, cancellationToken);
        }
        catch (NotSupportedException)
        {
            return RedactedExecution(TwCategoryRecoveryExecutionDisposition.RepositoryUnavailable, "CategorySourceUnavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RedactedExecution(TwCategoryRecoveryExecutionDisposition.Cancelled, "CancellationRequested");
        }
        catch
        {
            return RedactedExecution(TwCategoryRecoveryExecutionDisposition.RepositoryUnavailable, "CategorySourceUnavailable");
        }

        if (source.Page is null)
        {
            return RedactedExecution(
                source.Disposition == TwCategoryIndexPageSourceDisposition.RevisionConflict
                    ? TwCategoryRecoveryExecutionDisposition.RevisionConflict
                    : source.Disposition == TwCategoryIndexPageSourceDisposition.InvalidRequest
                        ? TwCategoryRecoveryExecutionDisposition.InvalidRequest
                        : TwCategoryRecoveryExecutionDisposition.PageMissing,
                source.DiagnosticCode ?? "SourcePageUnavailable");
        }

        var page = source.Page;
        if (!await authorizePage(new(TwCategoryRecoveryOperation.Execute, page.PageId, page.Navigation, page.Revision, actorUserId), cancellationToken))
            return RedactedExecution(TwCategoryRecoveryExecutionDisposition.Unauthorized, "PageAuthorizationRejected");

        var result = await applicationService.ReconcileAsync(
            new TwCategoryReconciliationRequest(page.PageId, page.Navigation, page.Revision, TwCategoryIndexMutationKind.AuthorizedMaintenance, actorUserId),
            (_, _) => Task.FromResult(true),
            cancellationToken);
        logger.LogInformation("Category recovery target completed with disposition {Disposition}.", result.Disposition);
        return result.Disposition switch
        {
            TwCategoryReconciliationDisposition.Reconciled => new(page.PageId, page.Navigation, page.Revision, result.ResolvedAuthoritativeRevision, TwCategoryRecoveryExecutionDisposition.Reindexed, result.DiagnosticCode),
            TwCategoryReconciliationDisposition.NoChange => new(page.PageId, page.Navigation, page.Revision, result.ResolvedAuthoritativeRevision, TwCategoryRecoveryExecutionDisposition.NoChange, result.DiagnosticCode),
            TwCategoryReconciliationDisposition.Incomplete => new(page.PageId, page.Navigation, page.Revision, result.ResolvedAuthoritativeRevision, TwCategoryRecoveryExecutionDisposition.IncompleteExtraction, result.DiagnosticCode),
            TwCategoryReconciliationDisposition.Superseded => new(page.PageId, page.Navigation, page.Revision, result.ResolvedAuthoritativeRevision, TwCategoryRecoveryExecutionDisposition.RevisionConflict, result.DiagnosticCode),
            TwCategoryReconciliationDisposition.Unauthorized => RedactedExecution(TwCategoryRecoveryExecutionDisposition.Unauthorized, result.DiagnosticCode),
            TwCategoryReconciliationDisposition.Cancelled => new(page.PageId, page.Navigation, page.Revision, result.ResolvedAuthoritativeRevision, TwCategoryRecoveryExecutionDisposition.Cancelled, result.DiagnosticCode),
            TwCategoryReconciliationDisposition.Unavailable => RedactedExecution(TwCategoryRecoveryExecutionDisposition.RepositoryUnavailable, result.DiagnosticCode),
            TwCategoryReconciliationDisposition.Invalid => new(page.PageId, page.Navigation, page.Revision, result.ResolvedAuthoritativeRevision, TwCategoryRecoveryExecutionDisposition.InvalidRequest, result.DiagnosticCode),
            _ => new(page.PageId, page.Navigation, page.Revision, result.ResolvedAuthoritativeRevision, TwCategoryRecoveryExecutionDisposition.PersistenceFailure, result.DiagnosticCode ?? "CategoryRecoveryFailed")
        };
    }

    private static bool TryNormalizeTargets(
        IReadOnlyList<TwCategoryRecoveryTarget>? input,
        out IReadOnlyList<TwCategoryRecoveryTarget> targets,
        out string code)
    {
        if (!TwCategoryRecoveryScope.TryCreate(input, out var scope, out code))
        {
            targets = [];
            return false;
        }
        targets = scope.Targets;
        return true;
    }

    private static string ScopeFingerprint(IReadOnlyList<TwCategoryRecoveryTarget> targets)
        => TwCategoryRecoveryScope.TryCreate(targets, out var scope, out _)
            ? scope.Fingerprint
            : string.Empty;

    private static TwCategoryRecoverySummary CreatePlanSummary(
        Guid correlationId,
        DateTime startedAtUtc,
        int targetCount,
        IReadOnlyList<TwCategoryRecoveryPlanEntry> entries)
    {
        var counts = entries
            .Select(entry => entry.Disposition switch
            {
                TwCategoryRecoveryPlanDisposition.NoChange => TwCategoryRecoveryOutcomeKind.NoChange,
                TwCategoryRecoveryPlanDisposition.NeedsReindex => TwCategoryRecoveryOutcomeKind.NeedsReindex,
                TwCategoryRecoveryPlanDisposition.IndexedEmpty => TwCategoryRecoveryOutcomeKind.IndexedEmpty,
                TwCategoryRecoveryPlanDisposition.Unindexed => TwCategoryRecoveryOutcomeKind.Unindexed,
                TwCategoryRecoveryPlanDisposition.Inconsistent => TwCategoryRecoveryOutcomeKind.Inconsistent,
                TwCategoryRecoveryPlanDisposition.Unsupported => TwCategoryRecoveryOutcomeKind.Unsupported,
                TwCategoryRecoveryPlanDisposition.IncompleteExtraction => TwCategoryRecoveryOutcomeKind.IncompleteExtraction,
                TwCategoryRecoveryPlanDisposition.Unauthorized => TwCategoryRecoveryOutcomeKind.Unauthorized,
                TwCategoryRecoveryPlanDisposition.RepositoryUnavailable => TwCategoryRecoveryOutcomeKind.RepositoryUnavailable,
                TwCategoryRecoveryPlanDisposition.PageMissing or TwCategoryRecoveryPlanDisposition.PageUnavailable => TwCategoryRecoveryOutcomeKind.PageMissing,
                _ => TwCategoryRecoveryOutcomeKind.InvalidRequest
            })
            .GroupBy(outcome => outcome)
            .OrderBy(group => group.Key)
            .Select(group => new TwCategoryRecoveryOutcomeCount(group.Key, group.Count()))
            .ToArray();
        return new(correlationId, startedAtUtc, DateTime.UtcNow, targetCount, entries.Count(entry => entry.PageId is null), counts);
    }

    private static TwCategoryRecoverySummary CreateExecutionSummary(
        Guid correlationId,
        DateTime startedAtUtc,
        int targetCount,
        IReadOnlyList<TwCategoryRecoveryExecutionEntry> entries)
    {
        var counts = entries
            .Select(entry => entry.Disposition switch
            {
                TwCategoryRecoveryExecutionDisposition.Reindexed => TwCategoryRecoveryOutcomeKind.Reindexed,
                TwCategoryRecoveryExecutionDisposition.NoChange => TwCategoryRecoveryOutcomeKind.NoChange,
                TwCategoryRecoveryExecutionDisposition.IncompleteExtraction => TwCategoryRecoveryOutcomeKind.IncompleteExtraction,
                TwCategoryRecoveryExecutionDisposition.RevisionConflict => TwCategoryRecoveryOutcomeKind.RevisionConflict,
                TwCategoryRecoveryExecutionDisposition.PageMissing => TwCategoryRecoveryOutcomeKind.PageMissing,
                TwCategoryRecoveryExecutionDisposition.Unauthorized => TwCategoryRecoveryOutcomeKind.Unauthorized,
                TwCategoryRecoveryExecutionDisposition.RepositoryUnavailable => TwCategoryRecoveryOutcomeKind.RepositoryUnavailable,
                TwCategoryRecoveryExecutionDisposition.PersistenceFailure => TwCategoryRecoveryOutcomeKind.PersistenceFailure,
                TwCategoryRecoveryExecutionDisposition.Cancelled => TwCategoryRecoveryOutcomeKind.Cancelled,
                _ => TwCategoryRecoveryOutcomeKind.InvalidRequest
            })
            .GroupBy(outcome => outcome)
            .OrderBy(group => group.Key)
            .Select(group => new TwCategoryRecoveryOutcomeCount(group.Key, group.Count()))
            .ToArray();
        return new(correlationId, startedAtUtc, DateTime.UtcNow, targetCount, entries.Count(entry => entry.PageId is null), counts);
    }

    private static TwCategoryRecoveryPlan InvalidPlan(string code)
        => new(TwCategoryRecoveryPlanDisposition.InvalidRequest, null, [], DiagnosticCode: code);

    private static TwCategoryRecoveryExecutionResult InvalidExecution(string code)
        => new(TwCategoryRecoveryExecutionDisposition.InvalidRequest, [], DiagnosticCode: code);

    private static TwCategoryRecoveryPlanEntry RedactedPlan(TwCategoryRecoveryPlanDisposition disposition, string code)
        => new(null, null, null, disposition, TwCategoryIndexConsistencyState.Unknown, false, code);

    private static TwCategoryRecoveryExecutionEntry RedactedExecution(TwCategoryRecoveryExecutionDisposition disposition, string? code)
        => new(null, null, null, null, disposition, code);
}
