using System.Text.Json;
using Dapper;
using TightWiki.Library.Database;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Durable administrative-grant store. Grant state transitions and their
/// redacted audit event are committed in one InnoDB transaction. This store is
/// never registered by default; explicit activation must prove the exact
/// schema and target first.
/// </summary>
public sealed class MySqlCategoryAdministrativeGrantStore : ITwCategoryAdministrativeGrantStore
{
    private const string GrantSelect = """
        SELECT GrantId, ApprovalId, ApproverUserId, ActorUserId, Operation,
               ScopeFingerprint, ScopePolicyVersion, ScopeJson, IssuedAtUtc,
               NotBeforeUtc, ExpiresAtUtc, RevocationState, ReplayMode,
               IssuerEvidenceReference, GrantState, Version, RevokedAtUtc,
               RevokedByActorUserId, RevocationReason, ConsumedAtUtc,
               ConsumedByActorUserId, ConsumedOperationId, ConsumedCorrelationId,
               LastAuditSequence, SchemaVersion
        FROM TwCategoryAdministrativeGrant
        WHERE GrantId = @GrantId
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly ITwCategoryAdministrativeGrantFaultInjector? _faultInjector;

    public MySqlCategoryAdministrativeGrantStore(
        IDatabaseConnectionFactory connectionFactory,
        ITwCategoryAdministrativeGrantFaultInjector? faultInjector = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _faultInjector = faultInjector;
    }

    public async Task<TwCategoryAdministrativeGrantCreateResult> CreateAsync(
        TwCategoryAdministrativeGrantCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateCreate(request, out var validation))
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, DiagnosticCode: validation);

        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken);
            var grant = request.Grant;
            var createdAt = grant.IssuedAtUtc;
            await session.Connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO TwCategoryAdministrativeGrant
                    (GrantId, ApprovalId, ApproverUserId, ActorUserId, Operation,
                     ScopeFingerprint, ScopePolicyVersion, ScopeJson, IssuedAtUtc,
                     NotBeforeUtc, ExpiresAtUtc, RevocationState, ReplayMode,
                     IssuerEvidenceReference, GrantState, Version, RevokedAtUtc,
                     RevokedByActorUserId, RevocationReason, ConsumedAtUtc,
                     ConsumedByActorUserId, ConsumedOperationId, ConsumedCorrelationId,
                     LastAuditSequence, SchemaVersion, CreatedAtUtc, UpdatedAtUtc)
                VALUES
                    (@GrantId, @ApprovalId, @ApproverUserId, @ActorUserId, @Operation,
                     @ScopeFingerprint, @ScopePolicyVersion, @ScopeJson, @IssuedAtUtc,
                     @NotBeforeUtc, @ExpiresAtUtc, 'Active', @ReplayMode,
                     @IssuerEvidenceReference, 'Issued', 0, NULL, NULL, NULL, NULL,
                     NULL, NULL, NULL, 1, @SchemaVersion, @CreatedAtUtc, @CreatedAtUtc);
                """, GrantParameters(grant, request.IssuedAtUtc, request.IssuerActorUserId), transaction: transaction.Transaction, cancellationToken: cancellationToken));

            await InsertAuditAsync(session.Connection, transaction.Transaction, grant.GrantId, 1,
                TwCategoryAdministrativeGrantAuditKind.Issued, request.IssuerActorUserId,
                grant.Operation, request.CorrelationId, createdAt, null, null, cancellationToken);
            var row = await session.Connection.QuerySingleAsync<GrantRow>(new CommandDefinition(
                GrantSelect + ";", new { GrantId = grant.GrantId.ToString("D") }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (!TryMap(row, out var mapped, out var mapDiagnostic))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, DiagnosticCode: mapDiagnostic);
            }

            await transaction.CommitAsync(cancellationToken);
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.Created, mapped);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, DiagnosticCode: "AdministrativeGrantPersistenceFailure");
        }
    }

    public async Task<TwCategoryAdministrativeGrantLoadResult> LoadAsync(
        Guid grantId,
        CancellationToken cancellationToken = default)
    {
        if (grantId == Guid.Empty)
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, DiagnosticCode: "InvalidGrantId");
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            var row = await session.Connection.QuerySingleOrDefaultAsync<GrantRow>(new CommandDefinition(
                GrantSelect + ";", new { GrantId = grantId.ToString("D") }, cancellationToken: cancellationToken));
            if (row is null)
                return new(TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, DiagnosticCode: "AdministrativeGrantMissing");
            return TryMap(row, out var grant, out var diagnostic)
                ? new(TwCategoryAdministrativeGrantDurabilityDisposition.Loaded, grant)
                : new(TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, DiagnosticCode: diagnostic);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, DiagnosticCode: "AdministrativeGrantPersistenceFailure");
        }
    }

    public async Task<TwCategoryAdministrativeGrantEvidenceResult> GetAsync(
        TwCategoryAdministrativeAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.ActorUserId == Guid.Empty || request.Scope is null)
            return new(null, DiagnosticCode: "MalformedAdministrativeGrantRequest");
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            var rows = (await session.Connection.QueryAsync<GrantRow>(new CommandDefinition("""
                SELECT GrantId, ApprovalId, ApproverUserId, ActorUserId, Operation,
                       ScopeFingerprint, ScopePolicyVersion, ScopeJson, IssuedAtUtc,
                       NotBeforeUtc, ExpiresAtUtc, RevocationState, ReplayMode,
                       IssuerEvidenceReference, GrantState, Version, RevokedAtUtc,
                       RevokedByActorUserId, RevocationReason, ConsumedAtUtc,
                       ConsumedByActorUserId, ConsumedOperationId, ConsumedCorrelationId,
                       LastAuditSequence, SchemaVersion
                FROM TwCategoryAdministrativeGrant
                WHERE ActorUserId = @ActorUserId
                  AND Operation = @Operation
                  AND ScopeFingerprint = @ScopeFingerprint
                ORDER BY IssuedAtUtc DESC;
                """, new
            {
                ActorUserId = request.ActorUserId.ToString("D"),
                Operation = request.Operation.ToString(),
                ScopeFingerprint = request.Scope.Fingerprint
            }, cancellationToken: cancellationToken))).ToArray();
            if (rows.Length == 0)
                return new(null, DurableReplayProtectionAvailable: true);
            if (rows.Length != 1)
                return new(null, DurableReplayProtectionAvailable: true, DiagnosticCode: "AdministrativeGrantEvidenceAmbiguous");
            if (!TryMap(rows[0], out var grant, out var diagnostic))
                return new(null, DurableReplayProtectionAvailable: true, DiagnosticCode: diagnostic);
            return new(
                grant.Grant,
                grant.State == TwCategoryAdministrativeGrantState.Consumed,
                DurableReplayProtectionAvailable: true,
                grant.Version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(null, DiagnosticCode: "AdministrativeGrantPersistenceFailure");
        }
    }

    public async Task<TwCategoryAdministrativeGrantConsumeResult> TryConsumeAsync(
        TwCategoryAdministrativeGrantConsumeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateConsume(request, out var validation))
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, DiagnosticCode: validation);

        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken);
            var row = await session.Connection.QuerySingleOrDefaultAsync<GrantRow>(new CommandDefinition(
                GrantSelect + " FOR UPDATE;", new { GrantId = request.GrantId.ToString("D") }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (row is null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, DiagnosticCode: "AdministrativeGrantMissing");
            }
            if (!TryMap(row, out var current, out var mapDiagnostic))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, DiagnosticCode: mapDiagnostic);
            }
            if (current.State == TwCategoryAdministrativeGrantState.Revoked)
                return await RollbackResultAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.Revoked, current, "GrantRevoked");
            if (current.State == TwCategoryAdministrativeGrantState.Consumed)
                return await RollbackResultAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.AlreadyConsumed, current, "GrantAlreadyConsumed");
            if (current.Version != request.ExpectedVersion)
                return await RollbackResultAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.GenerationConflict, current, "GrantVersionConflict");
            if (current.Grant.ActorUserId != request.ActorUserId
                || current.Grant.Operation != request.Operation
                || !SameScope(current.Grant.Scope, request.Scope))
                return await RollbackResultAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.ScopeConflict, current, "GrantBindingMismatch");
            if (current.Grant.ReplayMode != TwCategoryAdministrativeGrantReplayMode.SingleUse)
                return await RollbackResultAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, current, "GrantIsNotSingleUse");
            if (request.NowUtc < current.Grant.NotBeforeUtc)
                return await RollbackResultAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, current, "GrantNotYetValid");
            if (request.NowUtc >= current.Grant.ExpiresAtUtc)
                return await RollbackResultAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, current, "GrantExpired");

            var changed = await session.Connection.ExecuteAsync(new CommandDefinition("""
                UPDATE TwCategoryAdministrativeGrant
                SET GrantState = 'Consumed', Version = Version + 1,
                    ConsumedAtUtc = @NowUtc, ConsumedByActorUserId = @ActorUserId,
                    ConsumedOperationId = @CorrelationId, ConsumedCorrelationId = @CorrelationId,
                    LastAuditSequence = LastAuditSequence + 1, UpdatedAtUtc = @NowUtc
                WHERE GrantId = @GrantId AND Version = @ExpectedVersion AND GrantState = 'Issued';
                """, new
            {
                GrantId = request.GrantId.ToString("D"),
                ExpectedVersion = request.ExpectedVersion,
                request.NowUtc,
                ActorUserId = request.ActorUserId.ToString("D"),
                CorrelationId = request.CorrelationId.ToString("D")
            }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (changed != 1)
                return await RollbackResultAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.GenerationConflict, current, "GrantVersionConflict");

            if (_faultInjector is not null)
                await _faultInjector.InjectAsync(TwCategoryAdministrativeGrantFaultBoundary.BeforeTransitionAuditCommit, cancellationToken);
            await InsertAuditAsync(session.Connection, transaction.Transaction, request.GrantId, current.LastAuditSequence + 1,
                TwCategoryAdministrativeGrantAuditKind.Consumed, request.ActorUserId, request.Operation,
                request.CorrelationId, request.NowUtc, null, null, cancellationToken);
            var updated = await session.Connection.QuerySingleAsync<GrantRow>(new CommandDefinition(
                GrantSelect + ";", new { GrantId = request.GrantId.ToString("D") }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (!TryMap(updated, out var mapped, out mapDiagnostic))
                return await RollbackResultAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, current, mapDiagnostic);
            await transaction.CommitAsync(cancellationToken);
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.Consumed, mapped);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, DiagnosticCode: "AdministrativeGrantConsumeUnavailable");
        }
    }

    public async Task<TwCategoryAdministrativeGrantRevokeResult> RevokeAsync(
        TwCategoryAdministrativeGrantRevokeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateRevoke(request, out var validation))
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, DiagnosticCode: validation);
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken);
            var row = await session.Connection.QuerySingleOrDefaultAsync<GrantRow>(new CommandDefinition(
                GrantSelect + " FOR UPDATE;", new { GrantId = request.GrantId.ToString("D") }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (row is null)
                return await RollbackRevokeAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, "AdministrativeGrantMissing");
            if (!TryMap(row, out var current, out var diagnostic))
                return await RollbackRevokeAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, diagnostic);
            if (current.State == TwCategoryAdministrativeGrantState.Revoked)
                return await RollbackRevokeAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.AlreadyRevoked, "GrantAlreadyRevoked", current);
            if (current.State == TwCategoryAdministrativeGrantState.Consumed)
                return await RollbackRevokeAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.ScopeConflict, "GrantAlreadyConsumed", current);
            if (current.Version != request.ExpectedVersion)
                return await RollbackRevokeAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.GenerationConflict, "GrantVersionConflict", current);

            var changed = await session.Connection.ExecuteAsync(new CommandDefinition("""
                UPDATE TwCategoryAdministrativeGrant
                SET GrantState = 'Revoked', RevocationState = 'Revoked', Version = Version + 1,
                    RevokedAtUtc = @RevokedAtUtc, RevokedByActorUserId = @RevokingActorUserId,
                    RevocationReason = @Reason, LastAuditSequence = LastAuditSequence + 1,
                    UpdatedAtUtc = @RevokedAtUtc
                WHERE GrantId = @GrantId AND Version = @ExpectedVersion AND GrantState = 'Issued';
                """, new
            {
                GrantId = request.GrantId.ToString("D"),
                ExpectedVersion = request.ExpectedVersion,
                request.RevokedAtUtc,
                RevokingActorUserId = request.RevokingActorUserId.ToString("D"),
                request.Reason
            }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (changed != 1)
                return await RollbackRevokeAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.GenerationConflict, "GrantVersionConflict", current);

            await InsertAuditAsync(session.Connection, transaction.Transaction, request.GrantId, current.LastAuditSequence + 1,
                TwCategoryAdministrativeGrantAuditKind.Revoked, request.RevokingActorUserId, current.Grant.Operation,
                request.CorrelationId, request.RevokedAtUtc, null, request.Reason, cancellationToken);
            var updated = await session.Connection.QuerySingleAsync<GrantRow>(new CommandDefinition(
                GrantSelect + ";", new { GrantId = request.GrantId.ToString("D") }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (!TryMap(updated, out var mapped, out diagnostic))
                return await RollbackRevokeAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, diagnostic, current);
            await transaction.CommitAsync(cancellationToken);
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.Revoked, mapped);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, DiagnosticCode: "AdministrativeGrantRevocationUnavailable");
        }
    }

    public async Task<TwCategoryAdministrativeGrantExecutionOutcomeResult> RecordExecutionOutcomeAsync(
        TwCategoryAdministrativeGrantExecutionOutcomeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateOutcome(request, out var validation))
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, validation);
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken);
            var row = await session.Connection.QuerySingleOrDefaultAsync<GrantRow>(new CommandDefinition(
                GrantSelect + " FOR UPDATE;", new { GrantId = request.GrantId.ToString("D") }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (row is null)
                return await RollbackOutcomeAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, "AdministrativeGrantMissing");
            if (!TryMap(row, out var grant, out var diagnostic))
                return await RollbackOutcomeAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, diagnostic);
            if (grant.Grant.ActorUserId != request.ActorUserId || grant.Grant.Operation != request.Operation || !SameScope(grant.Grant.Scope, request.Scope))
                return await RollbackOutcomeAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.ScopeConflict, "GrantBindingMismatch");
            var eventKey = AuditEventKey(TwCategoryAdministrativeGrantAuditKind.ExecutionOutcome, request.CorrelationId);
            var existing = await session.Connection.QuerySingleOrDefaultAsync<GrantAuditRow>(new CommandDefinition("""
                SELECT GrantId, EventSequence, EventId, EventKey, EventKind, ActorUserId,
                       Operation, CorrelationId, OccurredAtUtc, Outcome, DiagnosticCode, SchemaVersion
                FROM TwCategoryAdministrativeGrantAudit
                WHERE GrantId = @GrantId AND EventKey = @EventKey
                FOR UPDATE;
                """, new { GrantId = request.GrantId.ToString("D"), EventKey = eventKey }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (existing is not null)
            {
                var same = string.Equals(existing.Outcome, request.Outcome, StringComparison.Ordinal)
                    && existing.ActorUserId == request.ActorUserId
                    && existing.Operation == request.Operation.ToString();
                return await RollbackOutcomeAsync(transaction, same ? TwCategoryAdministrativeGrantDurabilityDisposition.DuplicateSame : TwCategoryAdministrativeGrantDurabilityDisposition.DuplicateConflict, same ? null : "GrantOutcomeConflict");
            }
            if (grant.LastAuditSequence >= TwCategoryAdministrativeGrantDurabilityConstants.MaxAuditEventsPerGrant)
                return await RollbackOutcomeAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, "GrantAuditLimitExceeded");
            await InsertAuditAsync(session.Connection, transaction.Transaction, request.GrantId, grant.LastAuditSequence + 1,
                TwCategoryAdministrativeGrantAuditKind.ExecutionOutcome, request.ActorUserId, request.Operation,
                request.CorrelationId, request.OccurredAtUtc, request.Outcome, null, cancellationToken);
            await session.Connection.ExecuteAsync(new CommandDefinition(
                "UPDATE TwCategoryAdministrativeGrant SET LastAuditSequence = LastAuditSequence + 1, UpdatedAtUtc = @OccurredAtUtc WHERE GrantId = @GrantId;",
                new { GrantId = request.GrantId.ToString("D"), request.OccurredAtUtc }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.OutcomeRecorded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, "AdministrativeGrantOutcomeUnavailable");
        }
    }

    public async Task<TwCategoryAdministrativeGrantAuthorizationRejectionResult> RecordAuthorizationRejectionAsync(
        TwCategoryAdministrativeGrantAuthorizationRejectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateRejection(request, out var validation))
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, validation);
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken);
            var row = await session.Connection.QuerySingleOrDefaultAsync<GrantRow>(new CommandDefinition(
                GrantSelect + " FOR UPDATE;", new { GrantId = request.GrantId.ToString("D") }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (row is null)
                return await RollbackRejectionAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, "AdministrativeGrantMissing");
            if (!TryMap(row, out var grant, out var diagnostic))
                return await RollbackRejectionAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, diagnostic);
            if (grant.Grant.ActorUserId != request.ActorUserId || grant.Grant.Operation != request.Operation)
                return await RollbackRejectionAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.ScopeConflict, "GrantBindingMismatch");
            var eventKey = AuditEventKey(TwCategoryAdministrativeGrantAuditKind.Rejected, request.CorrelationId);
            var existing = await session.Connection.QuerySingleOrDefaultAsync<GrantAuditRow>(new CommandDefinition("""
                SELECT GrantId, EventSequence, EventId, EventKey, EventKind, ActorUserId,
                       Operation, CorrelationId, OccurredAtUtc, Outcome, DiagnosticCode, SchemaVersion
                FROM TwCategoryAdministrativeGrantAudit
                WHERE GrantId = @GrantId AND EventKey = @EventKey
                FOR UPDATE;
                """, new { GrantId = request.GrantId.ToString("D"), EventKey = eventKey }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (existing is not null)
            {
                var same = string.Equals(existing.Outcome, request.Reason, StringComparison.Ordinal)
                    && existing.ActorUserId == request.ActorUserId
                    && existing.Operation == request.Operation.ToString();
                return await RollbackRejectionAsync(transaction, same ? TwCategoryAdministrativeGrantDurabilityDisposition.DuplicateSame : TwCategoryAdministrativeGrantDurabilityDisposition.DuplicateConflict, same ? null : "GrantRejectionConflict");
            }
            if (grant.LastAuditSequence >= TwCategoryAdministrativeGrantDurabilityConstants.MaxAuditEventsPerGrant)
                return await RollbackRejectionAsync(transaction, TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, "GrantAuditLimitExceeded");
            await InsertAuditAsync(session.Connection, transaction.Transaction, request.GrantId, grant.LastAuditSequence + 1,
                TwCategoryAdministrativeGrantAuditKind.Rejected, request.ActorUserId, request.Operation,
                request.CorrelationId, request.OccurredAtUtc, request.Reason, null, cancellationToken);
            await session.Connection.ExecuteAsync(new CommandDefinition(
                "UPDATE TwCategoryAdministrativeGrant SET LastAuditSequence = LastAuditSequence + 1, UpdatedAtUtc = @OccurredAtUtc WHERE GrantId = @GrantId;",
                new { GrantId = request.GrantId.ToString("D"), request.OccurredAtUtc }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.OutcomeRecorded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, "AdministrativeGrantRejectionAuditUnavailable");
        }
    }

    private static async Task<TwCategoryAdministrativeGrantConsumeResult> RollbackResultAsync(
        TightWiki.Library.Database.IDatabaseTransaction transaction,
        TwCategoryAdministrativeGrantDurabilityDisposition disposition,
        TwCategoryAdministrativeGrantRecord grant,
        string diagnostic)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        return new(disposition, grant, diagnostic);
    }

    private static async Task<TwCategoryAdministrativeGrantRevokeResult> RollbackRevokeAsync(
        TightWiki.Library.Database.IDatabaseTransaction transaction,
        TwCategoryAdministrativeGrantDurabilityDisposition disposition,
        string? diagnostic,
        TwCategoryAdministrativeGrantRecord? grant = null)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        return new(disposition, grant, diagnostic);
    }

    private static async Task<TwCategoryAdministrativeGrantExecutionOutcomeResult> RollbackOutcomeAsync(
        TightWiki.Library.Database.IDatabaseTransaction transaction,
        TwCategoryAdministrativeGrantDurabilityDisposition disposition,
        string? diagnostic)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        return new(disposition, diagnostic);
    }

    private static async Task<TwCategoryAdministrativeGrantAuthorizationRejectionResult> RollbackRejectionAsync(
        TightWiki.Library.Database.IDatabaseTransaction transaction,
        TwCategoryAdministrativeGrantDurabilityDisposition disposition,
        string? diagnostic)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        return new(disposition, diagnostic);
    }

    private static async Task InsertAuditAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid grantId,
        long sequence,
        TwCategoryAdministrativeGrantAuditKind kind,
        Guid actorUserId,
        TwCategoryAdministrativeOperation operation,
        Guid correlationId,
        DateTime occurredAtUtc,
        string? outcome,
        string? diagnosticCode,
        CancellationToken cancellationToken)
        => await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO TwCategoryAdministrativeGrantAudit
                (GrantId, EventSequence, EventId, EventKey, EventKind, ActorUserId,
                 Operation, CorrelationId, OccurredAtUtc, Outcome, DiagnosticCode, SchemaVersion)
            VALUES
                (@GrantId, @EventSequence, @EventId, @EventKey, @EventKind, @ActorUserId,
                 @Operation, @CorrelationId, @OccurredAtUtc, @Outcome, @DiagnosticCode, @SchemaVersion);
            """, new
        {
            GrantId = grantId.ToString("D"),
            EventSequence = sequence,
            EventId = Guid.NewGuid().ToString("D"),
            EventKey = AuditEventKey(kind, correlationId),
            EventKind = kind.ToString(),
            ActorUserId = actorUserId.ToString("D"),
            Operation = operation.ToString(),
            CorrelationId = correlationId.ToString("D"),
            OccurredAtUtc = occurredAtUtc,
            Outcome = outcome,
            DiagnosticCode = diagnosticCode,
            SchemaVersion = TwCategoryAdministrativeGrantDurabilityConstants.CurrentSchemaVersion
        }, transaction: transaction, cancellationToken: cancellationToken));

    private static string AuditEventKey(TwCategoryAdministrativeGrantAuditKind kind, Guid correlationId)
        => $"{kind}:{correlationId:D}";

    private static object GrantParameters(TwCategoryAdministrativeGrant grant, DateTime issuedAtUtc, Guid issuerActorUserId)
        => new
        {
            GrantId = grant.GrantId.ToString("D"),
            ApprovalId = grant.ApprovalId.ToString("D"),
            ApproverUserId = grant.ApproverUserId.ToString("D"),
            ActorUserId = grant.ActorUserId.ToString("D"),
            Operation = grant.Operation.ToString(),
            ScopeFingerprint = grant.Scope.Fingerprint,
            ScopePolicyVersion = grant.Scope.PolicyVersion,
            ScopeJson = JsonSerializer.Serialize(grant.Scope.Targets),
            grant.IssuedAtUtc,
            grant.NotBeforeUtc,
            grant.ExpiresAtUtc,
            ReplayMode = grant.ReplayMode.ToString(),
            grant.IssuerEvidenceReference,
            SchemaVersion = TwCategoryAdministrativeGrantDurabilityConstants.CurrentSchemaVersion,
            CreatedAtUtc = issuedAtUtc,
            IssuerActorUserId = issuerActorUserId
        };

    private static bool TryMap(GrantRow row, out TwCategoryAdministrativeGrantRecord grant, out string diagnostic)
    {
        grant = null!;
        diagnostic = "AdministrativeGrantUnreadable";
        if (row.SchemaVersion != TwCategoryAdministrativeGrantDurabilityConstants.CurrentSchemaVersion
            || row.Version < 0 || row.LastAuditSequence < 0
            || !Enum.TryParse<TwCategoryAdministrativeOperation>(row.Operation, out var operation)
            || !Enum.TryParse<TwCategoryAdministrativeGrantReplayMode>(row.ReplayMode, out var replayMode)
            || !Enum.TryParse<TwCategoryAdministrativeGrantState>(row.GrantState, out var state)
            || !Enum.TryParse<TwCategoryAdministrativeGrantRevocationState>(row.RevocationState, out var revocationState))
            return false;
        try
        {
            var targets = JsonSerializer.Deserialize<TwCategoryRecoveryTarget[]>(row.ScopeJson);
            if (!TwCategoryRecoveryScope.TryCreate(targets, out var scope, out var scopeDiagnostic)
                || !string.Equals(scope.Fingerprint, row.ScopeFingerprint, StringComparison.Ordinal)
                || !string.Equals(scope.PolicyVersion, row.ScopePolicyVersion, StringComparison.Ordinal))
            {
                diagnostic = scopeDiagnostic.Length == 0 ? "AdministrativeGrantScopeUnreadable" : scopeDiagnostic;
                return false;
            }
            grant = new(
                new(row.GrantId, row.ApprovalId, row.ApproverUserId, row.ActorUserId, operation, scope,
                    EnsureUtc(row.IssuedAtUtc), EnsureUtc(row.NotBeforeUtc), EnsureUtc(row.ExpiresAtUtc), revocationState, replayMode, row.IssuerEvidenceReference),
                checked((long)row.Version), state, row.RevokedAtUtc is null ? null : EnsureUtc(row.RevokedAtUtc.Value), row.RevokedByActorUserId,
                row.RevocationReason, row.ConsumedAtUtc is null ? null : EnsureUtc(row.ConsumedAtUtc.Value), row.ConsumedByActorUserId,
                row.ConsumedOperationId, row.ConsumedCorrelationId, checked((long)row.LastAuditSequence));
            diagnostic = string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SameScope(TwCategoryRecoveryScope left, TwCategoryRecoveryScope right)
        => string.Equals(left.PolicyVersion, right.PolicyVersion, StringComparison.Ordinal)
            && string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal);

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static bool ValidateCreate(TwCategoryAdministrativeGrantCreateRequest request, out string diagnostic)
    {
        diagnostic = "InvalidAdministrativeGrant";
        if (request is null || request.IssuerActorUserId == Guid.Empty || request.CorrelationId == Guid.Empty || request.Grant is null)
            return false;
        var grant = request.Grant;
        if (grant.GrantId == Guid.Empty || grant.ApprovalId == Guid.Empty || grant.ApproverUserId == Guid.Empty || grant.ActorUserId == Guid.Empty
            || grant.Scope is null || string.IsNullOrWhiteSpace(grant.IssuerEvidenceReference)
            || grant.IssuerEvidenceReference.Length > TwCategoryAdministrativeGrantDurabilityConstants.MaxIssuerEvidenceCharacters
            || request.IssuedAtUtc.Kind != DateTimeKind.Utc
            || grant.IssuedAtUtc.Kind != DateTimeKind.Utc || grant.NotBeforeUtc.Kind != DateTimeKind.Utc || grant.ExpiresAtUtc.Kind != DateTimeKind.Utc
            || grant.NotBeforeUtc >= grant.ExpiresAtUtc || grant.IssuedAtUtc > grant.ExpiresAtUtc)
            return false;
        diagnostic = string.Empty;
        return true;
    }

    private static bool ValidateConsume(TwCategoryAdministrativeGrantConsumeRequest request, out string diagnostic)
    {
        diagnostic = "InvalidAdministrativeGrantConsumeRequest";
        if (request is null || request.GrantId == Guid.Empty || request.ExpectedVersion < 0 || request.ActorUserId == Guid.Empty
            || request.Scope is null || request.CorrelationId == Guid.Empty || request.NowUtc.Kind != DateTimeKind.Utc
            || request.SchemaVersion != TwCategoryAdministrativeGrantDurabilityConstants.CurrentSchemaVersion)
            return false;
        diagnostic = string.Empty;
        return true;
    }

    private static bool ValidateRevoke(TwCategoryAdministrativeGrantRevokeRequest request, out string diagnostic)
    {
        diagnostic = "InvalidAdministrativeGrantRevokeRequest";
        if (request is null || request.GrantId == Guid.Empty || request.ExpectedVersion < 0 || request.RevokingActorUserId == Guid.Empty
            || request.CorrelationId == Guid.Empty || request.RevokedAtUtc.Kind != DateTimeKind.Utc
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > TwCategoryAdministrativeGrantDurabilityConstants.MaxRevocationReasonCharacters
            || request.SchemaVersion != TwCategoryAdministrativeGrantDurabilityConstants.CurrentSchemaVersion)
            return false;
        diagnostic = string.Empty;
        return true;
    }

    private static bool ValidateOutcome(TwCategoryAdministrativeGrantExecutionOutcomeRequest request, out string diagnostic)
    {
        diagnostic = "InvalidAdministrativeGrantOutcomeRequest";
        if (request is null || request.GrantId == Guid.Empty || request.ActorUserId == Guid.Empty || request.Scope is null
            || request.CorrelationId == Guid.Empty || request.OccurredAtUtc.Kind != DateTimeKind.Utc
            || string.IsNullOrWhiteSpace(request.Outcome) || request.Outcome.Length > TwCategoryAdministrativeGrantDurabilityConstants.MaxOutcomeCharacters
            || request.SchemaVersion != TwCategoryAdministrativeGrantDurabilityConstants.CurrentSchemaVersion)
            return false;
        diagnostic = string.Empty;
        return true;
    }

    private static bool ValidateRejection(TwCategoryAdministrativeGrantAuthorizationRejectionRequest request, out string diagnostic)
    {
        diagnostic = "InvalidAdministrativeGrantRejectionRequest";
        if (request is null || request.GrantId == Guid.Empty || request.ActorUserId == Guid.Empty
            || request.CorrelationId == Guid.Empty || request.OccurredAtUtc.Kind != DateTimeKind.Utc
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > TwCategoryAdministrativeGrantDurabilityConstants.MaxOutcomeCharacters
            || request.SchemaVersion != TwCategoryAdministrativeGrantDurabilityConstants.CurrentSchemaVersion)
            return false;
        diagnostic = string.Empty;
        return true;
    }

    private sealed record GrantRow(
        Guid GrantId, Guid ApprovalId, Guid ApproverUserId, Guid ActorUserId, string Operation,
        string ScopeFingerprint, string ScopePolicyVersion, string ScopeJson, DateTime IssuedAtUtc,
        DateTime NotBeforeUtc, DateTime ExpiresAtUtc, string RevocationState, string ReplayMode,
        string IssuerEvidenceReference, string GrantState, ulong Version, DateTime? RevokedAtUtc,
        Guid? RevokedByActorUserId, string? RevocationReason, DateTime? ConsumedAtUtc,
        Guid? ConsumedByActorUserId, Guid? ConsumedOperationId, Guid? ConsumedCorrelationId,
        ulong LastAuditSequence, uint SchemaVersion);

    private sealed record GrantAuditRow(
        Guid GrantId, ulong EventSequence, Guid EventId, string EventKey, string EventKind,
        Guid ActorUserId, string Operation, Guid CorrelationId, DateTime OccurredAtUtc,
        string? Outcome, string? DiagnosticCode, uint SchemaVersion);
}
