using System.Text.Json;
using TightWiki.Engine.MediaWiki;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiScribuntoCompatibilityCorpusTests
{
    private static string CorpusPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures/scribunto-compatibility-corpus-v1.json"));

    private static string WorkerPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../ScribuntoWorker/bin/Debug/net10.0/ContinuumWiki.ScribuntoWorker.dll"));

    [Fact]
    public async Task CorpusRunnerProducesDeterministicPassingReportAndGapInventory()
    {
        var corpus = MediaWikiScribuntoCompatibilityCorpus.Load(CorpusPath);
        var report = await new MediaWikiScribuntoCompatibilityRunner().RunAsync(corpus, WorkerPath);

        Assert.Equal(MediaWikiScribuntoCompatibilityCorpus.CurrentVersion, report.CorpusVersion);
        Assert.Equal(20, report.Summary.TotalCases);
        Assert.Equal(18, report.Summary.ExecutedCases);
        Assert.Equal(7, report.Summary.SupportedCases);
        Assert.Equal(2, report.Summary.ValidationOnlyCases);
        Assert.Equal(20, report.Summary.PassedCases);
        Assert.Equal(0, report.Summary.FailedCases);
        Assert.Equal(1, report.Summary.UnsupportedCases);
        Assert.Equal(5, report.Summary.PartialCases);
        Assert.All(report.Cases, testCase => Assert.True(testCase.Passed, testCase.FailureReason));
        Assert.Equal(
            ["LuaSyntaxError"],
            report.Cases.Single(testCase => testCase.CaseId == "lua-version-difference").DiagnosticCodes);
        Assert.Equal(
            1,
            report.ApiGapInventory.Single(gap => gap.GapId == "lua-version-semantics").FixtureCount);
        Assert.Equal(
            "dynamic-loader-security",
            report.ApiGapInventory.First(gap => gap.FixtureCount > 0 && gap.RecommendedPriority == "critical").GapId);

        var first = JsonSerializer.Serialize(report, MediaWikiScribuntoCompatibilityCorpus.JsonOptions(indented: true));
        var second = JsonSerializer.Serialize(report, MediaWikiScribuntoCompatibilityCorpus.JsonOptions(indented: true));
        Assert.Equal(first, second);
    }

    [Fact]
    public void CorpusValidatorRejectsUnsupportedVersionsAndDuplicateIds()
    {
        var corpus = MediaWikiScribuntoCompatibilityCorpus.Load(CorpusPath);

        Assert.Throws<InvalidDataException>(() =>
            MediaWikiScribuntoCompatibilityCorpusValidator.Validate(corpus with { CorpusVersion = "future" }));
        Assert.Throws<InvalidDataException>(() =>
            MediaWikiScribuntoCompatibilityCorpusValidator.Validate(corpus with
            {
                Cases = [corpus.Cases[0], corpus.Cases[0]]
            }));
    }

    [Fact]
    public async Task CorpusRunnerDetectsIncorrectExpectedTextAndInvalidDependencyHash()
    {
        var corpus = MediaWikiScribuntoCompatibilityCorpus.Load(CorpusPath);
        var runner = new MediaWikiScribuntoCompatibilityRunner();

        var wrongText = corpus with
        {
            Cases = corpus.Cases.Select(testCase => testCase.CaseId == "basic-function"
                ? testCase with { ExpectedText = "wrong" }
                : testCase).ToArray()
        };
        var wrongTextReport = await runner.RunAsync(wrongText, WorkerPath);
        Assert.Equal(1, wrongTextReport.Summary.FailedCases);
        Assert.Contains("result mismatch", wrongTextReport.Cases.Single(testCase => testCase.CaseId == "basic-function").FailureReason);

        var dependencyCase = corpus.Cases.Single(testCase => testCase.CaseId == "nested-require");
        var invalidHashCase = dependencyCase with
        {
            Dependencies = dependencyCase.Dependencies!.Select(dependency =>
                dependency with { SourceSha256 = new string('0', 64) }).ToArray()
        };
        var invalidHashCorpus = corpus with
        {
            Cases = corpus.Cases.Select(testCase => testCase.CaseId == "nested-require"
                ? invalidHashCase
                : testCase).ToArray()
        };
        var invalidHashReport = await runner.RunAsync(invalidHashCorpus, WorkerPath);
        Assert.Equal(1, invalidHashReport.Summary.FailedCases);
        Assert.Contains("DependencySourceHashMismatch", invalidHashReport.Cases.Single(testCase => testCase.CaseId == "nested-require").DiagnosticCodes);
    }
}
