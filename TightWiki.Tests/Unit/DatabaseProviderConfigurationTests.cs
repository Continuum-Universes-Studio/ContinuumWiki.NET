using Microsoft.Extensions.Configuration;
using TightWiki.Library.Database;

namespace TightWiki.Tests.Unit
{
    public class DatabaseProviderConfigurationTests
    {
        private static IConfiguration Configuration(string? provider = null)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(provider is null
                    ? []
                    : new Dictionary<string, string?> { ["Database:Provider"] = provider })
                .Build();

        [Fact]
        public void MissingProviderUsesSQLite()
        {
            var configuration = Configuration();

            Assert.Equal(DatabaseProviderConfiguration.SQLite, DatabaseProviderConfiguration.GetProvider(configuration));
            DatabaseProviderConfiguration.ValidateSupportedProvider(configuration);
        }

        [Fact]
        public void ProviderNamesAreCaseInsensitive()
        {
            var configuration = Configuration(" SQLITE ");

            DatabaseProviderConfiguration.ValidateSupportedProvider(configuration);
            Assert.Equal(DatabaseProviderConfiguration.SQLite, DatabaseProviderConfiguration.GetProvider(configuration));
        }

        [Fact]
        public void MySQLIsRejectedUntilImplemented()
        {
            var exception = Assert.Throws<NotSupportedException>(
                () => DatabaseProviderConfiguration.ValidateSupportedProvider(Configuration("mysql")));

            Assert.Contains("application-wide MySQL runtime selection", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SQLite is not consulted", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MySQLRejectionHappensBeforeSQLiteDatabaseConstruction()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "mysql",
                    ["ConnectionStrings:DatabasePath"] = Path.Combine(Path.GetTempPath(), $"continuumwiki-provider-test-{Guid.NewGuid():N}")
                })
                .Build();

            Assert.Throws<NotSupportedException>(() => new TightWiki.Repository.Helpers.DatabaseManager(configuration));
        }

        [Fact]
        public void UnknownProviderIsRejectedInsteadOfFallingBack()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => DatabaseProviderConfiguration.ValidateSupportedProvider(Configuration("postgres")));

            Assert.Contains("Unsupported database provider", exception.Message);
        }
    }
}
