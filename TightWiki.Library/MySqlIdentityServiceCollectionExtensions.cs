using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace TightWiki.Library;

/// <summary>
/// Registers the bounded MySQL Identity context for an explicitly selected
/// provider plan. The application does not call this until all repositories can
/// leave SQLite together.
/// </summary>
public static class MySqlIdentityServiceCollectionExtensions
{
    public static IdentityBuilder AddMySqlIdentity(
        this IServiceCollection services,
        string connectionString,
        Action<IdentityOptions>? configureOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<MySqlApplicationDbContext>(options => options.UseMySQL(connectionString));

        return services.AddIdentity<IdentityUser, IdentityRole>(options => configureOptions?.Invoke(options))
            .AddEntityFrameworkStores<MySqlApplicationDbContext>()
            .AddDefaultTokenProviders();
    }
}
