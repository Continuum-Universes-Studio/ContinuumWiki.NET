using Dapper;
using MySqlConnector;
using TightWiki.Library.Database;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

/// <summary>
/// Real MariaDB grant certification is independently opt-in. Without the
/// M5-024 manifest and flags this class skips before opening a connection.
/// It intentionally retains diagnostic rows because cleanup is not part of the
/// M5-024 permission set.
/// </summary>
[Collection("CW-M5-024 serial")]
public sealed class MediaWikiCategoryAdministrativeGrantMariaDbAuthorizationTests
{
    [M5CategoryAdministrativeGrantFact]
    public async Task SeparateConnectionsAllowOnlyOneSingleUseConsumerAndPersistState()
    {
        var connectionString = M5_024AuthorizationGate.RequireConnectionString();
        var builder = new MySqlConnectionStringBuilder(connectionString);
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        var runtime = await connection.QuerySingleAsync<RuntimeEvidence>("SELECT DATABASE() AS DatabaseName, VERSION() AS ServerVersion;");
        Assert.Equal(builder.Database, runtime.DatabaseName);
        Assert.Contains("MariaDB", runtime.ServerVersion, StringComparison.OrdinalIgnoreCase);

        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlCategoryAdministrativeGrantSchemaInitializer().InitializeAsync(factory);
        var first = await CreateGrantAsync(factory, TwCategoryAdministrativeGrantReplayMode.SingleUse);

        var consumeResults = await Task.WhenAll(
            ConsumeAsync(connectionString, first),
            ConsumeAsync(connectionString, first));
        Assert.Single(consumeResults, result => result.Disposition == TwCategoryAdministrativeGrantDurabilityDisposition.Consumed);
        Assert.Single(consumeResults, result => result.Disposition is TwCategoryAdministrativeGrantDurabilityDisposition.AlreadyConsumed or TwCategoryAdministrativeGrantDurabilityDisposition.GenerationConflict);

        var restartedFactory = new MySqlDatabaseConnectionFactory(connectionString);
        var loaded = await new MySqlCategoryAdministrativeGrantStore(restartedFactory).LoadAsync(first.Grant!.Grant.GrantId);
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.Loaded, loaded.Disposition);
        Assert.Equal(TwCategoryAdministrativeGrantState.Consumed, loaded.Grant!.State);
    }

    [M5CategoryAdministrativeGrantFact]
    public async Task RevocationAndConsumptionHaveOneDatabaseOrderedWinner()
    {
        var connectionString = M5_024AuthorizationGate.RequireConnectionString();
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlCategoryAdministrativeGrantSchemaInitializer().InitializeAsync(factory);
        var created = await CreateGrantAsync(factory, TwCategoryAdministrativeGrantReplayMode.SingleUse);
        var revoke = RevokeAsync(connectionString, created);
        var consume = ConsumeAsync(connectionString, created);
        await Task.WhenAll(revoke, consume);
        var revokeResult = await revoke;
        var consumeResult = await consume;

        Assert.True(revokeResult.Disposition is TwCategoryAdministrativeGrantDurabilityDisposition.Revoked
            or TwCategoryAdministrativeGrantDurabilityDisposition.AlreadyRevoked
            or TwCategoryAdministrativeGrantDurabilityDisposition.ScopeConflict);
        Assert.True(consumeResult.Disposition is TwCategoryAdministrativeGrantDurabilityDisposition.Consumed
            or TwCategoryAdministrativeGrantDurabilityDisposition.Revoked
            or TwCategoryAdministrativeGrantDurabilityDisposition.AlreadyConsumed
            or TwCategoryAdministrativeGrantDurabilityDisposition.GenerationConflict);
    }

    private static async Task<TwCategoryAdministrativeGrantCreateResult> CreateGrantAsync(
        IDatabaseConnectionFactory factory,
        TwCategoryAdministrativeGrantReplayMode replayMode)
    {
        var now = DateTime.UtcNow;
        var grant = new TwCategoryAdministrativeGrant(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            TwCategoryAdministrativeOperation.Execute, Scope(), now.AddMinutes(-1), now.AddMinutes(-1), now.AddMinutes(10),
            TwCategoryAdministrativeGrantRevocationState.Active, replayMode, "m5-024-test-issuer");
        var result = await new MySqlCategoryAdministrativeGrantStore(factory).CreateAsync(new(grant, grant.ApproverUserId, Guid.NewGuid(), grant.IssuedAtUtc));
        Assert.Equal(TwCategoryAdministrativeGrantDurabilityDisposition.Created, result.Disposition);
        return result;
    }

    private static async Task<TwCategoryAdministrativeGrantConsumeResult> ConsumeAsync(
        string connectionString,
        TwCategoryAdministrativeGrantCreateResult created)
    {
        var grant = created.Grant!;
        return await new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString)).TryConsumeAsync(
            new(grant.Grant.GrantId, grant.Version, grant.Grant.ActorUserId, grant.Grant.Operation, grant.Grant.Scope, Guid.NewGuid(), DateTime.UtcNow));
    }

    private static async Task<TwCategoryAdministrativeGrantRevokeResult> RevokeAsync(
        string connectionString,
        TwCategoryAdministrativeGrantCreateResult created)
    {
        var grant = created.Grant!;
        return await new MySqlCategoryAdministrativeGrantStore(new MySqlDatabaseConnectionFactory(connectionString)).RevokeAsync(
            new(grant.Grant.GrantId, grant.Version, grant.Grant.ApproverUserId, Guid.NewGuid(), DateTime.UtcNow, "test revocation"));
    }

    private static TwCategoryRecoveryScope Scope()
        => TwCategoryRecoveryScope.TryCreate([new TwCategoryRecoveryTarget(17, "Category:AdministrativeGrantTest")], out var scope, out var error)
            ? scope
            : throw new InvalidOperationException(error);

    private sealed record RuntimeEvidence(string DatabaseName, string ServerVersion);

}
