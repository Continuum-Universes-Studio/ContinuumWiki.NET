using System.Diagnostics;
using System.Text.Json;

namespace TightWiki.Engine.MediaWiki;

public static class MediaWikiScribuntoHostAuthorization
{
    public static bool IsAuthorized(MediaWikiScribuntoExperimentalConfiguration configuration)
    {
        if (!configuration.Enabled || !configuration.UsesApprovedSocketTransport || string.IsNullOrWhiteSpace(configuration.HostCertificationPath)) return false;
        try
        {
            var endpoint = new FileInfo(configuration.SocketPath!);
            if (!endpoint.Exists || (endpoint.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (!OperatingSystem.IsLinux()) return false;
            var mode = File.GetUnixFileMode(configuration.SocketPath!);
            if ((mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) return false;
            var parentMode = File.GetUnixFileMode(Path.GetDirectoryName(configuration.SocketPath!)!);
            if ((parentMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return false;
            using var report = JsonDocument.Parse(File.ReadAllText(configuration.HostCertificationPath));
            var root = report.RootElement;
            if (!string.Equals(root.GetProperty("reportVersion").GetString(), "cw-m3-005b.6c-host-sandbox-v1", StringComparison.Ordinal)
                || !string.Equals(root.GetProperty("verdict").GetString(), "VERIFIED", StringComparison.Ordinal)
                || !string.Equals(root.GetProperty("socketPath").GetString(), configuration.SocketPath, StringComparison.Ordinal)) return false;
            var summary = root.GetProperty("summary");
            if (summary.GetProperty("failed").GetInt32() != 0 || summary.GetProperty("unavailable").GetInt32() != 0) return false;
            foreach (var check in root.GetProperty("checks").EnumerateArray())
                if (check.GetProperty("required").GetBoolean() && !string.Equals(check.GetProperty("status").GetString(), "PASS", StringComparison.Ordinal)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>Executes only through an already-authorized local worker socket.</summary>
public sealed class MediaWikiScribuntoSocketExecutor : IMediaWikiScribuntoExecutor
{
    private readonly MediaWikiScribuntoUnixSocketClient _client;
    public MediaWikiScribuntoSocketExecutor(MediaWikiScribuntoUnixSocketClient client) => _client = client;

    public async Task<MediaWikiScribuntoExecutionResult> ExecuteAsync(MediaWikiScribuntoInvocation invocation, IMediaWikiScribuntoExecutionHost host, MediaWikiScribuntoExecutionContext context, CancellationToken cancellationToken = default)
    {
        var bundle = await host.ResolveModuleBundleAsync(invocation.Module, context.Page.RevisionTimestampUtc, cancellationToken);
        if (bundle is null) return Failure(invocation, "ScribuntoModuleMissing", "The requested module was not available.");
        var correlationId = Guid.NewGuid().ToString("N");
        var request = new MediaWikiScribuntoWorkerRequest(MediaWikiScribuntoWorkerProtocol.CurrentVersion, correlationId,
            invocation.Module.CanonicalTitle, $"{bundle.Root.SourcePageId}@{bundle.Root.SourceRevisionId}", bundle.Root.SourceWiki.Value,
            bundle.Root.SourceRevisionId, bundle.Root.Source, invocation.ExportedFunction, invocation.PositionalArguments, invocation.NamedArguments,
            new(context.Budget.MaxModuleSourceCharacters, context.Budget.MaxArgumentCount, context.Budget.MaxArgumentCharacters,
                context.Budget.MaxIndividualArgumentCharacters, Math.Min(context.Budget.MaxResultCharacters, 200_000), context.Budget.MaxInstructionCount,
                context.Budget.MaxModuleDependencyDepth, context.Budget.MaxDependencySourceCharacters, Math.Min(context.Budget.MaxModuleSourceCharacters, 2_000_000), 100_000,
                context.Budget.MaxMemoryBytes, Math.Max(1, (int)Math.Min(int.MaxValue, context.Budget.EffectiveMaxExecutionTime.TotalMilliseconds))),
            MediaWikiScribuntoWorkerProtocol.DefaultCapabilities,
            bundle.Dependencies.Select(d => new MediaWikiScribuntoWorkerModuleDependency(d.SourceWiki.Value, d.Module.CanonicalTitle, d.SourcePageId, d.SourceRevisionId, d.ContentModel, d.SourceSha256, d.Source, d.Dependencies.Select(x => x.CanonicalTitle).ToArray())).ToArray(),
            bundle.Root.Dependencies.Select(x => x.CanonicalTitle).ToArray(), invocation.CallingPageTitle,
            invocation.TemplateInvocationChain.LastOrDefault(), invocation.TemplateInvocationChain.Count == 0 ? null : context.Page.SourceRevisionId, invocation.TemplateInvocationChain);
        try
        {
            var responseJson = await _client.SendAsync(MediaWikiScribuntoWorkerProtocol.SerializeRequest(request), cancellationToken);
            if (!MediaWikiScribuntoWorkerProtocol.TryParseResponse(responseJson, 100_000, correlationId, out var response, out _, out var message)) return Failure(invocation, "ScribuntoWorkerProtocolError", message);
            var diagnostics = response!.Diagnostics.Select(d => new MediaWikiCompatibilityDiagnostic(d.Code, d.Message, invocation.Location.Offset, invocation.Location.Length, invocation.Location.Line, invocation.Location.Column, d.Severity == "Information" ? MediaWikiDiagnosticSeverity.Information : d.Severity == "Warning" ? MediaWikiDiagnosticSeverity.Warning : MediaWikiDiagnosticSeverity.Error)).ToArray();
            if (response.Status != "Succeeded") return new(null, diagnostics, [new("module", invocation.Module.CanonicalTitle, bundle.Root.SourceRevisionId, "ExecutionFailed")]);
            if ((response.Text ?? string.Empty).Length > context.Budget.MaxResultCharacters) return Failure(invocation, "ScribuntoResultBudgetExceeded", "The worker result exceeded the configured limit.");
            var dependencies = (response.Dependencies ?? []).Select(d => new MediaWikiScribuntoDependency("module", d.CanonicalModuleTitle, d.SourceRevisionId, d.Status, d.SourceWikiIdentity, d.SourcePageId, d.SourceSha256)).ToArray();
            foreach (var dependency in dependencies) host.RegisterDependency(dependency);
            return new(response.Text ?? string.Empty, diagnostics, dependencies.Length == 0 ? [new("module", invocation.Module.CanonicalTitle, bundle.Root.SourceRevisionId, "Executed")] : dependencies, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Failure(invocation, "ScribuntoWorkerUnavailable", "The experimental Lua worker was unavailable."); }
    }

    private static MediaWikiScribuntoExecutionResult Failure(MediaWikiScribuntoInvocation i, string code, string message)
        => new(null, [new(code, message, i.Location.Offset, i.Location.Length, i.Location.Line, i.Location.Column, MediaWikiDiagnosticSeverity.Error)], [new("module", i.Module.CanonicalTitle, i.CallingSourceRevisionId, "ExecutionFailed")]);
}
