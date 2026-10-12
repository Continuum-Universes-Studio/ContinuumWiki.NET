using System.Text.Json;
using MySqlConnector;

namespace TightWiki.Tests.Integration;

/// <summary>
/// The host-certification gate is deliberately stricter than the general
/// MySQL integration-test gate.  It prevents a developer or CI process from
/// turning a production-looking connection string into an authorization test
/// target by accident.
/// </summary>
public sealed class MediaWikiTemplateStylesHostAuthorizationTests
{
    [Fact]
    public void HostCertificationRequiresAnExplicitDisposableTarget()
    {
        var decision = M5HostCertificationGate.Evaluate(
            runMySqlTests: Environment.GetEnvironmentVariable("CONTINUUMWIKI_RUN_MYSQL_TESTS"),
            connectionString: Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION"),
            hostAuthorization: Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_003_HOST_AUTHORIZED"),
            targetDatabase: Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_003_TARGET_DATABASE"),
            targetIdentity: Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_003_TARGET_IDENTITY"),
            approvalPath: Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_003_APPROVAL_PATH"));

        if (decision.IsAuthorized)
        {
            Assert.StartsWith("cw_m5_003_", decision.DatabaseName, StringComparison.Ordinal);
            return;
        }

        Assert.False(decision.IsAuthorized);
        Assert.Contains("CONTINUUMWIKI_M5_003_HOST_AUTHORIZED=1", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void HostCertificationRequiresLoopbackAndSeparateNonRootApplicationIdentity()
    {
        var targetDatabase = "cw_m5_003_certification";
        var targetIdentity = "container=continuumwiki-m5-003b-mysql;database=cw_m5_003_certification;endpoint=127.0.0.1:13310;image=docker.io/library/mysql@sha256:" + new string('a', 64);
        var approvalPath = WriteApprovedScope(targetDatabase, targetIdentity);

        try
        {
        var authorized = M5HostCertificationGate.Evaluate(
            runMySqlTests: "1",
            connectionString: "Server=127.0.0.1;Port=13309;Database=cw_m5_003_certification;User ID=continuumwiki_m5_003_app;Password=not-a-secret;",
            hostAuthorization: "1",
            targetDatabase: targetDatabase,
            targetIdentity: targetIdentity,
            approvalPath: approvalPath);
        Assert.True(authorized.IsAuthorized, authorized.Reason);

        var publicTarget = M5HostCertificationGate.Evaluate(
            runMySqlTests: "1",
            connectionString: "Server=192.0.2.10;Port=13309;Database=cw_m5_003_certification;User ID=cw_m5_app;Password=not-a-secret;",
            hostAuthorization: "1",
            targetDatabase: targetDatabase,
            targetIdentity: targetIdentity,
            approvalPath: approvalPath);
        Assert.False(publicTarget.IsAuthorized);

        var rootApplication = M5HostCertificationGate.Evaluate(
            runMySqlTests: "1",
            connectionString: "Server=127.0.0.1;Port=13309;Database=cw_m5_003_certification;User ID=root;Password=not-a-secret;",
            hostAuthorization: "1",
            targetDatabase: targetDatabase,
            targetIdentity: targetIdentity,
            approvalPath: approvalPath);
        Assert.False(rootApplication.IsAuthorized);

        var nonDedicatedApplication = M5HostCertificationGate.Evaluate(
            runMySqlTests: "1",
            connectionString: "Server=127.0.0.1;Port=13309;Database=cw_m5_003_certification;User ID=other_app;Password=not-a-secret;",
            hostAuthorization: "1",
            targetDatabase: targetDatabase,
            targetIdentity: targetIdentity,
            approvalPath: approvalPath);
        Assert.False(nonDedicatedApplication.IsAuthorized);
        }
        finally
        {
            File.Delete(approvalPath);
        }
    }

    [Fact]
    public void HostCertificationRejectsMissingOrReadOnlyApproval()
    {
        var targetDatabase = "cw_m5_003_certification";
        var targetIdentity = "container=continuumwiki-m5-003b-mysql;database=cw_m5_003_certification;endpoint=127.0.0.1:13310;image=docker.io/library/mysql@sha256:" + new string('b', 64);
        var missing = M5HostCertificationGate.Evaluate(
            "1",
            "Server=127.0.0.1;Port=13310;Database=cw_m5_003_certification;User ID=cw_m5_app;Password=not-a-secret;",
            "1", targetDatabase, targetIdentity, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.False(missing.IsAuthorized);
        Assert.Contains("approval", missing.Reason, StringComparison.OrdinalIgnoreCase);

        var readOnlyPath = Path.Combine(Path.GetTempPath(), $"cw-m5-003b-readonly-{Guid.NewGuid():N}.json");
        File.WriteAllText(readOnlyPath, BuildApprovalJson(targetDatabase, targetIdentity, readOnly: true));
        try
        {
            var readOnly = M5HostCertificationGate.Evaluate(
                "1",
                "Server=127.0.0.1;Port=13310;Database=cw_m5_003_certification;User ID=continuumwiki_m5_003_app;Password=not-a-secret;",
                "1", targetDatabase, targetIdentity, readOnlyPath);
            Assert.False(readOnly.IsAuthorized);
            Assert.Contains("mutation", readOnly.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(readOnlyPath);
        }

        var productionBoundaryPath = Path.Combine(Path.GetTempPath(), $"cw-m5-003b-production-{Guid.NewGuid():N}.json");
        File.WriteAllText(productionBoundaryPath, BuildApprovalJson(targetDatabase, targetIdentity, readOnly: false).Replace(
            "\"ProductionMariaDb\": false", "\"ProductionMariaDb\": true", StringComparison.Ordinal));
        try
        {
            var productionBoundary = M5HostCertificationGate.Evaluate(
                "1",
                "Server=127.0.0.1;Port=13310;Database=cw_m5_003_certification;User ID=continuumwiki_m5_003_app;Password=not-a-secret;",
                "1", targetDatabase, targetIdentity, productionBoundaryPath);
            Assert.False(productionBoundary.IsAuthorized);
            Assert.Contains("prohibited", productionBoundary.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(productionBoundaryPath);
        }
    }

    private static string WriteApprovedScope(string databaseName, string targetIdentity)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cw-m5-003b-approved-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, BuildApprovalJson(databaseName, targetIdentity, readOnly: false));
        return path;
    }

    private static string BuildApprovalJson(string databaseName, string targetIdentity, bool readOnly)
        => $$"""
        {
          "SchemaVersion": "cw-m4-005-operator-approval-v1",
          "ApprovalState": "APPROVED",
          "VerificationState": "UNVERIFIED",
          "Track": "B",
          "RequestedScope": {
            "Purpose": "CW-M5-003B disposable TemplateStyles host certification",
            "ReadOnly": {{readOnly.ToString().ToLowerInvariant()}},
            "DisposableTarget": "{{targetIdentity}}",
            "WorkerIdentity": "not-in-scope",
            "SocketPath": "not-in-scope",
            "MutationScope": {
              "CreateDisposableDatabase": true,
              "CreateSyntheticIdentityRecords": true,
              "CreateSyntheticWikiContent": true,
              "MutateSyntheticPermissions": true,
              "DeleteDisposableDatabase": true,
              "CreateAndRemoveContainer": true,
              "RetainSanitizedEvidence": true
            }
          },
          "Approver": { "Name": "fixture approver", "Role": "test", "RecordedAtUtc": "2026-10-11T00:00:00Z" },
          "Evidence": { "TargetIdentity": "{{targetIdentity}}", "RetentionAndCleanup": "destroy container after test" },
          "AuthorizationBoundaries": {
            "NewMediaWikiApiAccess": false,
            "NewMediaWikiDatabaseAccess": false,
            "NewBackupAccess": false,
            "PrivateCargoRows": false,
            "ProductionMariaDb": false,
            "ProductionLua": false,
            "SystemdOrBubblewrapChanges": false,
            "WorkerDeployment": false,
            "CredentialMaterialIncluded": false
          }
        }
        """;

}

internal static class M5HostCertificationGate
{
    public static HostCertificationDecision Evaluate(
        string? runMySqlTests,
        string? connectionString,
        string? hostAuthorization,
        string? targetDatabase,
        string? targetIdentity,
        string? approvalPath)
        => EvaluateForScope(
            runMySqlTests,
            connectionString,
            hostAuthorization,
            targetDatabase,
            targetIdentity,
            approvalPath,
            "cw_m5_003_",
            "continuumwiki_m5_003_app");

    public static HostCertificationDecision EvaluateForScope(
        string? runMySqlTests,
        string? connectionString,
        string? hostAuthorization,
        string? targetDatabase,
        string? targetIdentity,
        string? approvalPath,
        string requiredDatabasePrefix,
        string requiredApplicationIdentity)
    {
        if (!string.Equals(runMySqlTests, "1", StringComparison.Ordinal)
            || !string.Equals(hostAuthorization, "1", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(connectionString)
            || string.IsNullOrWhiteSpace(targetDatabase)
            || string.IsNullOrWhiteSpace(targetIdentity)
            || string.IsNullOrWhiteSpace(approvalPath))
        {
            return new(false, "Requires CONTINUUMWIKI_RUN_MYSQL_TESTS=1, CONTINUUMWIKI_M5_003_HOST_AUTHORIZED=1, a least-privilege exact-schema connection, an exact disposable target identity, and an approved CW-M4-005 scope file.", null);
        }

        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            return new(false, $"The disposable MySQL connection settings are invalid: {exception.Message}", null);
        }

        var databaseName = builder.Database;
        if (!string.Equals(databaseName, targetDatabase, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(requiredDatabasePrefix)
            || !databaseName.StartsWith(requiredDatabasePrefix, StringComparison.Ordinal))
        {
            return new(false, "The connection must name the exact approved disposable target database.", databaseName);
        }

        if (!IsLoopback(builder.Server) || builder.Port <= 1024)
        {
            return new(false, "The disposable MySQL connection must use loopback and an unprivileged mapped port.", databaseName);
        }

        var approval = ReadApproval(approvalPath, targetDatabase, targetIdentity);
        if (!approval.IsValid)
        {
            return new(false, approval.Reason, databaseName);
        }

        if (!string.Equals(builder.UserID, requiredApplicationIdentity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(builder.UserID, "root", StringComparison.OrdinalIgnoreCase)
            || string.Equals(builder.UserID, "mysql", StringComparison.OrdinalIgnoreCase)
            || string.Equals(builder.UserID, "mariadb", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "The application connection must use the dedicated approved application identity.", databaseName);
        }

        return new(true, "Explicit disposable host target authorized.", databaseName);
    }

    private static ApprovalDecision ReadApproval(string path, string targetDatabase, string targetIdentity)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!string.Equals(root.GetString("ApprovalState"), "APPROVED", StringComparison.Ordinal)
                || !string.Equals(root.GetString("Track"), "B", StringComparison.Ordinal))
            {
                return new(false, "The approval file must be an APPROVED CW-M4-005 Track B record.");
            }

            var scope = root.GetProperty("RequestedScope");
            if (scope.GetProperty("ReadOnly").GetBoolean()
                || !string.Equals(scope.GetString("DisposableTarget"), targetIdentity, StringComparison.Ordinal))
            {
                return new(false, "The approval must authorize mutations for the exact disposable target identity.");
            }

            var mutationScope = scope.GetProperty("MutationScope");
            foreach (var name in new[]
                     {
                         "CreateDisposableDatabase",
                         "CreateSyntheticIdentityRecords",
                         "CreateSyntheticWikiContent",
                         "MutateSyntheticPermissions",
                         "DeleteDisposableDatabase",
                         "CreateAndRemoveContainer",
                         "RetainSanitizedEvidence"
                     })
            {
                if (!mutationScope.GetProperty(name).GetBoolean())
                {
                    return new(false, "The approval must authorize every required disposable mutation and evidence-retention operation.");
                }
            }

            var evidence = root.GetProperty("Evidence");
            if (!string.Equals(evidence.GetString("TargetIdentity"), targetIdentity, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(evidence.GetString("RetentionAndCleanup")))
            {
                return new(false, "The approval must record the exact target identity and cleanup retention.");
            }

            var approver = root.GetProperty("Approver");
            if (string.IsNullOrWhiteSpace(approver.GetString("Name"))
                || string.IsNullOrWhiteSpace(approver.GetString("Role"))
                || string.IsNullOrWhiteSpace(approver.GetString("RecordedAtUtc")))
            {
                return new(false, "The approval must identify the approver and recorded time.");
            }

            var boundaries = root.GetProperty("AuthorizationBoundaries");
            foreach (var name in new[]
                     {
                         "NewMediaWikiApiAccess",
                         "NewMediaWikiDatabaseAccess",
                         "NewBackupAccess",
                         "PrivateCargoRows",
                         "ProductionMariaDb",
                         "ProductionLua",
                         "SystemdOrBubblewrapChanges",
                         "WorkerDeployment",
                         "CredentialMaterialIncluded"
                     })
            {
                if (boundaries.GetProperty(name).GetBoolean())
                {
                    return new(false, "The approval contains a prohibited production or credential boundary.");
                }
            }

            return new(true, "Approval scope is valid.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(false, "The approval file is missing, malformed, or incomplete.");
        }
    }

    private static string? GetString(this JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsLoopback(string? host)
        => string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
}

internal sealed record HostCertificationDecision(bool IsAuthorized, string Reason, string? DatabaseName);
internal sealed record ApprovalDecision(bool IsValid, string Reason);
