using Dapper;
using MySqlConnector;
using TightWiki.Library.Database;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

/// <summary>
/// Real MariaDB 11.8 certification is opt-in only. Ordinary test runs skip
/// before opening a connection. The exact target, identity, approval file and
/// mutation/cleanup scope must be supplied by the operator.
/// </summary>
[Collection("CW-M5-019 serial")]
public sealed class MediaWikiCategoryRecoveryDurabilityAuthorizationTests
{
    [M5CategoryRecoveryFact]
    public async Task AuthorizedDisposableMariaDbStoreRoundTripAndCasAreEnforced()
    {
        var connectionString = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION")!;
        var targetDatabase = M5_019AuthorizationGate.IsConfigured
            ? M5_019AuthorizationGate.RequireTargetDatabase()
            : Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_018_TARGET_DATABASE")!;
        var builder = new MySqlConnectionStringBuilder(connectionString);
        Assert.Equal(targetDatabase, builder.Database);
        Assert.StartsWith(M5_019AuthorizationGate.IsConfigured ? "cw_m5_019_" : "cw_m5_018_", builder.Database, StringComparison.Ordinal);
        Assert.True(builder.Port > 1024);
        Assert.Contains(builder.Server, new[] { "127.0.0.1", "localhost", "::1" }, StringComparer.OrdinalIgnoreCase);

        await using var runtimeConnection = new MySqlConnection(builder.ConnectionString);
        await runtimeConnection.OpenAsync();
        var runtime = await runtimeConnection.QuerySingleAsync<RuntimeEvidence>("SELECT DATABASE() AS DatabaseName, VERSION() AS ServerVersion;");
        Assert.Equal(targetDatabase, runtime.DatabaseName);
        Assert.StartsWith("11.8.", runtime.ServerVersion, StringComparison.Ordinal);
        Assert.Contains("MariaDB", runtime.ServerVersion, StringComparison.OrdinalIgnoreCase);

        var factory = new MySqlDatabaseConnectionFactory(builder.ConnectionString);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        await new MySqlCategorySchemaInitializer().InitializeAsync(factory);
        await new MySqlCategoryRecoveryDurabilitySchemaInitializer().InitializeAsync(factory);
        var store = new MySqlCategoryRecoveryDurabilityStore(factory);
        var operationId = Guid.NewGuid();
        var scope = TwCategoryRecoveryScope.TryCreate([new TwCategoryRecoveryTarget(17, "Category:Example")], out var createdScope, out var scopeError)
            ? createdScope
            : throw new InvalidOperationException(scopeError);
        var operation = new TwCategoryRecoveryOperationRecord(operationId, scope, null, DateTime.UtcNow, TwCategoryRecoveryDurabilityConstants.PolicyVersion, TwCategoryRecoveryOperationStatus.Planned, new(operationId, scope.Fingerprint, 0, 0, 0));

        try
        {
            var created = await store.CreateOrReplayOperationAsync(operation);
            Assert.True(created.Disposition == TwCategoryRecoveryDurabilityDisposition.Created, $"{created.Disposition}:{created.DiagnosticCode}");
            var replayed = await store.CreateOrReplayOperationAsync(operation with { CreatedAtUtc = DateTime.UtcNow.AddMinutes(1) });
            Assert.True(replayed.Disposition == TwCategoryRecoveryDurabilityDisposition.Replayed, $"{replayed.Disposition}:{replayed.DiagnosticCode}");

            var attemptId = Guid.NewGuid();
            var audit = new TwCategoryRecoveryAuditEvent(Guid.NewGuid(), $"{operationId:N}:outcome:0", operationId, 0, attemptId, TwCategoryRecoveryAuditEventKind.NoChangeObserved, TwCategoryRecoveryAuditConfidence.Observation, 17, 4, TwCategoryRecoveryExecutionDisposition.NoChange, null, DateTime.UtcNow);
            var appended = await store.AppendAuditEventAsync(audit);
            Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Appended, appended.Disposition);
            Assert.Equal(TwCategoryRecoveryDurabilityDisposition.DuplicateSame, (await store.AppendAuditEventAsync(audit)).Disposition);

            var advanced = await store.AdvanceCheckpointAsync(new(operationId, scope.Fingerprint, 0, 0, 0, 1, appended.EventSequence!.Value, audit.EventKey, 1));
            Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Advanced, advanced.Disposition);
            var stale = await store.AdvanceCheckpointAsync(new(operationId, scope.Fingerprint, 0, 0, 0, 1, appended.EventSequence.Value, audit.EventKey, 1));
            Assert.Equal(TwCategoryRecoveryDurabilityDisposition.GenerationConflict, stale.Disposition);

            var running = await store.TransitionOperationAsync(new(operationId, TwCategoryRecoveryOperationStatus.Planned, TwCategoryRecoveryOperationStatus.Running, 1));
            Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Finalized, running.Disposition);
            var completed = await store.TransitionOperationAsync(new(operationId, TwCategoryRecoveryOperationStatus.Running, TwCategoryRecoveryOperationStatus.Completed, 1, "CertificationComplete"));
            Assert.Equal(TwCategoryRecoveryDurabilityDisposition.Finalized, completed.Disposition);

            var progress = await store.ReadScopedProgressAsync(new(operationId, scope.Fingerprint, null));
            Assert.True(progress.Disposition == TwCategoryRecoveryDurabilityDisposition.ProgressRead, $"{progress.Disposition}:{progress.DiagnosticCode}");
            Assert.True(progress.Progress!.Redacted);
        }
        finally
        {
            if (!M5_019AuthorizationGate.IsConfigured || M5_019AuthorizationGate.DatabaseCleanupAuthorized)
            {
                await using var cleanup = await factory.OpenSessionAsync();
                await using var transaction = await cleanup.BeginTransactionAsync();
                await cleanup.Connection.ExecuteAsync("DELETE FROM TwCategoryRecoveryAuditEvent WHERE OperationId = @OperationId; DELETE FROM TwCategoryRecoveryOperation WHERE OperationId = @OperationId;", new { OperationId = operationId.ToString("D") }, transaction.Transaction);
                await transaction.CommitAsync();
            }
        }
    }

    private sealed record RuntimeEvidence(string DatabaseName, string ServerVersion);
}

internal sealed class M5CategoryRecoveryFactAttribute : FactAttribute
{
    public M5CategoryRecoveryFactAttribute()
    {
        if (M5_019AuthorizationGate.IsConfigured)
        {
            var decision = M5_019AuthorizationGate.Evaluate();
            if (!decision.IsAuthorized)
                Skip = decision.Reason;
            return;
        }

        var legacyDecision = M5HostCertificationGate.EvaluateForScope(
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_RUN_MYSQL_TESTS"),
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION"),
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_018_AUTHORIZED"),
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_018_TARGET_DATABASE"),
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_018_TARGET_IDENTITY"),
            Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_018_APPROVAL_PATH"),
            "cw_m5_018_",
            "continuumwiki_m5_018_app");
        if (!legacyDecision.IsAuthorized)
            Skip = legacyDecision.Reason;
    }
}
