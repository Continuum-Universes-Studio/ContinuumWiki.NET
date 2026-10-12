using System.Text.Json;
using MySqlConnector;

namespace TightWiki.Tests.Integration;

internal static class M5_019AuthorizationGate
{
    private const string ManifestVersion = "cw-m5-019-authorization-v2";
    private const string DatabaseName = "cw_m5_019_operator4";
    private const string ApplicationIdentity = "continuumwiki_m5_019_app";
    private const string TargetIdentity = "container=continuumwiki-m5-019-operator4;database=cw_m5_019_operator4;endpoint=127.0.0.1:13321;volume=continuumwiki-m5-019-operator4-data;image=docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15";
    private static readonly string[] RequiredPermissions =
    [
        "Connect",
        "ApplyV1Migration",
        "ApplyV2Migration",
        "CategoryMutations",
        "ConcurrencyAndCrashTests",
        "RetainSanitizedEvidence"
    ];

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_019_APPROVAL_PATH"));

    public static bool DatabaseCleanupAuthorized
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_019_APPROVAL_PATH");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("PermittedOperations").GetProperty("DatabaseCleanup").GetBoolean();
        }
    }

    public static HostCertificationDecision Evaluate()
    {
        if (!IsConfigured)
            return new(false, "M5-019 authorization inputs are not configured.", null);

        if (!string.Equals(Environment.GetEnvironmentVariable("CONTINUUMWIKI_RUN_MYSQL_TESTS"), "1", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_019_AUTHORIZED"), "1", StringComparison.Ordinal))
            return new(false, "M5-019 requires explicit test execution and authorization flags.", null);

        var connectionString = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION");
        var targetDatabase = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_019_TARGET_DATABASE");
        var targetIdentity = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_019_TARGET_IDENTITY");
        var approvalPath = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_019_APPROVAL_PATH");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(targetDatabase)
            || string.IsNullOrWhiteSpace(targetIdentity) || string.IsNullOrWhiteSpace(approvalPath))
            return new(false, "M5-019 requires a connection, exact target, exact identity, and v2 manifest path.", null);

        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return new(false, "The M5-019 connection settings are invalid.", null);
        }

        if (!string.Equals(builder.Database, DatabaseName, StringComparison.Ordinal)
            || !string.Equals(builder.Database, targetDatabase, StringComparison.Ordinal)
            || !string.Equals(targetIdentity, TargetIdentity, StringComparison.Ordinal)
            || !IsLoopback(builder.Server) || builder.Port != 13321
            || !string.Equals(builder.UserID, ApplicationIdentity, StringComparison.Ordinal))
            return new(false, "The connection does not target the exact approved operator4 database, endpoint, identity, or application user.", builder.Database);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(approvalPath));
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("ManifestVersion").GetString(), ManifestVersion, StringComparison.Ordinal)
                || !string.Equals(root.GetProperty("ApprovalId").GetString(), "CW-M5-019-OPERATOR4-V2-20261011-001", StringComparison.Ordinal)
                || DateTimeOffset.Parse(root.GetProperty("ExpiresAtUtc").GetString()!) <= DateTimeOffset.UtcNow)
                return new(false, "The M5-019 manifest is unsupported, wrong-target, or expired.", builder.Database);

            var target = root.GetProperty("Target");
            if (!target.GetProperty("Disposable").GetBoolean()
                || !string.Equals(target.GetProperty("DatabaseName").GetString(), DatabaseName, StringComparison.Ordinal)
                || !string.Equals(target.GetProperty("Endpoint").GetString(), "127.0.0.1:13321", StringComparison.Ordinal)
                || !string.Equals(target.GetProperty("DedicatedApplicationIdentity").GetString(), ApplicationIdentity, StringComparison.Ordinal)
                || !string.Equals(target.GetProperty("InstanceIdentity").GetString(), TargetIdentity, StringComparison.Ordinal))
                return new(false, "The M5-019 manifest target identity is not the approved operator4 target.", builder.Database);

            var permissions = root.GetProperty("PermittedOperations");
            foreach (var permission in RequiredPermissions)
                if (!permissions.GetProperty(permission).GetBoolean())
                    return new(false, $"The M5-019 manifest does not authorize {permission}.", builder.Database);

            return new(true, "Exact disposable M5-019 operator4 target authorized.", builder.Database);
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new(false, "The M5-019 manifest is missing, malformed, or incomplete.", builder.Database);
        }
    }

    public static string RequireApprovedConnectionString()
    {
        var decision = Evaluate();
        if (!decision.IsAuthorized)
            throw new InvalidOperationException(decision.Reason);
        return Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION")!;
    }

    public static string RequireTargetDatabase()
    {
        var decision = Evaluate();
        if (!decision.IsAuthorized)
            throw new InvalidOperationException(decision.Reason);
        return DatabaseName;
    }

    private static bool IsLoopback(string? host)
        => string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
}
