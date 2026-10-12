using Microsoft.Extensions.Configuration;
using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

public interface ITwCategoryAdministrativeRecoveryApi
{
    TwCategoryAdministrativeReadinessResponse Readiness(
        TwCategoryAdministrativeCaller caller);

    Task<TwCategoryAdministrativePlanResponse> PreviewAsync(
        TwCategoryAdministrativeCaller caller,
        TwCategoryAdministrativeRecoveryRequest request,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>> authorizePage,
        CancellationToken cancellationToken = default);

    Task<TwCategoryAdministrativeExecutionResponse> ExecuteAsync(
        TwCategoryAdministrativeCaller caller,
        TwCategoryAdministrativeRecoveryRequest request,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>> authorizePage,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Administrative recovery boundary. It owns no persistence and cannot
/// activate a provider. The configuration gate, trusted session context,
/// operation-specific authorization, readiness evidence, and existing recovery
/// services are all required before execution can reach the durable coordinator.
/// </summary>
public sealed class TwCategoryAdministrativeRecoveryApi(
    IConfiguration configuration,
    ITwCategoryOperationalReadinessService readinessService,
    ITwCategoryOperationalReadinessEvidenceSource evidenceSource,
    ITwCategoryAdministrativeGrantProvider operationAuthorization,
    ITwCategoryRecoveryService recoveryService,
    ITwCategoryDurableRecoveryService durableRecoveryService) : ITwCategoryAdministrativeRecoveryApi
{
    public const string EnabledConfigurationKey = "ContinuumWiki:CategoryRecovery:AdministrativeApi:Enabled";

    public TwCategoryAdministrativeReadinessResponse Readiness(
        TwCategoryAdministrativeCaller caller)
    {
        if (!IsEnabled())
            return new(TwCategoryAdministrativeApiDisposition.Disabled, DiagnosticCode: "CategoryAdministrativeApiDisabled");
        if (!caller.IsAuthenticated)
            return new(TwCategoryAdministrativeApiDisposition.Unauthenticated, DiagnosticCode: "AuthenticationRequired");
        if (!caller.IsTrustedAdministrator)
            return new(TwCategoryAdministrativeApiDisposition.Forbidden, DiagnosticCode: "AdministratorPermissionRequired");

        try
        {
            var snapshot = readinessService.Evaluate(evidenceSource.GetEvidence().Input);
            return new(TwCategoryAdministrativeApiDisposition.Success, snapshot);
        }
        catch
        {
            // The readiness endpoint must not turn an evidence-source failure
            // into a successful or empty readiness result.
            return new(TwCategoryAdministrativeApiDisposition.ProviderUnavailable, DiagnosticCode: "ReadinessEvidenceUnavailable");
        }
    }

    public async Task<TwCategoryAdministrativePlanResponse> PreviewAsync(
        TwCategoryAdministrativeCaller caller,
        TwCategoryAdministrativeRecoveryRequest request,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>> authorizePage,
        CancellationToken cancellationToken = default)
    {
        var access = CheckAccess(caller);
        if (access is not null)
            return new(access.Value, DiagnosticCode: Diagnostic(access.Value));

        if (request is null || request.RequestId == Guid.Empty)
            return new(TwCategoryAdministrativeApiDisposition.InvalidInput, DiagnosticCode: "InvalidRecoveryRequestId");
        if (!TwCategoryRecoveryScope.TryCreate(request.Targets, out var scope, out var validationCode))
            return new(TwCategoryAdministrativeApiDisposition.InvalidInput, DiagnosticCode: validationCode);
        if (authorizePage is null)
            return new(TwCategoryAdministrativeApiDisposition.Forbidden, DiagnosticCode: "PageAuthorizationRequired");

        try
        {
            TwCategoryAdministrativeAuthorizationDecision? authorizationDecision = null;
            async Task<bool> AuthorizeOperation(
                TwCategoryRecoveryOperationAuthorizationContext _,
                CancellationToken token)
            {
                authorizationDecision = await AuthorizeOperationAsync(TwCategoryAdministrativeOperation.Preview, caller, scope, token);
                return authorizationDecision.Allowed;
            }

            var plan = await recoveryService.PlanAsync(
                new(scope.Targets, caller.ActorUserId),
                AuthorizeOperation,
                authorizePage,
                cancellationToken);
            return new(Map(plan, authorizationDecision), plan, plan.DiagnosticCode ?? AuthorizationDiagnostic(authorizationDecision));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(TwCategoryAdministrativeApiDisposition.Cancellation, DiagnosticCode: "CancellationRequested");
        }
        catch
        {
            return new(TwCategoryAdministrativeApiDisposition.ProviderUnavailable, DiagnosticCode: "CategoryRecoveryPreviewUnavailable");
        }
    }

    public async Task<TwCategoryAdministrativeExecutionResponse> ExecuteAsync(
        TwCategoryAdministrativeCaller caller,
        TwCategoryAdministrativeRecoveryRequest request,
        Func<TwCategoryRecoveryPageAuthorizationContext, CancellationToken, Task<bool>> authorizePage,
        CancellationToken cancellationToken = default)
    {
        var access = CheckAccess(caller);
        if (access is not null)
            return new(access.Value, DiagnosticCode: Diagnostic(access.Value));

        if (request is null || request.RequestId is null || request.RequestId == Guid.Empty)
            return new(TwCategoryAdministrativeApiDisposition.InvalidInput, DiagnosticCode: "RecoveryOperationIdRequired");
        if (!TwCategoryRecoveryScope.TryCreate(request.Targets, out var scope, out var validationCode))
            return new(TwCategoryAdministrativeApiDisposition.InvalidInput, DiagnosticCode: validationCode);
        if (authorizePage is null)
            return new(TwCategoryAdministrativeApiDisposition.Forbidden, DiagnosticCode: "PageAuthorizationRequired");

        TwCategoryOperationalReadinessSnapshot readiness;
        try
        {
            var evidence = evidenceSource.GetEvidence();
            var evidenceValidation = TwCategoryOperationalReadinessEvidencePolicy.ValidateForAdministrativeExecution(evidence, DateTime.UtcNow);
            if (!evidenceValidation.Accepted)
                return new(TwCategoryAdministrativeApiDisposition.MutationGated, DiagnosticCode: evidenceValidation.DiagnosticCode);
            readiness = readinessService.Evaluate(evidence.Input);
        }
        catch
        {
            return new(TwCategoryAdministrativeApiDisposition.ProviderUnavailable, DiagnosticCode: "ReadinessEvidenceUnavailable");
        }

        if (readiness.State == TwCategoryOperationalReadinessState.RecoveryIndeterminate)
            return new(TwCategoryAdministrativeApiDisposition.RecoveryIndeterminate, DiagnosticCode: "RecoveryIndeterminate");
        if (readiness.Decision != TwCategoryOperationalDecision.AllowedForReview)
            return new(TwCategoryAdministrativeApiDisposition.MutationGated, DiagnosticCode: "CategoryRecoveryMutationGated");

        var administrativeRequest = new TwCategoryRecoveryAdministrativeRequest(
            request.RequestId.Value,
            scope.Targets,
            caller.ActorUserId,
            TwCategoryRecoveryAdministrationMode.Execute);
        var validation = TwCategoryRecoveryAdministrativePolicy.Validate(administrativeRequest);
        if (validation is not null)
            return new(TwCategoryAdministrativeApiDisposition.InvalidInput, DiagnosticCode: validation);

        try
        {
            TwCategoryAdministrativeAuthorizationDecision? authorizationDecision = null;
            async Task<bool> AuthorizeOperation(
                TwCategoryRecoveryOperationAuthorizationContext _,
                CancellationToken token)
            {
                authorizationDecision = await AuthorizeOperationAsync(TwCategoryAdministrativeOperation.Execute, caller, scope, token);
                return authorizationDecision.Allowed;
            }

            var execution = await durableRecoveryService.ExecuteAsync(
                administrativeRequest.ToDurableExecutionRequest(),
                AuthorizeOperation,
                authorizePage,
                cancellationToken);
            return new(Map(execution, authorizationDecision), execution, execution.DiagnosticCode ?? AuthorizationDiagnostic(authorizationDecision));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(TwCategoryAdministrativeApiDisposition.Cancellation, DiagnosticCode: "CancellationRequested");
        }
        catch
        {
            return new(TwCategoryAdministrativeApiDisposition.ProviderUnavailable, DiagnosticCode: "CategoryRecoveryExecutionUnavailable");
        }
    }

    private async Task<TwCategoryAdministrativeAuthorizationDecision> AuthorizeOperationAsync(
        TwCategoryAdministrativeOperation operation,
        TwCategoryAdministrativeCaller caller,
        TwCategoryRecoveryScope scope,
        CancellationToken cancellationToken)
    {
        if (!caller.IsTrustedAdministrator || caller.ActorUserId is null)
            return new(false, TwCategoryAdministrativeAuthorizationReason.ActorMismatch);
        return await operationAuthorization.EvaluateAsync(
            new(operation, caller.ActorUserId.Value, scope),
            cancellationToken);
    }

    private TwCategoryAdministrativeApiDisposition? CheckAccess(
        TwCategoryAdministrativeCaller caller)
    {
        if (!IsEnabled())
            return TwCategoryAdministrativeApiDisposition.Disabled;
        if (!caller.IsAuthenticated)
            return TwCategoryAdministrativeApiDisposition.Unauthenticated;
        if (!caller.IsTrustedAdministrator)
            return TwCategoryAdministrativeApiDisposition.Forbidden;
        return null;
    }

    private bool IsEnabled()
        => bool.TryParse(configuration[EnabledConfigurationKey], out var enabled) && enabled;

    private static string Diagnostic(TwCategoryAdministrativeApiDisposition disposition)
        => disposition switch
        {
            TwCategoryAdministrativeApiDisposition.Disabled => "CategoryAdministrativeApiDisabled",
            TwCategoryAdministrativeApiDisposition.Unauthenticated => "AuthenticationRequired",
            TwCategoryAdministrativeApiDisposition.Forbidden => "AdministratorPermissionRequired",
            _ => "CategoryAdministrativeOperationUnavailable"
        };

    private static TwCategoryAdministrativeApiDisposition Map(
        TwCategoryRecoveryPlan plan,
        TwCategoryAdministrativeAuthorizationDecision? authorizationDecision = null)
        => plan.Disposition switch
        {
            TwCategoryRecoveryPlanDisposition.NoChange or TwCategoryRecoveryPlanDisposition.IndexedEmpty => TwCategoryAdministrativeApiDisposition.Success,
            TwCategoryRecoveryPlanDisposition.NeedsReindex or TwCategoryRecoveryPlanDisposition.Unindexed or TwCategoryRecoveryPlanDisposition.Inconsistent => TwCategoryAdministrativeApiDisposition.InconsistentIndex,
            TwCategoryRecoveryPlanDisposition.IncompleteExtraction => TwCategoryAdministrativeApiDisposition.IncompleteExpansion,
            TwCategoryRecoveryPlanDisposition.RevisionChanged => TwCategoryAdministrativeApiDisposition.ConflictingRevision,
            TwCategoryRecoveryPlanDisposition.Unsupported => TwCategoryAdministrativeApiDisposition.Unsupported,
            TwCategoryRecoveryPlanDisposition.Unauthorized => MapAuthorization(authorizationDecision),
            TwCategoryRecoveryPlanDisposition.RepositoryUnavailable => TwCategoryAdministrativeApiDisposition.ProviderUnavailable,
            TwCategoryRecoveryPlanDisposition.InvalidRequest => TwCategoryAdministrativeApiDisposition.InvalidInput,
            _ => TwCategoryAdministrativeApiDisposition.PartialResult
        };

    private static TwCategoryAdministrativeApiDisposition Map(
        TwCategoryDurableRecoveryExecutionResult execution,
        TwCategoryAdministrativeAuthorizationDecision? authorizationDecision = null)
        => execution.Disposition switch
        {
            TwCategoryDurableRecoveryDisposition.Completed or TwCategoryDurableRecoveryDisposition.AlreadyCompleted => TwCategoryAdministrativeApiDisposition.Success,
            TwCategoryDurableRecoveryDisposition.PartiallyCompleted => TwCategoryAdministrativeApiDisposition.PartialResult,
            TwCategoryDurableRecoveryDisposition.Interrupted or TwCategoryDurableRecoveryDisposition.Recoverable => TwCategoryAdministrativeApiDisposition.RecoveryIndeterminate,
            TwCategoryDurableRecoveryDisposition.Unauthorized => MapAuthorization(authorizationDecision),
            TwCategoryDurableRecoveryDisposition.GenerationConflict => TwCategoryAdministrativeApiDisposition.ConflictingRevision,
            TwCategoryDurableRecoveryDisposition.ScopeConflict or TwCategoryDurableRecoveryDisposition.InvalidRequest or TwCategoryDurableRecoveryDisposition.IncompatibleVersion => TwCategoryAdministrativeApiDisposition.InvalidInput,
            TwCategoryDurableRecoveryDisposition.Unavailable => TwCategoryAdministrativeApiDisposition.ProviderUnavailable,
            TwCategoryDurableRecoveryDisposition.Cancelled => TwCategoryAdministrativeApiDisposition.Cancellation,
            TwCategoryDurableRecoveryDisposition.Failed => TwCategoryAdministrativeApiDisposition.PersistenceFailure,
            _ => TwCategoryAdministrativeApiDisposition.PartialResult
        };

    private static TwCategoryAdministrativeApiDisposition MapAuthorization(
        TwCategoryAdministrativeAuthorizationDecision? decision)
        => decision?.Reason switch
        {
            TwCategoryAdministrativeAuthorizationReason.ProviderUnavailable => TwCategoryAdministrativeApiDisposition.ProviderUnavailable,
            TwCategoryAdministrativeAuthorizationReason.CancellationRequested => TwCategoryAdministrativeApiDisposition.Cancellation,
            _ => TwCategoryAdministrativeApiDisposition.Forbidden
        };

    private static string? AuthorizationDiagnostic(
        TwCategoryAdministrativeAuthorizationDecision? decision)
        => decision is null || decision.Reason == TwCategoryAdministrativeAuthorizationReason.Granted
            ? null
            : decision.Reason.ToString();
}
