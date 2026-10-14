using System.Text.Json;
using MySqlConnector;

namespace TightWiki.Tests.Integration;

/// <summary>
/// M5-025 has an independent authorization namespace. This gate performs only
/// manifest and connection-string identity checks; it opens no database
/// connection. The test body is the first operation allowed to connect.
/// </summary>
internal static class M5_025AuthorizationGate
{
    private const string ManifestVersion = "cw-m5-025-authorization-v1";
    private static readonly string[] RequiredTruePermissions =
    [
        "Connect", "VerifyTargetIsolation", "ApplyV1Prerequisites", "ApplyV2Prerequisites",
        "ApplyV3Migration", "GrantIssueMutations", "GrantConsumeMutations",
        "GrantRevocationMutations", "GrantAuditMutations", "ConcurrentConnectionTests",
        "RestartRecoveryTests", "AcknowledgmentLossTests", "RetainSanitizedEvidence"
    ];
    private static readonly string[] RequiredFalsePermissions = ["DatabaseCleanup", "SchemaDeletion", "ContainerVolumeDeletion"];

    public static bool IsConfigured
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_025_AUTHORIZATION_PATH"));

    public static HostCertificationDecision Evaluate()
    {
        var approvalPath = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_025_AUTHORIZATION_PATH");
        var connectionString = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_025_TEST_CONNECTION");
        var targetContainer = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_025_TARGET_CONTAINER");
        var targetVolume = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_025_TARGET_VOLUME");
        var targetDatabase = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_025_TARGET_DATABASE");
        var targetEndpoint = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_025_TARGET_ENDPOINT");
        var targetIdentity = Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_025_TARGET_IDENTITY");
        if (string.IsNullOrWhiteSpace(approvalPath) || string.IsNullOrWhiteSpace(connectionString)
            || string.IsNullOrWhiteSpace(targetContainer) || string.IsNullOrWhiteSpace(targetVolume)
            || string.IsNullOrWhiteSpace(targetDatabase) || string.IsNullOrWhiteSpace(targetEndpoint)
            || string.IsNullOrWhiteSpace(targetIdentity))
            return new(false, "M5-025 requires an independent manifest, exact target identity, and test connection.", null);
        if (!string.Equals(Environment.GetEnvironmentVariable("CONTINUUMWIKI_RUN_MYSQL_TESTS"), "1", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_025_AUTHORIZED"), "1", StringComparison.Ordinal))
            return new(false, "M5-025 requires explicit test execution and authorization flags.", null);
        if (!File.Exists(approvalPath))
            return new(false, "The M5-025 authorization manifest is unavailable.", null);

        try
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            using var document = JsonDocument.Parse(File.ReadAllText(approvalPath));
            var root = document.RootElement;
            var approvedAt = root.GetProperty("ApprovedAtUtc").GetDateTimeOffset();
            var expiresAt = root.GetProperty("ExpiresAtUtc").GetDateTimeOffset();
            if (!string.Equals(root.GetProperty("ManifestVersion").GetString(), ManifestVersion, StringComparison.Ordinal)
                || approvedAt > DateTimeOffset.UtcNow
                || expiresAt <= DateTimeOffset.UtcNow)
                return new(false, "The M5-025 authorization manifest is unsupported or expired.", builder.Database);
            var target = root.GetProperty("Target");
            if (!target.GetProperty("Disposable").GetBoolean()
                || target.GetProperty("ContainerName").GetString() != targetContainer
                || target.GetProperty("VolumeName").GetString() != targetVolume
                || target.GetProperty("DatabaseName").GetString() != targetDatabase
                || target.GetProperty("Endpoint").GetString() != targetEndpoint
                || target.GetProperty("InstanceIdentity").GetString() != targetIdentity
                || target.GetProperty("DatabaseIdentity").GetString() != builder.UserID
                || target.GetProperty("EngineVersion").GetString() != "MariaDB 11.8.x")
                return new(false, "The M5-025 manifest and requested target identity do not match.", builder.Database);
            var permissions = root.GetProperty("PermittedOperations");
            foreach (var permission in RequiredTruePermissions)
                if (!permissions.TryGetProperty(permission, out var value) || value.ValueKind != JsonValueKind.True)
                    return new(false, $"The M5-025 manifest does not authorize {permission}.", builder.Database);
            foreach (var permission in RequiredFalsePermissions)
                if (!permissions.TryGetProperty(permission, out var value) || value.ValueKind != JsonValueKind.False)
                    return new(false, $"The M5-025 manifest must explicitly prohibit {permission}.", builder.Database);
            if (!IsLoopback(builder.Server) || builder.Port != ParseEndpointPort(targetEndpoint)
                || !string.Equals(builder.Database, targetDatabase, StringComparison.Ordinal))
                return new(false, "The M5-025 connection is not the exact approved loopback target.", builder.Database);
            return new(true, "Exact independently authorized disposable M5-025 target.", builder.Database);
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new(false, "The M5-025 authorization manifest is malformed or incomplete.", targetDatabase);
        }
    }

    public static string RequireConnectionString()
    {
        var decision = Evaluate();
        if (!decision.IsAuthorized)
            throw new InvalidOperationException(decision.Reason);
        return Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_025_TEST_CONNECTION")!;
    }

    private static int ParseEndpointPort(string endpoint)
        => int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsLoopback(string? host)
        => string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
}

internal sealed class M5CategoryAdministrativeGrantMariaDbFactAttribute : FactAttribute
{
    public M5CategoryAdministrativeGrantMariaDbFactAttribute()
    {
        if (!M5_025AuthorizationGate.IsConfigured)
        {
            Skip = "M5-025 authorization is absent; no database connection is permitted.";
            return;
        }
        var decision = M5_025AuthorizationGate.Evaluate();
        if (!decision.IsAuthorized)
            Skip = decision.Reason;
    }
}
