using System.Text.Json;
using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin.Models;

namespace TightWiki.Tests.Unit;

public sealed class TwCategoryOperationalReadinessTests
{
    private static readonly DateTime EvaluationTime = new(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RejectingDefaultsAreReportedAsDisabledAndNeverReady()
    {
        var input = FullyVerified() with
        {
            ActivationState = TwCategoryProviderActivationState.RejectingDefaults,
            ProductionApproval = TwCategoryOperationalEvidenceStatus.Missing
        };

        var snapshot = new TwCategoryOperationalReadinessService().Evaluate(input, EvaluationTime);

        Assert.Equal(TwCategoryOperationalReadinessState.DisabledByDefault, snapshot.State);
        Assert.Equal(TwCategoryOperationalDecision.RequiresMoreEvidence, snapshot.Decision);
        Assert.Contains(TwCategoryOperationalReasonCode.ProviderNotActivated, snapshot.Reasons);
        Assert.True(snapshot.DeterminedWithoutDatabaseConnection);
        Assert.True(snapshot.PublicSafe);
        Assert.False(snapshot.EligibleForReviewOnly);
    }

    [Theory]
    [InlineData(TwCategoryOperationalEvidenceStatus.Missing, TwCategoryOperationalReasonCode.AuthorizationMissing)]
    [InlineData(TwCategoryOperationalEvidenceStatus.Expired, TwCategoryOperationalReasonCode.AuthorizationExpired)]
    public void MissingOrExpiredAuthorizationBlocksActivation(
        TwCategoryOperationalEvidenceStatus status,
        TwCategoryOperationalReasonCode expectedReason)
    {
        var snapshot = new TwCategoryOperationalReadinessService().Evaluate(
            FullyVerified() with { Authorization = status },
            EvaluationTime);

        Assert.Equal(TwCategoryOperationalReadinessState.AuthorizationMissingOrExpired, snapshot.State);
        Assert.NotEqual(TwCategoryOperationalDecision.AllowedForReview, snapshot.Decision);
        Assert.Contains(expectedReason, snapshot.Reasons);
    }

    [Fact]
    public void HealthyProviderEvidenceAloneDoesNotImplyProductionReadiness()
    {
        var snapshot = new TwCategoryOperationalReadinessService().Evaluate(
            FullyVerified() with
            {
                Authorization = TwCategoryOperationalEvidenceStatus.Missing,
                TargetIsolation = TwCategoryOperationalEvidenceStatus.Unknown,
                CategorySchema = TwCategoryOperationalEvidenceStatus.Missing,
                RecoverySchema = TwCategoryOperationalEvidenceStatus.Missing,
                BrowseAuthorization = TwCategoryOperationalEvidenceStatus.Missing,
                RecoveryAuthorization = TwCategoryOperationalEvidenceStatus.Missing,
                ProductionApproval = TwCategoryOperationalEvidenceStatus.Missing
            },
            EvaluationTime);

        Assert.NotEqual(TwCategoryOperationalReadinessState.ReadyForOperatorReview, snapshot.State);
        Assert.NotEqual(TwCategoryOperationalDecision.AllowedForReview, snapshot.Decision);
        Assert.Contains(TwCategoryOperationalReasonCode.CategorySchemaNotCertified, snapshot.Reasons);
        Assert.Contains(TwCategoryOperationalReasonCode.TargetIsolationUnverified, snapshot.Reasons);
    }

    [Fact]
    public void ProviderOutageAndSchemaGapsRemainExplicit()
    {
        var service = new TwCategoryOperationalReadinessService();
        var outage = service.Evaluate(FullyVerified() with
        {
            CategoryProvider = TwCategoryOperationalEvidenceStatus.Unavailable
        }, EvaluationTime);
        var schemaGap = service.Evaluate(FullyVerified() with
        {
            RecoverySchema = TwCategoryOperationalEvidenceStatus.Missing
        }, EvaluationTime);

        Assert.Equal(TwCategoryOperationalReadinessState.ProviderUnavailable, outage.State);
        Assert.Contains(TwCategoryOperationalReasonCode.CategoryProviderUnavailable, outage.Reasons);
        Assert.Equal(TwCategoryOperationalReadinessState.SchemaNotCertified, schemaGap.State);
        Assert.Contains(TwCategoryOperationalReasonCode.RecoverySchemaNotCertified, schemaGap.Reasons);
    }

    [Fact]
    public void IndeterminateRecoveryCannotBecomeConfirmedCommit()
    {
        var service = new TwCategoryOperationalReadinessService();
        var snapshot = service.Evaluate(FullyVerified() with { RecoveryIndeterminate = true }, EvaluationTime);
        var eventRecord = service.CreateEvent(
            TwCategoryOperationalEventKind.RecoveryIndeterminate,
            TwCategoryOperationalEventOutcome.Indeterminate,
            TwCategoryOperationalReasonCode.RecoveryIndeterminate,
            EvaluationTime);

        Assert.Equal(TwCategoryOperationalReadinessState.RecoveryIndeterminate, snapshot.State);
        Assert.NotEqual(TwCategoryOperationalDecision.AllowedForReview, snapshot.Decision);
        Assert.Equal(TwCategoryOperationalEventOutcome.Indeterminate, eventRecord.Outcome);
        Assert.DoesNotContain("Page", JsonSerializer.Serialize(eventRecord), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Category", JsonSerializer.Serialize(eventRecord), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdministrativePreviewIsReadOnlyAndExecutionRequiresFreshAuthorization()
    {
        var preview = new TwCategoryRecoveryAdministrativeRequest(
            Guid.NewGuid(),
            [new TwCategoryRecoveryTarget(17)],
            null,
            TwCategoryRecoveryAdministrationMode.Preview);
        var execute = preview with { Mode = TwCategoryRecoveryAdministrationMode.Execute };

        Assert.Null(TwCategoryRecoveryAdministrativePolicy.Validate(preview));
        Assert.True(preview.IsPreview);
        Assert.False(preview.MutationRequested);
        Assert.False(preview.RequiresFreshAuthorization);
        Assert.Throws<InvalidOperationException>(() => preview.ToDurableExecutionRequest());
        Assert.True(execute.MutationRequested);
        Assert.True(execute.RequiresFreshAuthorization);
        Assert.Null(TwCategoryRecoveryAdministrativePolicy.Validate(execute));
    }

    [Fact]
    public void RollbackIsAReviewDecisionAndNeverMutatesRegistrations()
    {
        var service = new TwCategoryOperationalReadinessService();
        var snapshot = service.Evaluate(FullyVerified(), EvaluationTime);

        Assert.Equal(TwCategoryOperationalDecision.AllowedForReview, snapshot.Decision);
        Assert.True(snapshot.EligibleForReviewOnly);
        var withoutOperator = service.EvaluateRollback(snapshot, explicitOperatorRequest: false);
        var withOperator = service.EvaluateRollback(snapshot, explicitOperatorRequest: true);

        Assert.Equal(TwCategoryRollbackAction.ManualReviewRequired, withoutOperator.Action);
        Assert.Equal(TwCategoryRollbackAction.RevertToRejectingAfterOperatorApproval, withOperator.Action);
        Assert.False(withoutOperator.WouldMutate);
        Assert.False(withOperator.WouldMutate);
    }

    [Fact]
    public void SourceOnlyClaimsCannotBecomeAnActivationReviewDecision()
    {
        var snapshot = new TwCategoryOperationalReadinessService().Evaluate(
            FullyVerified() with { EvidenceOrigin = TwCategoryOperationalEvidenceOrigin.SourceOnly },
            EvaluationTime);

        Assert.Equal(TwCategoryOperationalReadinessState.SchemaNotCertified, snapshot.State);
        Assert.NotEqual(TwCategoryOperationalDecision.AllowedForReview, snapshot.Decision);
        Assert.Contains(TwCategoryOperationalReasonCode.RetainedCertificationEvidenceRequired, snapshot.Reasons);
    }

    private static TwCategoryOperationalReadinessInput FullyVerified()
        => new(
            TwCategoryProviderActivationState.ExplicitActivationPending,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceStatus.Verified,
            TwCategoryOperationalEvidenceOrigin.DisposableCertification,
            RecoveryIndeterminate: false,
            DatabaseContactedDuringEvaluation: false);
}
