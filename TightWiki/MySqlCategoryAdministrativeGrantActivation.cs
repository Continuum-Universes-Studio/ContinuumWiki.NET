using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TightWiki.Engine.MediaWiki;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;

namespace TightWiki;

/// <summary>
/// Explicit application-layer activation for the durable administrative-grant
/// provider. No startup path calls this method; the caller must first obtain an
/// independently approved schema/readiness proof for the exact target.
/// </summary>
public static class MySqlCategoryAdministrativeGrantActivation
{
    public static IServiceCollection AddAuthorizedMySqlCategoryAdministrativeGrantAuthorization(
        this IServiceCollection services,
        IConfiguration configuration,
        MySqlCategoryAdministrativeGrantReadinessProof readiness)
    {
        services.AddAuthorizedMySqlCategoryAdministrativeGrants(configuration, readiness);
        services.AddSingleton<ITwCategoryAdministrativeGrantProvider>(provider =>
            new TwCategoryAdministrativeGrantProvider(
                provider.GetRequiredService<ITwCategoryAdministrativeGrantStore>(),
                provider.GetRequiredService<ITwCategoryAdministrativeGrantStore>()));
        return services;
    }
}
