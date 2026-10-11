using System.Globalization;
using System.Text.Json;
using TightWiki.Engine.MediaWiki;

var arguments = ParseArguments(args);
var workerPath = Required(arguments, "worker");
var cgroupPath = Required(arguments, "cgroup");
var reportPath = Required(arguments, "report");
var report = await RunCertificationAsync(workerPath, cgroupPath);
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, options));
Console.WriteLine($"MoonSharp host certification: verdict={report.Verdict}, passed={report.Summary.Passed}, failed={report.Summary.Failed}, unavailable={report.Summary.Unavailable}");
return report.Verdict == "VERIFIED" ? 0 : 1;

static async Task<CertificationReport> RunCertificationAsync(string workerPath, string cgroupPath)
{
    var probes = new List<ProbeResult>();
    var executorOptions = new ExperimentalMediaWikiScribuntoExecutorOptions(
        workerPath,
        EnableExperimentalExecution: true,
        RequireBubblewrap: true,
        MaxConcurrentExecutions: 2,
        WorkerOutputCharacters: 100_000,
        WorkerMemoryBytes: 1024L * 1024 * 1024,
        WorkerCpuSeconds: 2,
        WorkerInputCharacters: 2_000_000,
        WorkerCgroupPath: cgroupPath,
        WorkerTaskLimit: 32,
        WorkerErrorOutputCharacters: 16_384);
    using var executor = new ExperimentalMediaWikiScribuntoExecutor(executorOptions);

    probes.Add(await RunProbeAsync(executor, "success-control", "return { main = function() return 'ok' end }", "Succeeded", null));
    probes.Add(await RunProbeAsync(executor, "environment-sanitization", "return { main = function() return tostring(os) .. '|' .. tostring(io) .. '|' .. tostring(debug) end }", "Succeeded", "nil|nil|nil"));
    probes.Add(await RunProbeAsync(executor, "filesystem-api-denial", "return { main = function() return io.open('/etc/passwd') end }", "Failed", null));
    probes.Add(await RunProbeAsync(executor, "network-api-denial", "return { main = function() return require('socket') end }", "Failed", null));
    probes.Add(await RunProbeAsync(executor, "malformed-input-recovery", "return {", "Failed", null));
    probes.Add(await RunProbeAsync(executor, "bounded-result", "return { main = function() return string.rep('x', 1000) end }", "Failed", null, maxResultCharacters: 64));
    probes.Add(await RunProbeAsync(executor, "wall-clock-termination", "return { main = function() while true do end end }", "Failed", null, timeout: TimeSpan.FromMilliseconds(250)));
    probes.Add(await RunProbeAsync(executor, "post-kill-recovery", "return { main = function() return 'recovered' end }", "Succeeded", "recovered"));

    var concurrent = await Task.WhenAll(
        RunProbeAsync(executor, "concurrent-a", "return { main = function() return 'a' end }", "Succeeded", "a"),
        RunProbeAsync(executor, "concurrent-b", "return { main = function() return 'b' end }", "Succeeded", "b"));
    probes.AddRange(concurrent);

    var failed = probes.Where(probe => !probe.Passed).ToArray();
    var unavailable = failed.Count(probe => probe.Diagnostics.Any(code => code is "WorkerCgroupUnavailable" or "ScribuntoIsolationUnavailable" or "WorkerCgroupNotWritable"));
    return new(
        "cw-m3-005b.5-host-sandbox-v1",
        failed.Length == 0 ? "VERIFIED" : unavailable > 0 ? "HOST_CERTIFICATION_UNAVAILABLE" : "BLOCKED",
        new(probes.Count, probes.Count(probe => probe.Passed), failed.Length, unavailable),
        probes);
}

static async Task<ProbeResult> RunProbeAsync(
    ExperimentalMediaWikiScribuntoExecutor executor,
    string name,
    string source,
    string expectedStatus,
    string? expectedText,
    int maxResultCharacters = 100_000,
    TimeSpan? timeout = null)
{
    try
    {
        var module = new MediaWikiModuleTitle("Certification");
        var invocation = new MediaWikiScribuntoInvocation(
            "{{#invoke:Certification|main}}", module, "main", [], new Dictionary<string, string>(),
            "SyntheticCertification", "cert-page", "cert-revision", [], CultureInfo.InvariantCulture,
            TimeZoneInfo.Utc, new(0, 1));
        var page = new MediaWikiScribuntoPageMetadata(
            "SyntheticCertification", "cert-page", "cert-revision", DateTime.UtcNow, "wikitext",
            new MediaWikiSourceWikiIdentity("synthetic-certification"));
        var context = new MediaWikiScribuntoExecutionContext(
            page,
            new(MaxResultCharacters: maxResultCharacters, MaxExecutionTime: timeout ?? TimeSpan.FromMilliseconds(500)),
            0,
            []);
        var host = new CertificationHost(source);
        var result = await executor.ExecuteAsync(invocation, host, context);
        var status = result.Executed ? "Succeeded" : result.Diagnostics.FirstOrDefault()?.Code switch
        {
            "ScribuntoExecutionRejected" => "Rejected",
            _ => "Failed"
        };
        var textMatches = expectedText is null || string.Equals(result.Text, expectedText, StringComparison.Ordinal);
        var statusMatches = string.Equals(status, expectedStatus, StringComparison.Ordinal);
        return new(name, statusMatches && textMatches, status, result.Diagnostics.Select(value => value.Code).ToArray(), result.Text, null);
    }
    catch (Exception exception)
    {
        return new(name, false, "Exception", [], null, exception.GetType().Name);
    }
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            throw new ArgumentException("Arguments must use --name value form.");
        values[args[index][2..]] = args[++index];
    }
    return values;
}

static string Required(IReadOnlyDictionary<string, string> arguments, string name)
    => arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"--{name} is required.");

sealed class CertificationHost(string source) : IMediaWikiScribuntoExecutionHost
{
    public Task<string?> ResolveModuleSourceAsync(MediaWikiModuleTitle module, DateTime? asOfUtc, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(source);

    public Task<MediaWikiScribuntoModuleBundle?> ResolveModuleBundleAsync(MediaWikiModuleTitle module, DateTime? asOfUtc, CancellationToken cancellationToken = default)
        => Task.FromResult<MediaWikiScribuntoModuleBundle?>(new(
            MediaWikiScribuntoModuleSource.Create(module, new MediaWikiSourceWikiIdentity("synthetic-certification"), "cert-page", "cert-revision", "Scribunto", source), []));

    public Task<string?> ResolveTemplateTextAsync(string templateTitle, DateTime? asOfUtc, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<MediaWikiScribuntoPageMetadata?> ResolvePageMetadataAsync(string title, DateTime? asOfUtc, CancellationToken cancellationToken = default)
        => Task.FromResult<MediaWikiScribuntoPageMetadata?>(null);

    public void RegisterDependency(MediaWikiScribuntoDependency dependency) { }
}

record CertificationReport(string ReportVersion, string Verdict, CertificationSummary Summary, IReadOnlyList<ProbeResult> Probes);
record CertificationSummary(int Total, int Passed, int Failed, int Unavailable);
record ProbeResult(string Name, bool Passed, string Status, IReadOnlyList<string> Diagnostics, string? Text, string? Error);
