using Microsoft.Extensions.Configuration;

namespace TightWiki.Library.Database
{
    /// <summary>
    /// Defines the explicit provider-selection boundary for persistence.
    /// </summary>
    public static class DatabaseProviderConfiguration
    {
        public const string SQLite = "sqlite";
        public const string MySQL = "mysql";

        public static string GetProvider(IConfiguration configuration)
            => (configuration["Database:Provider"] ?? SQLite).Trim().ToLowerInvariant();

        public static void ValidateSupportedProvider(IConfiguration configuration)
        {
            var provider = GetProvider(configuration);
            if (provider == SQLite)
            {
                return;
            }

            if (provider == MySQL) return;

            throw new InvalidOperationException(
                $"Unsupported database provider '{provider}'. Supported provider values are: sqlite, mysql.");
        }
    }
}
