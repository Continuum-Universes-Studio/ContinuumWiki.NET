using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

/// <summary>Stable identity of the offline MediaWiki source used by a render.</summary>
public sealed record MediaWikiSourceWikiIdentity(
    string Value,
    string? SiteName = null,
    string? BaseUrl = null,
    string? Generator = null);

/// <summary>Canonical module title. Module aliases are normalized before lookup.</summary>
public sealed record MediaWikiModuleTitle(string Name, int NamespaceId = 828, string NamespaceName = "Module")
{
    public string CanonicalTitle => NamespaceId == 828 ? $"{NamespaceName}:{Name}" : $"{NamespaceName}:{Name}";

    public static bool TryParse(string value, out MediaWikiModuleTitle title)
    {
        var normalized = value.Trim().Replace('_', ' ');
        var separator = normalized.IndexOf(':');
        if (separator > 0)
        {
            var prefix = normalized[..separator].Trim();
            if (!string.Equals(prefix, "Module", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(prefix, "Module talk", StringComparison.OrdinalIgnoreCase))
            {
                title = null!;
                return false;
            }
            var namespaceName = prefix.Equals("Module talk", StringComparison.OrdinalIgnoreCase) ? "Module talk" : "Module";
            normalized = normalized[(separator + 1)..].Trim();
            if (normalized.Length == 0 || HasUnsafeModulePath(normalized)) { title = null!; return false; }
            title = new(normalized, namespaceName.Equals("Module talk", StringComparison.OrdinalIgnoreCase) ? 829 : 828, namespaceName);
            return true;
        }
        if (normalized.Length == 0 || HasUnsafeModulePath(normalized))
        {
            title = null!;
            return false;
        }
        title = new(normalized);
        return true;
    }

    private static bool HasUnsafeModulePath(string value)
        => value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Split(':').Any(part => part is "." or "..")
            || value.Any(char.IsControl);
}

public sealed record MediaWikiScribuntoDiagnosticLocation(int Offset, int Length, int Line = 0, int Column = 0);

/// <summary>Measured limits a future sandbox must enforce outside this process.</summary>
public sealed record MediaWikiScribuntoExecutionBudget(
    int MaxInvocations = 32,
    int MaxModuleDependencyDepth = 16,
    int MaxModuleSourceCharacters = 1_000_000,
    int MaxArgumentCharacters = 100_000,
    int MaxResultCharacters = 100_000,
    long MaxInstructionCount = 1_000_000,
    TimeSpan? MaxExecutionTime = null,
    long MaxMemoryBytes = 64L * 1024 * 1024,
    int MaxArgumentCount = 128,
    int MaxIndividualArgumentCharacters = 25_000,
    long MaxDependencySourceCharacters = 4_000_000)
{
    public TimeSpan EffectiveMaxExecutionTime => MaxExecutionTime ?? TimeSpan.FromMilliseconds(250);
}

public sealed record MediaWikiScribuntoInvocation(
    string OriginalText,
    MediaWikiModuleTitle Module,
    string ExportedFunction,
    IReadOnlyList<string> PositionalArguments,
    IReadOnlyDictionary<string, string> NamedArguments,
    string CallingPageTitle,
    string? CallingSourcePageId,
    string? CallingSourceRevisionId,
    IReadOnlyList<string> TemplateInvocationChain,
    CultureInfo Culture,
    TimeZoneInfo TimeZone,
    MediaWikiScribuntoDiagnosticLocation Location);

public sealed record MediaWikiScribuntoPageMetadata(
    string PageTitle,
    string? SourcePageId,
    string? SourceRevisionId,
    DateTime? RevisionTimestampUtc,
    string? ContentModel,
    MediaWikiSourceWikiIdentity SourceWiki);

public sealed record MediaWikiScribuntoExecutionContext(
    MediaWikiScribuntoPageMetadata Page,
    MediaWikiScribuntoExecutionBudget Budget,
    int DependencyDepth,
    IReadOnlyList<string> InvocationChain);

public sealed record MediaWikiScribuntoDependency(
    string DependencyType,
    string Name,
    string? SourceRevisionId = null,
    string Status = "Declared",
    string? SourceWikiIdentity = null,
    string? SourcePageId = null,
    string? SourceSha256 = null);

/// <summary>
/// A module source selected by the host's import and historical provenance
/// policy. Lua receives only these values and never resolves a title itself.
/// </summary>
public sealed record MediaWikiScribuntoModuleSource(
    MediaWikiModuleTitle Module,
    MediaWikiSourceWikiIdentity SourceWiki,
    string SourcePageId,
    string SourceRevisionId,
    string ContentModel,
    string SourceSha256,
    string Source,
    IReadOnlyList<MediaWikiModuleTitle> Dependencies)
{
    public static MediaWikiScribuntoModuleSource Create(
        MediaWikiModuleTitle module,
        MediaWikiSourceWikiIdentity sourceWiki,
        string sourcePageId,
        string sourceRevisionId,
        string contentModel,
        string source,
        IReadOnlyList<MediaWikiModuleTitle>? dependencies = null)
        => new(module, sourceWiki, sourcePageId, sourceRevisionId, contentModel,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))), source,
            dependencies ?? []);
}

public sealed record MediaWikiScribuntoModuleBundle(
    MediaWikiScribuntoModuleSource Root,
    IReadOnlyList<MediaWikiScribuntoModuleSource> Dependencies);

public sealed record MediaWikiScribuntoExecutionResult(
    string? Text,
    IReadOnlyList<MediaWikiCompatibilityDiagnostic> Diagnostics,
    IReadOnlyList<MediaWikiScribuntoDependency> Dependencies,
    bool Executed = false);

public static class MediaWikiScribuntoBudgetValidator
{
    public static IReadOnlyList<MediaWikiCompatibilityDiagnostic> Validate(
        MediaWikiScribuntoInvocation invocation,
        MediaWikiScribuntoExecutionContext context,
        int invocationCount,
        int moduleSourceCharacters,
        int resultCharacters,
        long dependencySourceCharacters = 0)
    {
        var diagnostics = new List<MediaWikiCompatibilityDiagnostic>();
        void Add(string code, string message) => diagnostics.Add(new(code, message, invocation.Location.Offset, invocation.Location.Length, invocation.Location.Line, invocation.Location.Column, MediaWikiDiagnosticSeverity.Error));
        var argumentCharacters = invocation.PositionalArguments.Sum(value => value.Length) + invocation.NamedArguments.Sum(pair => pair.Key.Length + pair.Value.Length);
        if (invocation.PositionalArguments.Count + invocation.NamedArguments.Count > context.Budget.MaxArgumentCount) Add("ScribuntoArgumentCountBudgetExceeded", "The Scribunto invocation argument count exceeded its configured size budget.");
        if (invocation.PositionalArguments.Any(value => value.Length > context.Budget.MaxIndividualArgumentCharacters)
            || invocation.NamedArguments.Any(pair => pair.Key.Length + pair.Value.Length > context.Budget.MaxIndividualArgumentCharacters)) Add("ScribuntoIndividualArgumentBudgetExceeded", "A Scribunto invocation argument exceeded its configured individual size budget.");
        if (invocationCount > context.Budget.MaxInvocations) Add("ScribuntoInvocationBudgetExceeded", "The Scribunto invocation count exceeded its configured budget.");
        if (context.DependencyDepth > context.Budget.MaxModuleDependencyDepth) Add("ScribuntoDependencyDepthExceeded", "The Scribunto module dependency depth exceeded its configured budget.");
        if (moduleSourceCharacters > context.Budget.MaxModuleSourceCharacters) Add("ScribuntoModuleSourceBudgetExceeded", "The Scribunto module source exceeded its configured size budget.");
        if (dependencySourceCharacters > context.Budget.MaxDependencySourceCharacters) Add("ScribuntoDependencySourceBudgetExceeded", "The total Scribunto dependency source exceeded its configured size budget.");
        if (argumentCharacters > context.Budget.MaxArgumentCharacters) Add("ScribuntoArgumentBudgetExceeded", "The Scribunto invocation arguments exceeded their configured size budget.");
        if (resultCharacters > context.Budget.MaxResultCharacters) Add("ScribuntoResultBudgetExceeded", "The Scribunto result exceeded its configured size budget.");
        return diagnostics;
    }
}

/// <summary>
/// Provider-neutral host contract for a future isolated Scribunto runtime.
/// Implementations must not expose database, filesystem, process, reflection,
/// network, or mutable application services to module code.
/// </summary>
public interface IMediaWikiScribuntoExecutionHost
{
    Task<string?> ResolveModuleSourceAsync(MediaWikiModuleTitle module, DateTime? asOfUtc, CancellationToken cancellationToken = default);
    /// <summary>
    /// Resolves a root module and its pre-resolved historical dependency
    /// closure. The compatibility adapter intentionally supplies no dynamic
    /// dependency sources for older hosts.
    /// </summary>
    async Task<MediaWikiScribuntoModuleBundle?> ResolveModuleBundleAsync(
        MediaWikiModuleTitle module,
        DateTime? asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var source = await ResolveModuleSourceAsync(module, asOfUtc, cancellationToken);
        if (source is null) return null;
        var root = MediaWikiScribuntoModuleSource.Create(
            module,
            new MediaWikiSourceWikiIdentity("unversioned-host"),
            module.CanonicalTitle,
            "unversioned",
            "Scribunto",
            source);
        return new(root, []);
    }
    Task<string?> ResolveTemplateTextAsync(string templateTitle, DateTime? asOfUtc, CancellationToken cancellationToken = default);
    Task<MediaWikiScribuntoPageMetadata?> ResolvePageMetadataAsync(string title, DateTime? asOfUtc, CancellationToken cancellationToken = default);
    void RegisterDependency(MediaWikiScribuntoDependency dependency);
}

/// <summary>
/// Optional diagnostic-only pre-resolution boundary. Implementations return
/// bounded, typed Cargo rows from an explicitly authorized dataset; they never
/// expose a connection or SQL to the worker. The default host does not
/// implement this interface, so no Cargo data is sent by normal execution.
/// </summary>
public interface IMediaWikiScribuntoCargoExecutionHost
{
    Task<IReadOnlyList<MediaWikiScribuntoWorkerCargoResult>?> ResolveCargoResultsAsync(
        MediaWikiScribuntoInvocation invocation,
        MediaWikiScribuntoExecutionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Execution provider contract. The default implementation always rejects.</summary>
public interface IMediaWikiScribuntoExecutor
{
    Task<MediaWikiScribuntoExecutionResult> ExecuteAsync(
        MediaWikiScribuntoInvocation invocation,
        IMediaWikiScribuntoExecutionHost host,
        MediaWikiScribuntoExecutionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Safe default: records a diagnostic and never interprets Lua.</summary>
public sealed class RejectingMediaWikiScribuntoExecutor : IMediaWikiScribuntoExecutor
{
    public Task<MediaWikiScribuntoExecutionResult> ExecuteAsync(
        MediaWikiScribuntoInvocation invocation,
        IMediaWikiScribuntoExecutionHost host,
        MediaWikiScribuntoExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);
        var diagnostic = new MediaWikiCompatibilityDiagnostic(
            "ScribuntoExecutionRejected",
            $"Scribunto module execution is disabled for '{invocation.Module.CanonicalTitle}:{invocation.ExportedFunction}'.",
            invocation.Location.Offset,
            invocation.Location.Length,
            invocation.Location.Line,
            invocation.Location.Column,
            MediaWikiDiagnosticSeverity.Warning);
        return Task.FromResult(new MediaWikiScribuntoExecutionResult(
            invocation.OriginalText,
            [diagnostic],
            [new MediaWikiScribuntoDependency("module", invocation.Module.CanonicalTitle, invocation.CallingSourceRevisionId, "ExecutionRejected")],
            false));
    }
}

public static class MediaWikiScribuntoInvocationParser
{
    public static bool TryParse(
        string originalText,
        string body,
        MediaWikiExpansionContext expansion,
        IReadOnlyList<string>? templateInvocationChain,
        int offset,
        out MediaWikiScribuntoInvocation invocation)
    {
        invocation = null!;
        var parts = SplitArguments(body);
        if (parts.Count == 0 || !parts[0].TrimStart().StartsWith("#invoke:", StringComparison.OrdinalIgnoreCase)) return false;
        var moduleText = parts[0].Trim()[8..].Trim();
        var moduleSeparator = moduleText.IndexOf(':');
        if (moduleSeparator >= 0) moduleText = moduleText[(moduleSeparator + 1)..].Trim();
        if (!MediaWikiModuleTitle.TryParse(moduleText, out var module) || parts.Count < 2) return false;
        var function = parts[1].Trim();
        if (function.Length == 0) return false;
        var positional = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in parts.Skip(2))
        {
            var equals = argument.IndexOf('=');
            if (equals > 0) named[argument[..equals].Trim()] = argument[(equals + 1)..];
            else positional.Add(argument);
        }
        invocation = new(
            originalText,
            module,
            function,
            positional,
            named,
            expansion.CurrentPageTitle,
            expansion.SourcePageId,
            expansion.SourceRevisionId,
            templateInvocationChain ?? [],
            expansion.Culture,
            expansion.TimeZone,
            new(offset, originalText.Length));
        return true;
    }

    private static List<string> SplitArguments(string value)
    {
        var result = new List<string>();
        var start = 0;
        var braces = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value.AsSpan(index).StartsWith("{{", StringComparison.Ordinal)) { braces++; index++; continue; }
            if (value.AsSpan(index).StartsWith("}}", StringComparison.Ordinal) && braces > 0) { braces--; index++; continue; }
            if (value[index] == '|' && braces == 0) { result.Add(value[start..index]); start = index + 1; }
        }
        result.Add(value[start..]);
        return result;
    }
}
