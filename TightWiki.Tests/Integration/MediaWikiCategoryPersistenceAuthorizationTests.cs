using System.Text.RegularExpressions;

namespace TightWiki.Tests.Integration;

/// <summary>
/// Gate-only test for the future category persistence slice. It never opens a
/// connection; database tests remain separately gated until a provider is
/// implemented and an approved disposable target exists.
/// </summary>
public sealed class CategoryPersistenceAuthorizationFactAttribute : FactAttribute
{
    public CategoryPersistenceAuthorizationFactAttribute()
    {
        if (M5_019AuthorizationGate.IsConfigured)
        {
            var decision = M5_019AuthorizationGate.Evaluate();
            if (!decision.IsAuthorized)
                Skip = decision.Reason;
            return;
        }

        var missing = new List<string>();
        if (!string.Equals(Environment.GetEnvironmentVariable("CONTINUUMWIKI_RUN_MYSQL_TESTS"), "1", StringComparison.Ordinal))
            missing.Add("CONTINUUMWIKI_RUN_MYSQL_TESTS=1");
        if (!string.Equals(Environment.GetEnvironmentVariable("CONTINUUMWIKI_CATEGORY_PERSISTENCE_AUTHORIZED"), "1", StringComparison.Ordinal))
            missing.Add("CONTINUUMWIKI_CATEGORY_PERSISTENCE_AUTHORIZED=1");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION")))
            missing.Add("CONTINUUMWIKI_MYSQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTINUUMWIKI_CATEGORY_PERSISTENCE_APPROVAL_PATH")))
            missing.Add("CONTINUUMWIKI_CATEGORY_PERSISTENCE_APPROVAL_PATH");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTINUUMWIKI_CATEGORY_PERSISTENCE_TARGET_DATABASE")))
            missing.Add("CONTINUUMWIKI_CATEGORY_PERSISTENCE_TARGET_DATABASE");

        if (missing.Count > 0)
            Skip = "AUTHORIZATION_NOT_ESTABLISHED: missing explicit disposable category authorization inputs: " + string.Join(", ", missing);
    }
}

public sealed class MediaWikiCategoryPersistenceAuthorizationTests
{
    [CategoryPersistenceAuthorizationFact]
    public void ExplicitAuthorizationGateIdentifiesOnlyTheApprovedDisposableTarget()
    {
        var approvalPath = M5_019AuthorizationGate.IsConfigured
            ? Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_019_APPROVAL_PATH")
            : Environment.GetEnvironmentVariable("CONTINUUMWIKI_CATEGORY_PERSISTENCE_APPROVAL_PATH");
        var targetDatabase = M5_019AuthorizationGate.IsConfigured
            ? Environment.GetEnvironmentVariable("CONTINUUMWIKI_M5_019_TARGET_DATABASE")
            : Environment.GetEnvironmentVariable("CONTINUUMWIKI_CATEGORY_PERSISTENCE_TARGET_DATABASE");

        Assert.False(string.IsNullOrWhiteSpace(approvalPath));
        Assert.True(File.Exists(approvalPath));
        if (M5_019AuthorizationGate.IsConfigured)
        {
            Assert.Equal("cw_m5_019_operator4", targetDatabase);
            return;
        }

        Assert.Matches(new Regex(@"^cw_m5_014_[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant), targetDatabase ?? string.Empty);
    }
}
