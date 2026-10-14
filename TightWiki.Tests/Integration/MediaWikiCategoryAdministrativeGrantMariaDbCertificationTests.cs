using Dapper;
using MySqlConnector;
using TightWiki.Library.Database;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

/// <summary>
/// CW-M5-025 live certification. Every test is gated before its first
/// connection. Rows are intentionally retained because cleanup is prohibited
/// by the required authorization contract.
/// </summary>
[Collection("CW-M5-025 serial")]
public sealed class MediaWikiCategoryAdministrativeGrantMariaDbCertificationTests
{
    [M5CategoryAdministrativeGrantMariaDbFact]
    public async Task V3MigrationReadbackAndSeparateConnectionsHaveOneConsumeWinner()
    {
        var connectionString = await PrepareAsync();
        var created = await CreateGrantAsync(connectionString, TwCategoryAdministrativeGrantReplayMode.SingleUse);
        var first = ConsumeAsync(connectionString, created);
        var second = ConsumeAsync(connectionString, created);
        await Task.WhenAll(first, second);

        var results = new[] { await first, await second };
        Assert.Single(results, result => result.Disposition == TwCategoryAdministrativeGrantDurabilityDisposition.Consumed);
        Assert.Single(results, result => result.Disposition is TwCategoryAdministrativeGrantDurabilityDisposition.AlreadyConsumed or TwCategoryAdministrativeGrantDurabilityDisposition.GenerationConflict);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var schema = await connection.QuerySingleAsync<SchemaEvidence>("""
            SELECT (SELECT Version FROM MySqlCategorySchemaVersion WHERE Domain = 'category-admin-grants') AS GrantVersion,
                   (SELECT ENGINE FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'TwCategoryAdministrativeGrant') AS GrantEngine,
                   (SELECT ENGINE FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'TwCategoryAdministrativeGrantAudit') AS AuditEngine,
                   (SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'TwCategoryAdministrativeGrant' AND INDEX_NAME = 'PRIMARY') AS GrantPrimaryKey,
                   (SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'TwCategoryAdministrativeGrantAudit' AND INDEX_NAME = 'UQ_TwCategoryAdministrativeGrantAudit_EventKey') AS AuditEventKeyIndex;
            """);
        Assert.Equal(MySqlCategoryAdministrativeGrantSchemaInitializer.CurrentSchemaVersion, schema.GrantVersion);
        Assert.Equal("InnoDB", schema.GrantEngine);
        Assert.Equal("InnoDB", schema.AuditEngine);
        Assert.True(schema.GrantPrimaryKey > 0);
        Assert.True(schema.AuditEventKeyIndex > 0);
    }

    [M5CategoryAdministrativeGrantMariaDbFact]
    public async Task ExpiredAndPrematureGrantsAreRejectedAtTransactionBoundary()
    {
        var connectionString = await PrepareAsync();
        var now = DateTime.UtcNow;
        var expired = await CreateGrantAsync(connectionString, TwCategoryAdministrativeGrantReplayMode.SingleUse, now.AddMinutes(-5), now.AddSeconds(-1));
        var premature = await CreateGrantAsync(connectionString, TwCategoryAdministrativeGrantReplayMode.SingleUse, now.AddMinutes(5), now.AddMinutes(10));

        var expiredResult = await ConsumeAsync(connectionString, expired, now);
        var prematureResult = await ConsumeAsync(connectionString, premature, now);

        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, expiredResult.Disposition);
        Assert.Equal("GrantExpired", expiredResult.DiagnosticCode);
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.InvalidRequest, prematureResult.Disposition);
        Assert.Equal("GrantNotYetValid", prematureResult.DiagnosticCode);
    }

    [M5CategoryAdministrativeGrantMariaDbFact]
    public async Task ActorActionAndScopeMismatchesRemainRejected()
    {
        var connectionString = await PrepareAsync();
        var created = await CreateGrantAsync(connectionString, TwCategoryAdministrativeGrantReplayMode.SingleUse);
        var grant = created.Grant!;
        var mismatchedActor = await new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString)).TryConsumeAsync(
            new(grant.Grant.GrantId, grant.Version, Guid.NewGuid(), grant.Grant.Operation, grant.Grant.Scope, Guid.NewGuid(), DateTime.UtcNow));
        var mismatchedAction = await new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString)).TryConsumeAsync(
            new(grant.Grant.GrantId, grant.Version, grant.Grant.ActorUserId, TwCategoryAdministrativeOperation.Preview, grant.Grant.Scope, Guid.NewGuid(), DateTime.UtcNow));
        var otherScope = TwCategoryRecoveryScope.TryCreate([new TwCategoryRecoveryTarget(18, "Category:Other")], out var scope, out var error)
            ? scope
            : throw new InvalidOperationException(error);
        var mismatchedScope = await new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString)).TryConsumeAsync(
            new(grant.Grant.GrantId, grant.Version, grant.Grant.ActorUserId, grant.Grant.Operation, otherScope, Guid.NewGuid(), DateTime.UtcNow));

        Assert.All(new[] { mismatchedActor, mismatchedAction, mismatchedScope }, result =>
        {
            Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.ScopeConflict, result.Disposition);
            Assert.Equal("GrantBindingMismatch", result.DiagnosticCode);
        });
        var unchanged = await new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString)).LoadAsync(grant.Grant.GrantId);
        Assert.Equal(TwCategoryAdministrativeGrantState.Issued, unchanged.Grant!.State);
    }

    [M5CategoryAdministrativeGrantMariaDbFact]
    public async Task RevokeAndConsumeRaceHasOneDatabaseOrderedWinner()
    {
        var connectionString = await PrepareAsync();
        var created = await CreateGrantAsync(connectionString, TwCategoryAdministrativeGrantReplayMode.SingleUse);
        var revoke = RevokeAsync(connectionString, created);
        var consume = ConsumeAsync(connectionString, created);
        await Task.WhenAll(revoke, consume);
        var revokeResult = await revoke;
        var consumeResult = await consume;

        Assert.True(revokeResult.Disposition is TwCategoryAdministrativeGrantDurabilityDisposition.Revoked
            or TwCategoryAdministrativeGrantDurabilityDisposition.ScopeConflict
            or TwCategoryAdministrativeGrantDurabilityDisposition.GenerationConflict);
        Assert.True(consumeResult.Disposition is TwCategoryAdministrativeGrantDurabilityDisposition.Consumed
            or TwCategoryAdministrativeGrantDurabilityDisposition.Revoked
            or TwCategoryAdministrativeGrantDurabilityDisposition.GenerationConflict);
        Assert.False(revokeResult.Disposition == TwCategoryAdministrativeGrantDurabilityDisposition.Revoked
            && consumeResult.Disposition == TwCategoryAdministrativeGrantDurabilityDisposition.Consumed);
    }

    [M5CategoryAdministrativeGrantMariaDbFact]
    public async Task AuditReplayIsIdempotentAndConflictingOutcomeFailsClosed()
    {
        var connectionString = await PrepareAsync();
        var created = await CreateGrantAsync(connectionString, TwCategoryAdministrativeGrantReplayMode.ReusableWithinValidityWindow);
        var grant = created.Grant!;
        var store = new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString));
        var request = new TwCategoryAdministrativeGrantExecutionOutcomeRequest(
            grant.Grant.GrantId, grant.Grant.ActorUserId, grant.Grant.Operation, grant.Grant.Scope,
            Guid.NewGuid(), "Completed", DateTime.UtcNow);
        var recorded = await store.RecordExecutionOutcomeAsync(request);
        var duplicate = await store.RecordExecutionOutcomeAsync(request);
        var conflict = await store.RecordExecutionOutcomeAsync(request with { Outcome = "Failed" });
        var loaded = await store.LoadAsync(grant.Grant.GrantId);

        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.OutcomeRecorded, recorded.Disposition);
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.DuplicateSame, duplicate.Disposition);
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.DuplicateConflict, conflict.Disposition);
        Assert.Equal(2L, loaded.Grant!.LastAuditSequence);
    }

    [M5CategoryAdministrativeGrantMariaDbFact]
    public async Task RecreatedStoreCannotReplayConsumedGrantAndAcknowledgmentLossIsIndeterminate()
    {
        var connectionString = await PrepareAsync();
        var created = await CreateGrantAsync(connectionString, TwCategoryAdministrativeGrantReplayMode.SingleUse);
        var grant = created.Grant!;
        var consumingStore = new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString));
        try
        {
            var committed = await consumingStore.TryConsumeAsync(new(grant.Grant.GrantId, grant.Version, grant.Grant.ActorUserId, grant.Grant.Operation, grant.Grant.Scope, Guid.NewGuid(), DateTime.UtcNow));
            Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.Consumed, committed.Disposition);
            throw new SimulatedAcknowledgmentLossException();
        }
        catch (SimulatedAcknowledgmentLossException)
        {
            // The response was lost after the store committed. The next
            // service-equivalent instance must consult authoritative state.
        }

        var restartedStore = new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString));
        var readBack = await restartedStore.LoadAsync(grant.Grant.GrantId);
        var replay = await restartedStore.TryConsumeAsync(new(grant.Grant.GrantId, grant.Version, grant.Grant.ActorUserId, grant.Grant.Operation, grant.Grant.Scope, Guid.NewGuid(), DateTime.UtcNow));
        Assert.Equal(TwCategoryAdministrativeGrantState.Consumed, readBack.Grant!.State);
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.AlreadyConsumed, replay.Disposition);
    }

    [M5CategoryAdministrativeGrantMariaDbFact]
    public async Task ForcedFailureBeforeGrantAuditRollsBackStateAndAuditTogether()
    {
        var connectionString = await PrepareAsync();
        var created = await CreateGrantAsync(connectionString, TwCategoryAdministrativeGrantReplayMode.SingleUse);
        var grant = created.Grant!;
        var faultingStore = new MySqlCategoryAdministrativeGrantStore(
            new MySqlDatabaseConnectionFactory(connectionString), new FailBeforeAuditInjector());

        var result = await faultingStore.TryConsumeAsync(new(grant.Grant.GrantId, grant.Version, grant.Grant.ActorUserId, grant.Grant.Operation, grant.Grant.Scope, Guid.NewGuid(), DateTime.UtcNow));
        var readBack = await new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString)).LoadAsync(grant.Grant.GrantId);

        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.Unavailable, result.Disposition);
        Assert.Equal(TwCategoryAdministrativeGrantState.Issued, readBack.Grant!.State);
        Assert.Equal(0L, readBack.Grant.Version);
        Assert.Equal(1L, readBack.Grant.LastAuditSequence);
    }

    [M5CategoryAdministrativeGrantMariaDbFact]
    public async Task ConsumptionDoesNotImplyRecoveryOutcome()
    {
        var connectionString = await PrepareAsync();
        var created = await CreateGrantAsync(connectionString, TwCategoryAdministrativeGrantReplayMode.SingleUse);
        var grant = created.Grant!;
        var store = new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString));
        var consumed = await store.TryConsumeAsync(new(grant.Grant.GrantId, grant.Version, grant.Grant.ActorUserId, grant.Grant.Operation, grant.Grant.Scope, Guid.NewGuid(), DateTime.UtcNow));
        var afterConsume = await store.LoadAsync(grant.Grant.GrantId);

        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.Consumed, consumed.Disposition);
        Assert.Equal(TwCategoryAdministrativeGrantState.Consumed, afterConsume.Grant!.State);
        Assert.Equal(2L, afterConsume.Grant.LastAuditSequence);

        var outcome = await store.RecordExecutionOutcomeAsync(new(
            grant.Grant.GrantId, grant.Grant.ActorUserId, grant.Grant.Operation, grant.Grant.Scope,
            Guid.NewGuid(), "RecoveryIndeterminate", DateTime.UtcNow));
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.OutcomeRecorded, outcome.Disposition);
    }

    private static async Task<string> PrepareAsync()
    {
        var connectionString = M5_025AuthorizationGate.RequireConnectionString();
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await MySqlRuntimeReadiness.ValidateServerAsync(factory);
        // Category V1 has foreign keys to the existing Page domain. The
        // certification prerequisite sequence must establish that dependency
        // before applying the category schema; a fresh disposable target does
        // not contain the base schema from provisioning.
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        await new MySqlCategorySchemaInitializer().InitializeAsync(factory);
        await new MySqlCategoryRecoveryDurabilitySchemaInitializer().InitializeAsync(factory);
        await new MySqlCategoryAdministrativeGrantSchemaInitializer().InitializeAsync(factory);
        return connectionString;
    }

    private static async Task<TwCategoryAdministrativeGrantCreateResult> CreateGrantAsync(
        string connectionString,
        TwCategoryAdministrativeGrantReplayMode replayMode,
        DateTime? notBefore = null,
        DateTime? expires = null)
    {
        var now = DateTime.UtcNow;
        var grant = new TwCategoryAdministrativeGrant(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), TwCategoryAdministrativeOperation.Execute,
            Scope(), now.AddMinutes(-1), notBefore ?? now.AddMinutes(-1), expires ?? now.AddMinutes(10),
            TwCategoryAdministrativeGrantRevocationState.Active, replayMode, "m5-025-test-issuer");
        var result = await new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString)).CreateAsync(
            new(grant, grant.ApproverUserId, Guid.NewGuid(), grant.IssuedAtUtc));
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.Created, result.Disposition);
        return result;
    }

    private static Task<TwCategoryAdministrativeGrantConsumeResult> ConsumeAsync(
        string connectionString,
        TwCategoryAdministrativeGrantCreateResult created,
        DateTime? now = null)
    {
        var grant = created.Grant!;
        return new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString)).TryConsumeAsync(
            new(grant.Grant.GrantId, grant.Version, grant.Grant.ActorUserId, grant.Grant.Operation, grant.Grant.Scope, Guid.NewGuid(), now ?? DateTime.UtcNow));
    }

    private static Task<TwCategoryAdministrativeGrantRevokeResult> RevokeAsync(
        string connectionString,
        TwCategoryAdministrativeGrantCreateResult created)
    {
        var grant = created.Grant!;
        return new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString)).RevokeAsync(
            new(grant.Grant.GrantId, grant.Version, grant.Grant.ApproverUserId, Guid.NewGuid(), DateTime.UtcNow, "M5-025 test revocation"));
    }

    private static TwCategoryRecoveryScope Scope()
        => TwCategoryRecoveryScope.TryCreate([new TwCategoryRecoveryTarget(17, "Category:M5-025")], out var scope, out var error)
            ? scope
            : throw new InvalidOperationException(error);

    private sealed record SchemaEvidence(int GrantVersion, string GrantEngine, string AuditEngine, long GrantPrimaryKey, long AuditEventKeyIndex);

    private sealed class FailBeforeAuditInjector : ITwCategoryAdministrativeGrantFaultInjector
    {
        public Task InjectAsync(TwCategoryAdministrativeGrantFaultBoundary boundary, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("M5-025 simulated failure before grant audit commit");
    }

    private sealed class SimulatedAcknowledgmentLossException : Exception;
}
