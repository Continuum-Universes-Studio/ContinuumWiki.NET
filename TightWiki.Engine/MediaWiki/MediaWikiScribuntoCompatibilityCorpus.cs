using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TightWiki.Engine.MediaWiki;

public enum MediaWikiScribuntoCompatibilityClassification
{
    SUPPORTED,
    PARTIALLY_SUPPORTED,
    UNSUPPORTED,
    INVALID_INPUT,
    SECURITY_REJECTED
}

public sealed record MediaWikiScribuntoCompatibilityCorpus(
    string CorpusVersion,
    IReadOnlyList<MediaWikiScribuntoCompatibilityCase> Cases)
{
    public const string CurrentVersion = "cw-m3-005b.4-v1";

    public static MediaWikiScribuntoCompatibilityCorpus Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A corpus path is required.", nameof(path));
        var options = JsonOptions();
        var corpus = JsonSerializer.Deserialize<MediaWikiScribuntoCompatibilityCorpus>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("The compatibility corpus was empty.");
        MediaWikiScribuntoCompatibilityCorpusValidator.Validate(corpus);
        return corpus;
    }

    public static JsonSerializerOptions JsonOptions(bool indented = false)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record MediaWikiScribuntoCompatibilityCase(
    string CaseId,
    string ModuleTitle,
    string ModuleSourceIdentity,
    string SourceWikiIdentity,
    string? SourceRevisionId,
    string Source,
    string Function,
    IReadOnlyList<string> PositionalArguments,
    IReadOnlyDictionary<string, string> NamedArguments,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<MediaWikiScribuntoWorkerModuleDependency>? Dependencies,
    IReadOnlyList<string>? RootDependencies,
    string? FrameTitle,
    string? ParentFrameTitle,
    string? ParentFrameSourceRevisionId,
    IReadOnlyList<string>? InvocationChain,
    int MaxSourceCharacters,
    int MaxArgumentCount,
    int MaxArgumentCharacters,
    int MaxIndividualArgumentCharacters,
    int MaxResultCharacters,
    long MaxInstructionCount,
    int MaxDependencyDepth,
    long MaxDependencySourceCharacters,
    int MaxExecutionTimeMilliseconds,
    string? ExpectedText,
    IReadOnlyList<string>? ExpectedDiagnosticCodes,
    IReadOnlyList<MediaWikiScribuntoExpectedDependency>? ExpectedDependencies,
    MediaWikiScribuntoCompatibilityClassification ExpectedClassification,
    IReadOnlyList<string>? GapIds,
    bool RequiresVerification = false,
    bool Skip = false,
    string? Notes = null);

public sealed record MediaWikiScribuntoExpectedDependency(
    string CanonicalModuleTitle,
    string? SourceRevisionId = null,
    string Status = "Executed");

public sealed record MediaWikiScribuntoCompatibilityReport(
    string ReportVersion,
    string CorpusVersion,
    MediaWikiScribuntoCompatibilityReportSummary Summary,
    IReadOnlyList<MediaWikiScribuntoCompatibilityCaseReport> Cases,
    IReadOnlyList<MediaWikiScribuntoApiGap> ApiGapInventory);

public sealed record MediaWikiScribuntoCompatibilityReportSummary(
    int TotalCases,
    int ExecutedCases,
    int SupportedCases,
    int PassedCases,
    int FailedCases,
    int SkippedCases,
    int UnsupportedCases,
    int PartialCases,
    int InvalidInputCases,
    int SecurityRejectedCases,
    int RequiresVerificationCases,
    int ValidationOnlyCases);

public sealed record MediaWikiScribuntoCompatibilityCaseReport(
    string CaseId,
    MediaWikiScribuntoCompatibilityClassification ExpectedClassification,
    MediaWikiScribuntoCompatibilityClassification? ObservedClassification,
    bool Executed,
    bool Passed,
    bool Skipped,
    string ActualStatus,
    string? ActualText,
    IReadOnlyList<string> DiagnosticCodes,
    IReadOnlyList<MediaWikiScribuntoWorkerDependencyReport> Dependencies,
    IReadOnlyList<string> ExpectedDiagnosticCodes,
    string? FailureReason,
    string? Notes);

public sealed record MediaWikiScribuntoApiGap(
    string GapId,
    string Surface,
    MediaWikiScribuntoCompatibilityClassification Classification,
    string CurrentBehavior,
    string DifferenceOrSecurityBoundary,
    string RecommendedPriority,
    int FixtureCount = 0);

public static class MediaWikiScribuntoCompatibilityCorpusValidator
{
    public static void Validate(MediaWikiScribuntoCompatibilityCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        if (!string.Equals(corpus.CorpusVersion, MediaWikiScribuntoCompatibilityCorpus.CurrentVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported compatibility corpus version '{corpus.CorpusVersion}'.");
        if (corpus.Cases is null || corpus.Cases.Count is 0 or > 512)
            throw new InvalidDataException("The compatibility corpus must contain between one and 512 cases.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var testCase in corpus.Cases)
        {
            if (testCase is null || !IsSafeId(testCase.CaseId) || !ids.Add(testCase.CaseId))
                throw new InvalidDataException("The corpus contained a missing, unsafe, or duplicate case identifier.");
            if (string.IsNullOrWhiteSpace(testCase.ModuleTitle) || string.IsNullOrWhiteSpace(testCase.ModuleSourceIdentity)
                || string.IsNullOrWhiteSpace(testCase.SourceWikiIdentity) || string.IsNullOrWhiteSpace(testCase.Source)
                || string.IsNullOrWhiteSpace(testCase.Function))
                throw new InvalidDataException($"Corpus case '{testCase.CaseId}' omitted required invocation data.");
            if (testCase.Skip) continue;
            if (testCase.ExpectedClassification is MediaWikiScribuntoCompatibilityClassification.SUPPORTED or MediaWikiScribuntoCompatibilityClassification.PARTIALLY_SUPPORTED)
            {
                if (testCase.ExpectedText is null && !testCase.RequiresVerification)
                    throw new InvalidDataException($"Corpus case '{testCase.CaseId}' requires expected text or explicit verification status.");
            }
            else if (testCase.ExpectedDiagnosticCodes is null or { Count: 0 })
                throw new InvalidDataException($"Corpus case '{testCase.CaseId}' requires an expected diagnostic.");
            if (testCase.MaxSourceCharacters < 1 || testCase.MaxArgumentCount < 1 || testCase.MaxArgumentCharacters < 1
                || testCase.MaxIndividualArgumentCharacters < 1 || testCase.MaxResultCharacters < 1
                || testCase.MaxInstructionCount < 1 || testCase.MaxDependencyDepth < 1
                || testCase.MaxDependencySourceCharacters < 1 || testCase.MaxExecutionTimeMilliseconds < 1)
                throw new InvalidDataException($"Corpus case '{testCase.CaseId}' contained a non-positive budget.");
        }
    }

    private static bool IsSafeId(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
            && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
}

public sealed class MediaWikiScribuntoCompatibilityRunner
{
    public const string ReportVersion = "cw-m3-005b.4-report-v1";

    public async Task<MediaWikiScribuntoCompatibilityReport> RunAsync(
        MediaWikiScribuntoCompatibilityCorpus corpus,
        string workerAssemblyPath,
        CancellationToken cancellationToken = default)
    {
        MediaWikiScribuntoCompatibilityCorpusValidator.Validate(corpus);
        if (string.IsNullOrWhiteSpace(workerAssemblyPath) || !File.Exists(workerAssemblyPath))
            throw new FileNotFoundException("The offline Scribunto worker assembly was not found.", workerAssemblyPath);

        var caseReports = new List<MediaWikiScribuntoCompatibilityCaseReport>();
        foreach (var testCase in corpus.Cases.OrderBy(value => value.CaseId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (testCase.Skip)
            {
                caseReports.Add(new(testCase.CaseId, testCase.ExpectedClassification, null, false, false, true,
                    "Skipped", null, [], [], testCase.ExpectedDiagnosticCodes ?? [], null, testCase.Notes));
                continue;
            }
            caseReports.Add(await RunCaseAsync(testCase, workerAssemblyPath, cancellationToken));
        }

        var gaps = MediaWikiScribuntoApiGapInventory.CreateDefault()
            .Select(gap => gap with { FixtureCount = corpus.Cases.Count(testCase => testCase.GapIds?.Contains(gap.GapId, StringComparer.Ordinal) == true) })
            .OrderByDescending(gap => gap.FixtureCount)
            .ThenBy(gap => gap.GapId, StringComparer.Ordinal)
            .ToArray();
        return new(
            ReportVersion,
            corpus.CorpusVersion,
            new(
                caseReports.Count,
                caseReports.Count(value => value.Executed),
                caseReports.Count(value => value.ExpectedClassification == MediaWikiScribuntoCompatibilityClassification.SUPPORTED),
                caseReports.Count(value => value.Passed),
                caseReports.Count(value => !value.Passed && !value.Skipped),
                caseReports.Count(value => value.Skipped),
                caseReports.Count(value => value.ExpectedClassification == MediaWikiScribuntoCompatibilityClassification.UNSUPPORTED),
                caseReports.Count(value => value.ExpectedClassification == MediaWikiScribuntoCompatibilityClassification.PARTIALLY_SUPPORTED),
                caseReports.Count(value => value.ExpectedClassification == MediaWikiScribuntoCompatibilityClassification.INVALID_INPUT),
                caseReports.Count(value => value.ExpectedClassification == MediaWikiScribuntoCompatibilityClassification.SECURITY_REJECTED),
                caseReports.Count(value => corpus.Cases.Single(testCase => testCase.CaseId == value.CaseId).RequiresVerification),
                caseReports.Count(value => !value.Executed && value.Passed)),
            caseReports,
            gaps);
    }

    private static async Task<MediaWikiScribuntoCompatibilityCaseReport> RunCaseAsync(
        MediaWikiScribuntoCompatibilityCase testCase,
        string workerAssemblyPath,
        CancellationToken cancellationToken)
    {
        var request = new MediaWikiScribuntoWorkerRequest(
            MediaWikiScribuntoWorkerProtocol.CurrentVersion,
            "corpus-" + testCase.CaseId,
            testCase.ModuleTitle,
            testCase.ModuleSourceIdentity,
            testCase.SourceWikiIdentity,
            testCase.SourceRevisionId,
            testCase.Source,
            testCase.Function,
            testCase.PositionalArguments ?? [],
            testCase.NamedArguments ?? new Dictionary<string, string>(StringComparer.Ordinal),
            new(
                testCase.MaxSourceCharacters,
                testCase.MaxArgumentCount,
                testCase.MaxArgumentCharacters,
                testCase.MaxIndividualArgumentCharacters,
                testCase.MaxResultCharacters,
                testCase.MaxInstructionCount,
                testCase.MaxDependencyDepth,
                testCase.MaxDependencySourceCharacters,
                2_000_000,
                250_000,
                64L * 1024 * 1024,
                testCase.MaxExecutionTimeMilliseconds),
            testCase.Capabilities ?? [],
            testCase.Dependencies ?? [],
            testCase.RootDependencies ?? [],
            testCase.FrameTitle,
            testCase.ParentFrameTitle,
            testCase.ParentFrameSourceRevisionId,
            testCase.InvocationChain ?? []);
        var requestJson = MediaWikiScribuntoWorkerProtocol.SerializeRequest(request);
        if (!MediaWikiScribuntoWorkerProtocol.TryParseRequest(requestJson, 2_000_000, out _, out var validationCode, out var validationMessage))
            return new(testCase.CaseId, testCase.ExpectedClassification, Classify(validationCode), false,
                testCase.ExpectedDiagnosticCodes?.Contains(validationCode, StringComparer.Ordinal) == true,
                false, "ProtocolError", null, [validationCode], [], testCase.ExpectedDiagnosticCodes ?? [], validationMessage, testCase.Notes);

        using var process = Process.Start(CreateStartInfo(workerAssemblyPath))
            ?? throw new InvalidOperationException("The offline Scribunto worker could not be started.");
        try
        {
            await process.StandardInput.WriteLineAsync(requestJson);
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1_000, testCase.MaxExecutionTimeMilliseconds + 2_000)));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            _ = await process.StandardError.ReadToEndAsync(timeout.Token);
            if (!MediaWikiScribuntoWorkerProtocol.TryParseResponse(output.Trim(), 250_000, request.CorrelationId, out var response, out var responseCode, out var responseMessage))
                return new(testCase.CaseId, testCase.ExpectedClassification, null, true, false, false, "ProtocolError", null, [responseCode], [], testCase.ExpectedDiagnosticCodes ?? [], responseMessage, testCase.Notes);
            return Compare(testCase, response!);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new(testCase.CaseId, testCase.ExpectedClassification, null, true, false, false, "Terminated", null,
                ["CorpusRunnerTimeout"], [], testCase.ExpectedDiagnosticCodes ?? [], "The corpus worker exceeded its bounded case timeout.", testCase.Notes);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string workerAssemblyPath)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(workerAssemblyPath);
        return info;
    }

    private static MediaWikiScribuntoCompatibilityCaseReport Compare(
        MediaWikiScribuntoCompatibilityCase testCase,
        MediaWikiScribuntoWorkerResponse response)
    {
        var diagnostics = response.Diagnostics.Select(value => value.Code).ToArray();
        var expectedDiagnostics = testCase.ExpectedDiagnosticCodes ?? [];
        var expectedDependencies = testCase.ExpectedDependencies ?? [];
        var actualDependencies = response.Dependencies ?? [];
        var textMatches = testCase.ExpectedText is null || string.Equals(testCase.ExpectedText, response.Text, StringComparison.Ordinal);
        var diagnosticsMatch = expectedDiagnostics.Count == 0 || expectedDiagnostics.Any(code => diagnostics.Contains(code, StringComparer.Ordinal));
        var dependenciesMatch = expectedDependencies.All(expected => actualDependencies.Any(actual =>
            string.Equals(actual.CanonicalModuleTitle, expected.CanonicalModuleTitle, StringComparison.Ordinal)
            && (expected.SourceRevisionId is null || string.Equals(actual.SourceRevisionId, expected.SourceRevisionId, StringComparison.Ordinal))
            && string.Equals(actual.Status, expected.Status, StringComparison.Ordinal)));
        var passed = textMatches && diagnosticsMatch && dependenciesMatch;
        MediaWikiScribuntoCompatibilityClassification? observed = null;
        if (response.Status == "Succeeded")
            observed = testCase.ExpectedClassification is MediaWikiScribuntoCompatibilityClassification.SUPPORTED or MediaWikiScribuntoCompatibilityClassification.PARTIALLY_SUPPORTED
                ? testCase.ExpectedClassification : MediaWikiScribuntoCompatibilityClassification.SUPPORTED;
        else if (diagnosticsMatch)
            observed = testCase.ExpectedClassification;
        var failure = passed ? null : string.Join("; ", new[]
        {
            textMatches ? null : "result mismatch",
            diagnosticsMatch ? null : "diagnostic mismatch",
            dependenciesMatch ? null : "dependency mismatch"
        }.Where(value => value is not null));
        return new(testCase.CaseId, testCase.ExpectedClassification, observed, true, passed, false,
            response.Status, response.Text, diagnostics, actualDependencies, expectedDiagnostics, failure, testCase.Notes);
    }

    private static MediaWikiScribuntoCompatibilityClassification? Classify(string code)
        => code is "UnsupportedCapability" or "InvalidCapabilities" or "CyclicModuleDependency"
            or "CrossWikiModuleDependency" or "DependencySourceHashMismatch" or "DuplicateModuleIdentity"
            or "DuplicateSourceIdentity" or "DependencySourceLimitExceeded" or "DependencyCountLimitExceeded"
            ? MediaWikiScribuntoCompatibilityClassification.SECURITY_REJECTED
            : MediaWikiScribuntoCompatibilityClassification.INVALID_INPUT;
}

public static class MediaWikiScribuntoApiGapInventory
{
    public static IReadOnlyList<MediaWikiScribuntoApiGap> CreateDefault() =>
    [
        new("frame.args", "frame", MediaWikiScribuntoCompatibilityClassification.SUPPORTED, "Bounded scalar positional/named arguments with nil-versus-empty behavior.", "No live parent argument expansion or host callback access.", "low"),
        new("frame.title-parent", "frame", MediaWikiScribuntoCompatibilityClassification.PARTIALLY_SUPPORTED, "Trusted getTitle/getParent title metadata is available.", "Parent arguments and ambiguous frame relationships are not synthesized.", "medium"),
        new("mw.text", "mw.text", MediaWikiScribuntoCompatibilityClassification.PARTIALLY_SUPPORTED, "Bounded trim, literal split, nowiki, encode, decode, and listToText subset.", "Pattern/context-sensitive and PHP edge semantics remain unverified.", "high"),
        new("mw.ustring", "mw.ustring", MediaWikiScribuntoCompatibilityClassification.PARTIALLY_SUPPORTED, "Unicode scalar len, case, sub, and literal find subset.", "Full Scribunto pattern and grapheme semantics are not implemented.", "high"),
        new("module.require", "module loading", MediaWikiScribuntoCompatibilityClassification.PARTIALLY_SUPPORTED, "Bundle-only explicit-edge require with invocation-local caching.", "No dynamic lookup, package path, or complete Scribunto module semantics.", "high"),
        new("mw.loadData", "module loading", MediaWikiScribuntoCompatibilityClassification.PARTIALLY_SUPPORTED, "Bounded immutable Lua-table subset.", "Complete Scribunto immutable-table and error semantics require differential verification.", "high"),
        new("mw.loadJsonData", "module loading", MediaWikiScribuntoCompatibilityClassification.PARTIALLY_SUPPORTED, "Bounded immutable JSON scalar/table conversion.", "MediaWiki JSON conversion edge cases remain unverified.", "medium"),
        new("frame.host-callbacks", "frame callbacks", MediaWikiScribuntoCompatibilityClassification.UNSUPPORTED, "preprocess, expandTemplate, and callParserFunction are rejected.", "No typed callback protocol exists and no host services are exposed.", "high"),
        new("lua-version-semantics", "Lua language", MediaWikiScribuntoCompatibilityClassification.PARTIALLY_SUPPORTED, "MoonSharp 2.0.0 provides its documented Lua-compatible subset.", "MoonSharp is not Scribunto's Lua 5.1 runtime; differences require differential testing.", "high"),
        new("dynamic-loader-security", "security boundary", MediaWikiScribuntoCompatibilityClassification.SECURITY_REJECTED, "Undeclared/dynamic module references are rejected.", "Filesystem, network, CLR, environment, process, database, and unrestricted package access remain denied.", "critical"),
        new("os-sandbox", "deployment security", MediaWikiScribuntoCompatibilityClassification.SECURITY_REJECTED, "Application executor requires explicit opt-in and bubblewrap.", "Current host cannot certify the private-network namespace; production Lua remains disabled.", "critical")
    ];
}
