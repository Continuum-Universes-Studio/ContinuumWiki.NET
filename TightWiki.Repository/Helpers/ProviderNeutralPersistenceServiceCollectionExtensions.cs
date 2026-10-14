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

    /// <summary>
    /// Explicitly opts a managed MariaDB host into category persistence. The
    /// normal application registrations intentionally do not call this method.
    /// The caller must provision and validate the category schema separately.
    /// </summary>
    public static IServiceCollection AddAuthorizedMySqlCategoryPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        MySqlCategoryPersistenceReadinessProof readiness)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(readiness);

        if (DatabaseProviderConfiguration.GetProvider(configuration) != DatabaseProviderConfiguration.MySQL)
            throw new InvalidOperationException("Category persistence requires Database:Provider=mysql.");
        if (!configuration.GetValue<bool>("Database:CategoryPersistence:Enabled"))
            throw new InvalidOperationException("Category persistence requires explicit Database:CategoryPersistence:Enabled=true.");
        if (!string.Equals(configuration["Database:CategoryPersistence:SchemaVersion"], "1", StringComparison.Ordinal)
            || readiness.SchemaVersion != MySqlCategorySchemaInitializer.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(readiness.DatabaseName))
            throw new InvalidOperationException("Category persistence requires an explicitly approved schema version of 1.");
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IDatabaseConnectionFactory)))
            throw new InvalidOperationException("Category persistence requires an existing provider connection factory registration.");

        services.AddSingleton<ITwCategoryMembershipRepository>(provider =>
            new MySqlCategoryMembershipRepository(provider.GetRequiredService<IDatabaseConnectionFactory>()));
        return services;
    }

    /// <summary>
    /// Explicitly opts a previously proved MariaDB target into the category
    /// recovery durability store. The default graph remains rejecting and no
    /// startup path calls this method.
    /// </summary>
    public static IServiceCollection AddAuthorizedMySqlCategoryRecoveryDurability(
        this IServiceCollection services,
        IConfiguration configuration,
        MySqlCategoryRecoveryDurabilityReadinessProof readiness)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(readiness);

        if (DatabaseProviderConfiguration.GetProvider(configuration) != DatabaseProviderConfiguration.MySQL)
            throw new InvalidOperationException("Category recovery durability requires Database:Provider=mysql.");
        if (!configuration.GetValue<bool>("Database:CategoryRecoveryDurability:Enabled"))
            throw new InvalidOperationException("Category recovery durability requires explicit Database:CategoryRecoveryDurability:Enabled=true.");
        if (!string.Equals(configuration["Database:CategoryRecoveryDurability:SchemaVersion"], "2", StringComparison.Ordinal)
            || readiness.SchemaVersion != MySqlCategoryRecoveryDurabilitySchemaInitializer.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(readiness.DatabaseName))
            throw new InvalidOperationException("Category recovery durability requires an explicitly approved schema version of 2.");
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IDatabaseConnectionFactory)))
            throw new InvalidOperationException("Category recovery durability requires an existing provider connection factory registration.");

        services.AddSingleton<ITwCategoryRecoveryDurabilityStore>(provider =>
            new MySqlCategoryRecoveryDurabilityStore(provider.GetRequiredService<IDatabaseConnectionFactory>()));
        return services;
    }

    /// <summary>
    /// Explicitly opts a previously proved MariaDB target into durable
    /// administrative grants. This does not run the schema initializer and is
    /// never called by application startup.
    /// </summary>
    public static IServiceCollection AddAuthorizedMySqlCategoryAdministrativeGrants(
        this IServiceCollection services,
        IConfiguration configuration,
        MySqlCategoryAdministrativeGrantReadinessProof readiness)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(readiness);

        if (DatabaseProviderConfiguration.GetProvider(configuration) != DatabaseProviderConfiguration.MySQL)
            throw new InvalidOperationException("Administrative grants require Database:Provider=mysql.");
        if (!configuration.GetValue<bool>("Database:CategoryAdministrativeGrants:Enabled"))
            throw new InvalidOperationException("Administrative grants require explicit Database:CategoryAdministrativeGrants:Enabled=true.");
        if (!string.Equals(configuration["Database:CategoryAdministrativeGrants:SchemaVersion"], "3", StringComparison.Ordinal)
            || readiness.SchemaVersion != MySqlCategoryAdministrativeGrantSchemaInitializer.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(readiness.DatabaseName))
            throw new InvalidOperationException("Administrative grants require an explicitly approved schema version of 3.");
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IDatabaseConnectionFactory)))
            throw new InvalidOperationException("Administrative grants require an existing provider connection factory registration.");

        services.AddSingleton<ITwCategoryAdministrativeGrantStore>(provider =>
            new MySqlCategoryAdministrativeGrantStore(provider.GetRequiredService<IDatabaseConnectionFactory>()));
        return services;
    }
}
