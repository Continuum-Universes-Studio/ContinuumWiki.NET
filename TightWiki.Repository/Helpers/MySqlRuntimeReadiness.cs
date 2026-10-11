using Dapper;
using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>Fail-closed runtime checks for the supported MariaDB installation.</summary>
public static class MySqlRuntimeReadiness
{
    public static async Task ValidateServerAsync(IDatabaseConnectionFactory factory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var session = await factory.OpenSessionAsync(cancellationToken);
        var version = await session.Connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT VERSION();", cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(version) || !version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported database server '{version}'. ContinuumWiki.NET requires MariaDB 11.8.");
        var majorMinor = version.Split('-', 2)[0];
        if (!majorMinor.StartsWith("11.8.", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported MariaDB version '{version}'. ContinuumWiki.NET requires MariaDB 11.8.");
    }

    public static async Task ValidateInstallationAsync(IDatabaseConnectionFactory factory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var session = await factory.OpenSessionAsync(cancellationToken);
        var requirements = new (string Name, string Sql)[]
        {
            ("configuration", "SELECT COUNT(*) FROM ConfigurationEntry;"),
            ("translations", "SELECT COUNT(*) FROM Translation;"),
            ("themes", "SELECT COUNT(*) FROM Theme;"),
            ("menus", "SELECT COUNT(*) FROM MenuItem;"),
            ("feature templates", "SELECT COUNT(*) FROM DefaultFeatureTemplates;"),
            ("Identity users", "SELECT COUNT(*) FROM AspNetUsers;"),
            ("Identity roles", "SELECT COUNT(*) FROM Role;")
        };
        foreach (var requirement in requirements)
        {
            var count = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(requirement.Sql, cancellationToken: cancellationToken));
            if (count == 0)
                throw new InvalidOperationException($"MariaDB installation is incomplete: required {requirement.Name} data is missing.");
        }

        var administrators = await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM AccountRole AR INNER JOIN Role R ON R.Id = AR.RoleId WHERE R.Name = 'Administrator';", cancellationToken: cancellationToken));
        if (administrators == 0)
            throw new InvalidOperationException("MariaDB installation is incomplete: no Administrator account is configured.");
    }
}
