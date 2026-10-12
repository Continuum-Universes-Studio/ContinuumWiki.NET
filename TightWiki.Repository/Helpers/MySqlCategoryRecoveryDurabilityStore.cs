using System.Text.Json;
using Dapper;
using TightWiki.Library.Database;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// MariaDB durability store for recoverable category replay. Each method is
/// atomic within the durability tables only. Category membership mutation is
/// performed by the existing category repository in a separate transaction.
/// This type is deliberately registered only by explicit authorized activation.
/// </summary>
public sealed class MySqlCategoryRecoveryDurabilityStore : ITwCategoryRecoveryDurabilityStore
{
    private const string OperationSelect = """
        SELECT OperationId, ScopeFingerprint, ScopeJson, ActorUserId, PolicyVersion,
               OperationStatus, TerminalReason, CommittedPosition, CheckpointGeneration,
               LastOutcomeSequence, LastAuditSequence, SchemaVersion, CreatedAtUtc, UpdatedAtUtc
        FROM TwCategoryRecoveryOperation
        WHERE OperationId = @OperationId
        """;

    private readonly IDatabaseConnectionFactory _connectionFactory;

    public MySqlCategoryRecoveryDurabilityStore(IDatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<TwCategoryRecoveryCreateOperationResult> CreateOrReplayOperationAsync(
        TwCategoryRecoveryOperationRecord requested,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateOperation(requested, out var validation))
            return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: validation);

        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken);
            var existingBeforeInsert = await session.Connection.QuerySingleOrDefaultAsync<OperationRow>(new CommandDefinition(
                OperationSelect + " FOR UPDATE;",
                new { OperationId = requested.OperationId.ToString("D") },
                transaction: transaction.Transaction,
                cancellationToken: cancellationToken));
            var inserted = existingBeforeInsert is null;
            if (inserted)
            {
                await session.Connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO TwCategoryRecoveryOperation
                        (OperationId, ScopeFingerprint, ScopeJson, ActorUserId, PolicyVersion,
                         OperationStatus, TerminalReason, CommittedPosition, CheckpointGeneration,
                         LastOutcomeSequence, LastAuditSequence, SchemaVersion, CreatedAtUtc, UpdatedAtUtc)
                    VALUES
                        (@OperationId, @ScopeFingerprint, @ScopeJson, @ActorUserId, @PolicyVersion,
                         @OperationStatus, @TerminalReason, @CommittedPosition, @CheckpointGeneration,
                         @LastOutcomeSequence, 0, @SchemaVersion, @CreatedAtUtc, @CreatedAtUtc)
                    ON DUPLICATE KEY UPDATE OperationId = OperationId;
                    """, OperationParameters(requested), transaction: transaction.Transaction, cancellationToken: cancellationToken));
            }

            var row = inserted
                ? await session.Connection.QuerySingleOrDefaultAsync<OperationRow>(new CommandDefinition(
                    OperationSelect + " FOR UPDATE;",
                    new { OperationId = requested.OperationId.ToString("D") },
                    transaction: transaction.Transaction,
                    cancellationToken: cancellationToken))
                : existingBeforeInsert;
            TwCategoryRecoveryOperationRecord operation = null!;
            string? mapDiagnostic = null;
            var operationMapped = row is not null && TryMapOperation(row, out operation, out mapDiagnostic);
            if (!operationMapped)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: mapDiagnostic ?? "RecoveryOperationUnreadable");
            }

            if (!string.Equals(operation.PolicyVersion, requested.PolicyVersion, StringComparison.Ordinal)
                || operation.Checkpoint.SchemaVersion != requested.Checkpoint.SchemaVersion)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.IncompatibleVersion, operation, "RecoveryDurabilityVersionUnsupported");
            }
            if (!string.Equals(operation.Scope.Fingerprint, requested.Scope.Fingerprint, StringComparison.Ordinal)
                || operation.ActorUserId != requested.ActorUserId)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.ScopeConflict, operation, "RecoveryScopeConflict");
            }

            await transaction.CommitAsync(cancellationToken);
            return new(inserted ? TwCategoryRecoveryDurabilityDisposition.Created : TwCategoryRecoveryDurabilityDisposition.Replayed, operation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: "RecoveryDurabilityPersistenceFailure");
        }
    }

    public async Task<TwCategoryRecoveryLoadOperationResult> LoadOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
            return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: "InvalidOperationId");
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            var row = await session.Connection.QuerySingleOrDefaultAsync<OperationRow>(new CommandDefinition(
                OperationSelect + ";",
                new { OperationId = operationId.ToString("D") },
                cancellationToken: cancellationToken));
            if (row is null)
                return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: "RecoveryOperationMissing");
            return TryMapOperation(row, out var operation, out var diagnostic)
                ? new(TwCategoryRecoveryDurabilityDisposition.Loaded, operation)
                : new(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: diagnostic ?? "RecoveryOperationUnreadable");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: "RecoveryDurabilityPersistenceFailure");
        }
    }

    public async Task<TwCategoryRecoveryAppendAuditResult> AppendAuditEventAsync(
        TwCategoryRecoveryAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateAudit(auditEvent, out var validation))
            return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: validation);
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken);
            var operation = await session.Connection.QuerySingleOrDefaultAsync<OperationRow>(new CommandDefinition(
                OperationSelect + " FOR UPDATE;",
                new { OperationId = auditEvent.OperationId.ToString("D") },
                transaction: transaction.Transaction,
                cancellationToken: cancellationToken));
            if (operation is null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: "RecoveryOperationMissing");
            }
            if (!string.Equals(operation.ActorUserId?.ToString("D") ?? string.Empty, auditEvent.ActorUserId?.ToString("D") ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.DuplicateConflict, DiagnosticCode: "RecoveryActorConflict");
            }
            if (operation.LastAuditSequence >= TwCategoryRecoveryDurabilityConstants.MaxAuditEventsPerOperation)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: "RecoveryAuditLimitExceeded");
            }

            var existing = await session.Connection.QuerySingleOrDefaultAsync<AuditRow>(new CommandDefinition("""
                SELECT OperationId, EventSequence, EventId, EventKey, Position, AttemptId,
                       EventKind, Confidence, PageId, ObservedRevision, Outcome, ActorUserId,
                       OccurredAtUtc, DiagnosticCode, SchemaVersion
                FROM TwCategoryRecoveryAuditEvent
                WHERE OperationId = @OperationId AND EventKey = @EventKey
                FOR UPDATE;
                """, new { OperationId = auditEvent.OperationId.ToString("D"), auditEvent.EventKey }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (existing is not null)
            {
                var same = TryMapAudit(existing, out var mapped, out _)
                    && EquivalentAudit(mapped, auditEvent);
                await transaction.RollbackAsync(CancellationToken.None);
                return new(same ? TwCategoryRecoveryDurabilityDisposition.DuplicateSame : TwCategoryRecoveryDurabilityDisposition.DuplicateConflict, checked((long)existing.EventSequence),
                    same ? null : "RecoveryAuditEventKeyConflict");
            }

            var sequence = checked((long)operation.LastAuditSequence + 1);
            await session.Connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO TwCategoryRecoveryAuditEvent
                    (OperationId, EventSequence, EventId, EventKey, Position, AttemptId,
                     EventKind, Confidence, PageId, ObservedRevision, Outcome, ActorUserId,
                     OccurredAtUtc, DiagnosticCode, SchemaVersion)
                VALUES
                    (@OperationId, @EventSequence, @EventId, @EventKey, @Position, @AttemptId,
                     @EventKind, @Confidence, @PageId, @ObservedRevision, @Outcome, @ActorUserId,
                     @OccurredAtUtc, @DiagnosticCode, @SchemaVersion);
                """, AuditParameters(auditEvent, sequence), transaction: transaction.Transaction, cancellationToken: cancellationToken));
            var sequenceUpdated = await session.Connection.ExecuteAsync(new CommandDefinition("""
                UPDATE TwCategoryRecoveryOperation
                SET LastAuditSequence = @EventSequence, UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE OperationId = @OperationId AND LastAuditSequence = @ExpectedSequence;
                """, new { OperationId = auditEvent.OperationId.ToString("D"), EventSequence = sequence, ExpectedSequence = operation.LastAuditSequence }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (sequenceUpdated != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.GenerationConflict, DiagnosticCode: "AuditSequenceConflict");
            }
            await transaction.CommitAsync(cancellationToken);
            return new(TwCategoryRecoveryDurabilityDisposition.Appended, sequence);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: "RecoveryDurabilityPersistenceFailure");
        }
    }

    public async Task<TwCategoryRecoveryAdvanceCheckpointResult> AdvanceCheckpointAsync(
        TwCategoryRecoveryCheckpointAdvanceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.OperationId == Guid.Empty || !IsSafeToken(request.RecordedOutcomeEventKey, 192))
            return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: "InvalidCheckpointRequest");
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken);
            var row = await session.Connection.QuerySingleOrDefaultAsync<OperationRow>(new CommandDefinition(OperationSelect + " FOR UPDATE;", new { OperationId = request.OperationId.ToString("D") }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            TwCategoryRecoveryOperationRecord operation = null!;
            string? diagnostic = null;
            var operationMapped = row is not null && TryMapOperation(row, out operation, out diagnostic);
            if (!operationMapped)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: diagnostic ?? "RecoveryOperationMissing");
            }
            var recorded = await session.Connection.QuerySingleOrDefaultAsync<AuditRow>(new CommandDefinition("""
                SELECT OperationId, EventSequence, EventId, EventKey, Position, AttemptId,
                       EventKind, Confidence, PageId, ObservedRevision, Outcome, ActorUserId,
                       OccurredAtUtc, DiagnosticCode, SchemaVersion
                FROM TwCategoryRecoveryAuditEvent
                WHERE OperationId = @OperationId
                  AND EventSequence = @EventSequence
                  AND EventKey = @EventKey
                  AND Outcome IS NOT NULL
                  AND EventKind IN ('ReconciliationCommitted', 'NoChangeObserved')
                FOR UPDATE;
                """, new { OperationId = request.OperationId.ToString("D"), EventSequence = request.RecordedOutcomeSequence, EventKey = request.RecordedOutcomeEventKey }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            var validation = TwCategoryRecoveryDurabilityPolicy.ValidateCheckpointAdvance(operation.Checkpoint, request, recorded is not null);
            if (validation is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(MapCheckpointValidation(validation), DiagnosticCode: validation);
            }

            var affected = await session.Connection.ExecuteAsync(new CommandDefinition("""
                UPDATE TwCategoryRecoveryOperation
                SET CommittedPosition = @NextCommittedPosition,
                    CheckpointGeneration = CheckpointGeneration + 1,
                    LastOutcomeSequence = @RecordedOutcomeSequence,
                    UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE OperationId = @OperationId
                  AND ScopeFingerprint = @ScopeFingerprint
                  AND CommittedPosition = @ExpectedCommittedPosition
                  AND CheckpointGeneration = @ExpectedGeneration
                  AND LastOutcomeSequence = @ExpectedOutcomeSequence
                  AND SchemaVersion = @SchemaVersion;
                """, new
            {
                OperationId = request.OperationId.ToString("D"),
                request.ScopeFingerprint,
                request.ExpectedCommittedPosition,
                request.ExpectedGeneration,
                request.ExpectedOutcomeSequence,
                request.NextCommittedPosition,
                request.RecordedOutcomeSequence,
                request.SchemaVersion
            }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (affected != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.GenerationConflict, DiagnosticCode: "CheckpointGenerationConflict");
            }

            var updated = await session.Connection.QuerySingleAsync<OperationRow>(new CommandDefinition(OperationSelect + ";", new { OperationId = request.OperationId.ToString("D") }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (!TryMapOperation(updated, out var mapped, out diagnostic))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: diagnostic ?? "RecoveryOperationUnreadable");
            }
            await transaction.CommitAsync(cancellationToken);
            return new(TwCategoryRecoveryDurabilityDisposition.Advanced, mapped.Checkpoint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: "RecoveryDurabilityPersistenceFailure");
        }
    }

    public async Task<TwCategoryRecoveryTransitionOperationResult> TransitionOperationAsync(
        TwCategoryRecoveryTransitionOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.OperationId == Guid.Empty || request.ExpectedGeneration < 0 || request.SchemaVersion != TwCategoryRecoveryDurabilityConstants.CurrentSchemaVersion || !IsSafeToken(request.TerminalReason, 128, allowEmpty: true))
            return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: "InvalidOperationTransitionRequest");
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken);
            var row = await session.Connection.QuerySingleOrDefaultAsync<OperationRow>(new CommandDefinition(OperationSelect + " FOR UPDATE;", new { OperationId = request.OperationId.ToString("D") }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            TwCategoryRecoveryOperationRecord operation = null!;
            string? diagnostic = null;
            var operationMapped = row is not null && TryMapOperation(row, out operation, out diagnostic);
            if (!operationMapped)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: diagnostic ?? "RecoveryOperationMissing");
            }
            if (operation.Checkpoint.SchemaVersion != request.SchemaVersion)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.IncompatibleVersion, DiagnosticCode: "RecoveryDurabilityVersionUnsupported");
            }
            if (operation.Status != request.ExpectedStatus || operation.Checkpoint.Generation != request.ExpectedGeneration)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.GenerationConflict, DiagnosticCode: "OperationGenerationConflict");
            }
            if (!TwCategoryRecoveryDurabilityPolicy.CanTransition(operation.Status, request.NewStatus))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.InvalidTransition, DiagnosticCode: "InvalidOperationTransition");
            }
            var affected = await session.Connection.ExecuteAsync(new CommandDefinition("""
                UPDATE TwCategoryRecoveryOperation
                SET OperationStatus = @NewStatus, TerminalReason = @TerminalReason, UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE OperationId = @OperationId AND OperationStatus = @ExpectedStatus
                  AND CheckpointGeneration = @ExpectedGeneration AND SchemaVersion = @SchemaVersion;
                """, new
            {
                OperationId = request.OperationId.ToString("D"),
                ExpectedStatus = request.ExpectedStatus.ToString(),
                NewStatus = request.NewStatus.ToString(),
                request.TerminalReason,
                request.ExpectedGeneration,
                request.SchemaVersion
            }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (affected != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.GenerationConflict, DiagnosticCode: "OperationGenerationConflict");
            }
            var updated = await session.Connection.QuerySingleAsync<OperationRow>(new CommandDefinition(OperationSelect + ";", new { OperationId = request.OperationId.ToString("D") }, transaction: transaction.Transaction, cancellationToken: cancellationToken));
            if (!TryMapOperation(updated, out var mapped, out diagnostic))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: diagnostic ?? "RecoveryOperationUnreadable");
            }
            await transaction.CommitAsync(cancellationToken);
            return new(TwCategoryRecoveryDurabilityDisposition.Finalized, mapped);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: "RecoveryDurabilityPersistenceFailure");
        }
    }

    public async Task<TwCategoryRecoveryProgressResult> ReadScopedProgressAsync(
        TwCategoryRecoveryProgressRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.OperationId == Guid.Empty || !IsSafeToken(request.ScopeFingerprint, 64))
            return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: "InvalidProgressRequest");
        try
        {
            await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
            var operationRow = await session.Connection.QuerySingleOrDefaultAsync<OperationRow>(new CommandDefinition(OperationSelect + ";", new { OperationId = request.OperationId.ToString("D") }, cancellationToken: cancellationToken));
            TwCategoryRecoveryOperationRecord operation = null!;
            string? diagnostic = null;
            var operationMapped = operationRow is not null && TryMapOperation(operationRow, out operation, out diagnostic);
            if (!operationMapped)
                return new(TwCategoryRecoveryDurabilityDisposition.InvalidRequest, DiagnosticCode: diagnostic ?? "RecoveryOperationMissing");
            if (!string.Equals(operation.Scope.Fingerprint, request.ScopeFingerprint, StringComparison.Ordinal)
                || operation.ActorUserId != request.ActorUserId)
                return new(TwCategoryRecoveryDurabilityDisposition.ScopeConflict, DiagnosticCode: "RecoveryScopeConflict");

            var rows = await session.Connection.QueryAsync<OutcomeCountRow>(new CommandDefinition("""
                SELECT A.Outcome, COUNT(*) AS Count
                FROM TwCategoryRecoveryAuditEvent A
                INNER JOIN (
                    SELECT Position, MAX(EventSequence) AS EventSequence
                    FROM TwCategoryRecoveryAuditEvent
                    WHERE OperationId = @OperationId AND Outcome IS NOT NULL
                    GROUP BY Position
                ) Latest ON Latest.Position = A.Position
                    AND Latest.EventSequence = A.EventSequence
                    AND A.OperationId = @OperationId
                GROUP BY A.Outcome;
                """, new { OperationId = request.OperationId.ToString("D") }, cancellationToken: cancellationToken));
            var counts = new List<TwCategoryRecoveryOutcomeCount>();
            foreach (var row in rows)
            {
                if (!Enum.TryParse<TwCategoryRecoveryOutcomeKind>(row.Outcome, ignoreCase: false, out var outcome))
                    return new(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: "RecoveryOutcomeUnreadable");
                counts.Add(new(outcome, checked((int)row.Count)));
            }
            return new(TwCategoryRecoveryDurabilityDisposition.ProgressRead,
                new(operation.OperationId, operation.Scope.Fingerprint, operation.Status, operation.Checkpoint, counts, true));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(TwCategoryRecoveryDurabilityDisposition.Unavailable, DiagnosticCode: "RecoveryDurabilityPersistenceFailure");
        }
    }

    private static object OperationParameters(TwCategoryRecoveryOperationRecord value) => new
    {
        OperationId = value.OperationId.ToString("D"),
        ScopeFingerprint = value.Scope.Fingerprint,
        ScopeJson = JsonSerializer.Serialize(value.Scope.Targets),
        ActorUserId = value.ActorUserId?.ToString("D"),
        value.PolicyVersion,
        OperationStatus = value.Status.ToString(),
        value.TerminalReason,
        CommittedPosition = value.Checkpoint.CommittedPosition,
        CheckpointGeneration = value.Checkpoint.Generation,
        LastOutcomeSequence = value.Checkpoint.LastOutcomeSequence,
        value.Checkpoint.SchemaVersion,
        CreatedAtUtc = value.CreatedAtUtc.ToUniversalTime()
    };

    private static object AuditParameters(TwCategoryRecoveryAuditEvent value, long sequence) => new
    {
        OperationId = value.OperationId.ToString("D"),
        EventSequence = sequence,
        EventId = value.EventId.ToString("D"),
        value.EventKey,
        value.Position,
        AttemptId = value.AttemptId.ToString("D"),
        EventKind = value.Kind.ToString(),
        Confidence = value.Confidence.ToString(),
        value.PageId,
        value.ObservedRevision,
        Outcome = value.Outcome?.ToString(),
        ActorUserId = value.ActorUserId?.ToString("D"),
        OccurredAtUtc = value.OccurredAtUtc.ToUniversalTime(),
        value.DiagnosticCode,
        value.SchemaVersion
    };

    private static bool ValidateOperation(TwCategoryRecoveryOperationRecord value, out string diagnostic)
    {
        diagnostic = "InvalidRecoveryOperation";
        if (value is null || value.OperationId == Guid.Empty || value.Checkpoint.OperationId != value.OperationId || value.Checkpoint.CommittedPosition < 0 || value.Checkpoint.Generation < 0 || value.Checkpoint.LastOutcomeSequence < 0 || !IsSafeToken(value.PolicyVersion, 64) || value.Checkpoint.SchemaVersion != TwCategoryRecoveryDurabilityConstants.CurrentSchemaVersion)
            return false;
        if (!TwCategoryRecoveryScope.TryCreate(value.Scope.Targets, out var canonical, out var scopeDiagnostic)
            || !string.Equals(canonical.Fingerprint, value.Scope.Fingerprint, StringComparison.Ordinal)
            || !string.Equals(canonical.PolicyVersion, value.PolicyVersion, StringComparison.Ordinal))
        {
            diagnostic = scopeDiagnostic.Length == 0 ? "InvalidRecoveryScope" : scopeDiagnostic;
            return false;
        }
        if (!IsSafeToken(value.TerminalReason, 128, allowEmpty: true))
            return false;
        diagnostic = string.Empty;
        return true;
    }

    private static bool ValidateAudit(TwCategoryRecoveryAuditEvent value, out string diagnostic)
    {
        diagnostic = "InvalidRecoveryAuditEvent";
        if (value is null || value.EventId == Guid.Empty || value.OperationId == Guid.Empty || value.AttemptId == Guid.Empty || value.Position < 0 || value.PageId is <= 0 || value.ObservedRevision is <= 0 || value.SchemaVersion != TwCategoryRecoveryDurabilityConstants.CurrentSchemaVersion)
            return false;
        if (!IsSafeToken(value.EventKey, 192) || !IsSafeToken(value.DiagnosticCode, 128, allowEmpty: true))
            return false;
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryMapOperation(OperationRow row, out TwCategoryRecoveryOperationRecord operation, out string? diagnostic)
    {
        operation = null!;
        diagnostic = null;
        var scopeDiagnostic = string.Empty;
        if (!Enum.TryParse<TwCategoryRecoveryOperationStatus>(row.OperationStatus, false, out var status)
            || !TwCategoryRecoveryScope.TryCreate(JsonSerializer.Deserialize<List<TwCategoryRecoveryTarget>>(row.ScopeJson) ?? [], out var scope, out scopeDiagnostic)
            || !string.Equals(scope.Fingerprint, row.ScopeFingerprint, StringComparison.Ordinal)
            || !string.Equals(scope.PolicyVersion, row.PolicyVersion, StringComparison.Ordinal))
        {
            diagnostic = scopeDiagnostic.Length == 0 ? "RecoveryOperationDataInvalid" : scopeDiagnostic;
            return false;
        }
        if (row.SchemaVersion != TwCategoryRecoveryDurabilityConstants.CurrentSchemaVersion)
        {
            diagnostic = "RecoveryDurabilityVersionUnsupported";
            return false;
        }
        operation = new(row.OperationId, scope, row.ActorUserId, EnsureUtc(row.CreatedAtUtc), row.PolicyVersion, status,
            new(row.OperationId, row.ScopeFingerprint, checked((int)row.CommittedPosition), checked((long)row.CheckpointGeneration), checked((long)row.LastOutcomeSequence), checked((int)row.SchemaVersion)), row.TerminalReason);
        return true;
    }

    private static bool TryMapAudit(AuditRow row, out TwCategoryRecoveryAuditEvent audit, out string? diagnostic)
    {
        audit = null!;
        diagnostic = null;
        if (!Enum.TryParse<TwCategoryRecoveryAuditEventKind>(row.EventKind, false, out var kind)
            || !Enum.TryParse<TwCategoryRecoveryAuditConfidence>(row.Confidence, false, out var confidence)
            || !TryParseNullableEnum(row.Outcome, out TwCategoryRecoveryExecutionDisposition? outcome))
        {
            diagnostic = "RecoveryAuditDataInvalid";
            return false;
        }
        audit = new(row.EventId, row.EventKey, row.OperationId, checked((int)row.Position), row.AttemptId, kind, confidence, row.PageId is null ? null : checked((int)row.PageId.Value), row.ObservedRevision is null ? null : checked((int)row.ObservedRevision.Value), outcome, row.ActorUserId, EnsureUtc(row.OccurredAtUtc), row.DiagnosticCode, checked((int)row.SchemaVersion), checked((long)row.EventSequence));
        return true;
    }

    private static bool EquivalentAudit(TwCategoryRecoveryAuditEvent left, TwCategoryRecoveryAuditEvent right)
        => left with { Sequence = null, OccurredAtUtc = TruncateToMariaDbMicroseconds(left.OccurredAtUtc) }
            == right with { Sequence = null, OccurredAtUtc = TruncateToMariaDbMicroseconds(right.OccurredAtUtc) };

    private static DateTime TruncateToMariaDbMicroseconds(DateTime value)
        => new(value.Ticks - (value.Ticks % 10), DateTimeKind.Utc);

    private static bool GuidTryParseNullable(string? value, out Guid? result)
    {
        if (string.IsNullOrWhiteSpace(value)) { result = null; return true; }
        if (Guid.TryParse(value, out var parsed)) { result = parsed; return true; }
        result = null;
        return false;
    }

    private static bool TryParseNullableEnum(string? value, out TwCategoryRecoveryExecutionDisposition? result)
    {
        if (string.IsNullOrWhiteSpace(value)) { result = null; return true; }
        if (Enum.TryParse<TwCategoryRecoveryExecutionDisposition>(value, false, out var parsed)) { result = parsed; return true; }
        result = null;
        return false;
    }

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static bool IsSafeToken(string? value, int maximum, bool allowEmpty = false)
        => (allowEmpty && string.IsNullOrEmpty(value))
            || (!string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or ':' or '.'));

    private static TwCategoryRecoveryDurabilityDisposition MapCheckpointValidation(string value)
        => value switch
        {
            "CheckpointCannotMoveBackwards" => TwCategoryRecoveryDurabilityDisposition.BackwardsCheckpoint,
            "OutcomeNotRecorded" => TwCategoryRecoveryDurabilityDisposition.OutcomeNotRecorded,
            "CheckpointGenerationConflict" => TwCategoryRecoveryDurabilityDisposition.GenerationConflict,
            _ => TwCategoryRecoveryDurabilityDisposition.InvalidRequest
        };

    private sealed record OperationRow(Guid OperationId, string ScopeFingerprint, string ScopeJson, Guid? ActorUserId, string PolicyVersion, string OperationStatus, string? TerminalReason, uint CommittedPosition, ulong CheckpointGeneration, ulong LastOutcomeSequence, ulong LastAuditSequence, uint SchemaVersion, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
    private sealed record AuditRow(Guid OperationId, ulong EventSequence, Guid EventId, string EventKey, uint Position, Guid AttemptId, string EventKind, string Confidence, uint? PageId, uint? ObservedRevision, string? Outcome, Guid? ActorUserId, DateTime OccurredAtUtc, string? DiagnosticCode, uint SchemaVersion);
    private sealed record OutcomeCountRow(string Outcome, long Count);
}
