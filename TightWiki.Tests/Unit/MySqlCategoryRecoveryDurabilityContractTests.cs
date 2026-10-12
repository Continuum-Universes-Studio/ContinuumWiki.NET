using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TightWiki.Library;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Unit;

public sealed class MySqlCategoryRecoveryDurabilityContractTests
{
    [Fact]
    public void V2MigrationSeparatesOperationStateFromAppendOnlyAuditAndContainsRequiredGuards()
    {
        var sql = EmbeddedResourceReader.LoadText("MySql.Category.V2__CategoryRecoveryDurability.sql");

        Assert.Contains("CREATE TABLE IF NOT EXISTS TwCategoryRecoveryOperation", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS TwCategoryRecoveryAuditEvent", sql, StringComparison.Ordinal);
        Assert.Contains("CheckpointGeneration BIGINT UNSIGNED NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("LastOutcomeSequence BIGINT UNSIGNED NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("LastAuditSequence BIGINT UNSIGNED NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("UQ_TwCategoryRecoveryAuditEvent_EventKey UNIQUE (OperationId, EventKey)", sql, StringComparison.Ordinal);
        Assert.Contains("UQ_TwCategoryRecoveryAuditEvent_EventId UNIQUE (EventId)", sql, StringComparison.Ordinal);
        Assert.Contains("FK_TwCategoryRecoveryAuditEvent_Operation", sql, StringComparison.Ordinal);
        Assert.Contains("Domain, Version, AppliedAtUtc", sql, StringComparison.Ordinal);
        Assert.Contains("'category-recovery', 2", sql, StringComparison.Ordinal);
        Assert.Equal(4096, TwCategoryRecoveryDurabilityConstants.MaxAuditEventsPerOperation);
    }

    [Fact]
    public void ProviderIsNotRegisteredByOrdinaryMySqlApplicationPersistence()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "mysql",
                ["ConnectionStrings:MySql"] = "Server=127.0.0.1;Port=13318;Database=cw_m5_018_contract;User ID=contract;Password=not-used;"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddMySqlApplicationPersistence(configuration);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ITwCategoryRecoveryDurabilityStore));
    }

    [Fact]
    public void ExplicitActivationRequiresEnabledVersionAndExistingFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "mysql",
                ["Database:CategoryRecoveryDurability:Enabled"] = "true",
                ["Database:CategoryRecoveryDurability:SchemaVersion"] = "2"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<TightWiki.Library.Database.IDatabaseConnectionFactory>(_ => throw new InvalidOperationException("not called by activation"));

        services.AddAuthorizedMySqlCategoryRecoveryDurability(
            configuration,
            new MySqlCategoryRecoveryDurabilityReadinessProof("cw_m5_018_contract", 2));

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ITwCategoryRecoveryDurabilityStore));
    }

    [Fact]
    public void ExplicitActivationRejectsMissingOptInOrWrongSchemaVersion()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "mysql",
                ["Database:CategoryRecoveryDurability:SchemaVersion"] = "1"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<TightWiki.Library.Database.IDatabaseConnectionFactory>(_ => throw new InvalidOperationException("not called by activation"));

        Assert.Throws<InvalidOperationException>(() => services.AddAuthorizedMySqlCategoryRecoveryDurability(
            configuration,
            new MySqlCategoryRecoveryDurabilityReadinessProof("cw_m5_018_contract", 2)));
    }
}
