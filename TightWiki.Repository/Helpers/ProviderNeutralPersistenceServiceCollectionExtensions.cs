using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TightWiki.Library.Database;
using TightWiki.Plugin.Interfaces.Persistence;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Registers the provider-neutral application contracts without changing the
/// legacy ITw* plugin registrations. This is the MySQL application seam; SQLite
/// continues to use the established legacy graph until its adapters are selected.
/// </summary>
public static class ProviderNeutralPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddMySqlApplicationPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MySql")
            ?? configuration["Database:MySql:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("A MySQL connection string is required at ConnectionStrings:MySql or Database:MySql:ConnectionString.");

        services.AddSingleton<IDatabaseConnectionFactory>(_ => new MySqlDatabaseConnectionFactory(connectionString));
        services.AddSingleton<ITwApplicationConfigurationRepository, MySqlConfigurationRepository>();
        services.AddSingleton<ITwApplicationPageRepository, MySqlPageRepository>();
        services.AddSingleton<ITwApplicationPageSupportRepository, MySqlPageSupportRepository>();
        services.AddSingleton<ITwApplicationIdentityRepository, MySqlUsersRepository>();
        services.AddSingleton<ITwApplicationOperationsRepository, MySqlOperationsRepository>();
        services.AddSingleton<ITwApplicationDefaultsRepository, MySqlDefaultsRepository>();
        services.AddSingleton<ITwApplicationTranslationRepository, MySqlTranslationRepository>();
        services.AddSingleton<ITwApplicationMaintenanceRepository, MySqlApplicationMaintenanceRepository>();
        return services;
    }
}
