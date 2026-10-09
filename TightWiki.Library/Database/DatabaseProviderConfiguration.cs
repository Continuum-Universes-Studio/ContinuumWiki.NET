using Microsoft.Extensions.Configuration;

namespace TightWiki.Library.Database
{
    /// <summary>
    /// Defines the provider-selection boundary for persistence. SQLite is the only
    /// implemented provider in this milestone; other values fail explicitly.
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

            if (provider == MySQL)
            {
                throw new NotSupportedException(
                    "Database:Provider=mysql was requested, but application-wide MySQL runtime selection is not yet safe. "
                    + "The legacy plugin contracts and startup paths still require SQLite for deleted-page storage, search parity, "
                    + "defaults/seed, administrative backup, and remaining repository operations. Configure Database:Provider=sqlite "
                    + "until the coordinated cutover milestone; SQLite is not consulted for this rejected MySQL startup.");
            }

            throw new InvalidOperationException(
                $"Unsupported database provider '{provider}'. Supported provider values are: sqlite. MySQL is reserved for a future migration.");
        }
    }
}
