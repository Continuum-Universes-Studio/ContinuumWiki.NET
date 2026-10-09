using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TightWiki.Library;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Unit;

public sealed class ProviderNeutralPersistenceTests
{
    [Fact]
    public void UndeclaredLegacyPluginIsAllowedOnlyForSQLite()
    {
        Assert.True(PluginPersistenceCompatibility.Evaluate(null, TwPersistenceProvider.SQLite).IsSupported);

        var decision = PluginPersistenceCompatibility.Evaluate(null, TwPersistenceProvider.MySQL);
        Assert.False(decision.IsSupported);
        Assert.Contains("legacy SQLite-only", decision.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeclaredProviderNeutralPluginSupportsMySql()
    {
        var declaration = new TestCompatibility(
            new[] { TwPersistenceProvider.SQLite, TwPersistenceProvider.MySQL },
            RequiresLegacySqliteRepositories: false);

        Assert.True(PluginPersistenceCompatibility.Evaluate(declaration, TwPersistenceProvider.MySQL).IsSupported);
    }

    [Fact]
    public void LegacyRepositoryRequirementIsRejectedInMySqlMode()
    {
        var declaration = new TestCompatibility(
            new[] { TwPersistenceProvider.SQLite, TwPersistenceProvider.MySQL },
            RequiresLegacySqliteRepositories: true);

        var decision = PluginPersistenceCompatibility.Evaluate(declaration, TwPersistenceProvider.MySQL);
        Assert.False(decision.IsSupported);
        Assert.Contains("legacy SQLite repositories", decision.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlRegistrationExposesOnlyProviderNeutralContracts()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MySql"] = "Server=127.0.0.1;Database=test;User ID=test;Password=test;"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddMySqlApplicationPersistence(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<MySqlConfigurationRepository>(provider.GetRequiredService<ITwApplicationConfigurationRepository>());
        Assert.IsType<MySqlPageRepository>(provider.GetRequiredService<ITwApplicationPageRepository>());
        Assert.IsType<MySqlPageSupportRepository>(provider.GetRequiredService<ITwApplicationPageSupportRepository>());
        Assert.IsType<MySqlUsersRepository>(provider.GetRequiredService<ITwApplicationIdentityRepository>());
        Assert.IsType<MySqlOperationsRepository>(provider.GetRequiredService<ITwApplicationOperationsRepository>());
        Assert.IsType<MySqlDefaultsRepository>(provider.GetRequiredService<ITwApplicationDefaultsRepository>());
        Assert.IsType<MySqlTranslationRepository>(provider.GetRequiredService<ITwApplicationTranslationRepository>());
        Assert.IsType<MySqlApplicationMaintenanceRepository>(provider.GetRequiredService<ITwApplicationMaintenanceRepository>());
    }

    private sealed record TestCompatibility(
        IReadOnlyCollection<TwPersistenceProvider> SupportedProviders,
        bool RequiresLegacySqliteRepositories) : ITwPluginPersistenceCompatibility;
}
