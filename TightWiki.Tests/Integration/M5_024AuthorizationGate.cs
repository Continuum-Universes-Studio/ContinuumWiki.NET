using System.Text.Json;
using MySqlConnector;

namespace TightWiki.Tests.Integration;

internal static class M5_024AuthorizationGate
{
    private const string ManifestVersion = "cw-m5-024-authorization-v1";
    private static readonly string[] RequiredPermissions =
    [
        "Connect",
        "ApplyV3Migration",
        "GrantConcurrencyTests",
        "GrantRevocationTests",
        "GrantRestartTests",
        "RetainSanitizedEvidence"
    ];

    public static bool IsConfigured
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_024_AUTHORIZATION_PATH"));

    public static HostCertificationDecision Evaluate()
    {
        var approvalPath = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_024_AUTHORIZATION_PATH");
        var connectionString = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_024_TEST_CONNECTION");
        var targetDatabase = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_024_TARGET_DATABASE");
        var targetIdentity = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_024_TARGET_IDENTITY");
        if (string.IsNullOrWhiteSpace(approvalPath) || string.IsNullOrWhiteSpace(connectionString)
            || string.IsNullOrWhiteSpace(targetDatabase) || string.IsNullOrWhiteSpace(targetIdentity))
            return new(false, "M5-024 requires an independent manifest, connection, exact database, and target identity.", null);
        if (!string.Equals(Environment.GetEnvironmentVariable("CONTINUUMWIKI_RUN_MYSQL_TESTS"), "1", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_024_AUTHORIZED"), "1", StringComparison.Ordinal))
            return new(false, "M5-024 requires explicit test execution and authorization flags.", null);
        if (!File.Exists(approvalPath))
            return new(false, "The M5-024 authorization manifest is unavailable.", null);

        try
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            using var document = JsonDocument.Parse(File.ReadAllText(approvalPath));
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("ManifestVersion").GetString(), ManifestVersion, StringComparison.Ordinal)
                || root.GetProperty("ExpiresAtUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow)
                return new(false, "The M5-024 authorization manifest is unsupported or expired.", builder.Database);
            var target = root.GetProperty("Target");
            if (!target.GetProperty("Disposable").GetBoolean()
                || !string.Equals(target.GetProperty("DatabaseName").GetString(), targetDatabase, StringComparison.Ordinal)
                || !string.Equals(target.GetProperty("DatabaseIdentity").GetString(), targetIdentity, StringComparison.Ordinal))
                return new(false, "The M5-024 manifest target does not match the requested disposable target.", builder.Database);
            foreach (var permission in RequiredPermissions)
                if (!root.GetProperty("PermittedOperations").GetProperty(permission).GetBoolean())
                    return new(false, $"The M5-024 manifest does not authorize {permission}.", builder.Database);
            if (!string.Equals(builder.Database, targetDatabase, StringComparison.Ordinal)
                || !IsLoopback(builder.Server) || builder.UserID != targetIdentity)
                return new(false, "The M5-024 connection is not loopback, target-bound, or identity-bound.", builder.Database);
            return new(true, "Exact independently authorized disposable M5-024 target.", builder.Database);
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new(false, "The M5-024 authorization manifest is malformed or incomplete.", targetDatabase);
        }
    }

    public static string RequireConnectionString()
    {
        var decision = Evaluate();
        if (!decision.IsAuthorized)
            throw new InvalidOperationException(decision.Reason);
        return Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_024_TEST_CONNECTION")!;
    }

    private static bool IsLoopback(string? host)
        => string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
}

internal sealed class M5CategoryAdministrativeGrantFactAttribute : FactAttribute
{
    public M5CategoryAdministrativeGrantFactAttribute()
    {
        if (!M5_024AuthorizationGate.IsConfigured)
        {
            Skip = "M5-024 authorization is absent; no database connection is permitted.";
            return;
        }
        var decision = M5_024AuthorizationGate.Evaluate();
        if (!decision.IsAuthorized)
            Skip = decision.Reason;
    }
}
