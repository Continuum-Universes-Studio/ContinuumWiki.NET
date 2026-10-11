using System.Net;
using System.Text.RegularExpressions;
using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

public enum MediaWikiExtensionCapability
{
    SafeStatic,
    BoundedNative,
    ExternalRendererRequired,
    WriteCapabilityRequired,
    Unsupported,
    Indeterminate
}

public enum MediaWikiExtensionRenderStatus
{
    StaticFallback,
    InvalidInput,
    InputLimitExceeded,
    OutputLimitExceeded,
    SecurityRejected,
    ExternalRendererUnavailable,
    Unsupported
}

public sealed record MediaWikiExtensionRenderRequest(
    string ExtensionName,
    string Source,
    IReadOnlyDictionary<string, string> Attributes,
    TwMediaWikiRenderSource? SourceContext,
    int MaxInputCharacters,
    int MaxNestingDepth,
    int NestingDepth,
    CancellationToken CancellationToken = default);

public sealed record MediaWikiExtensionRenderResult(
    MediaWikiExtensionCapability Capability,
    MediaWikiExtensionRenderStatus Status,
    string? Html,
    string? DiagnosticCode,
    string Message,
    MediaWikiDiagnosticSeverity Severity = MediaWikiDiagnosticSeverity.Warning);

/// <summary>
/// Safe, source-preserving handling for the observed Mermaid extension tag.
/// This deliberately does not parse Mermaid into SVG or invoke an external renderer.
/// </summary>
public static class MediaWikiMermaidCompatibility
{
    public const string ExtensionName = "mermaid";
    public const int DefaultMaxInputCharacters = 100_000;
    public const int DefaultMaxNestingDepth = 1;

    private static readonly Regex DiagramType = new(
        "^(?:graph|flowchart|sequenceDiagram|classDiagram|stateDiagram(?:-v2)?|erDiagram|journey|gantt|pie|quadrantChart|gitGraph|mindmap|timeline|xychart-beta|block-beta|C4Context|sankey-beta|architecture-beta)(?:\\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex DangerousMarkup = new(
        "<\\s*/?\\s*(?:script|iframe|object|embed|img|svg)(?:\\s|>|/)|\\bon[a-z][a-z0-9_-]*\\s*=|javascript\\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static MediaWikiExtensionRenderResult CreateStaticResult(MediaWikiExtensionRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.CancellationToken.ThrowIfCancellationRequested();

        if (!request.ExtensionName.Equals(ExtensionName, StringComparison.OrdinalIgnoreCase))
            return Failure(MediaWikiExtensionCapability.Unsupported, MediaWikiExtensionRenderStatus.Unsupported,
                "UnsupportedExtension", $"Extension '{request.ExtensionName}' has no authorized compatibility adapter.");

        if (request.MaxInputCharacters < 1 || request.Source.Length > request.MaxInputCharacters)
            return Failure(MediaWikiExtensionCapability.SafeStatic, MediaWikiExtensionRenderStatus.InputLimitExceeded,
                "ExtensionInputLimitExceeded", "Mermaid source exceeded the configured extension input limit.");

        if (request.NestingDepth > request.MaxNestingDepth ||
            request.Source.Contains("<mermaid", StringComparison.OrdinalIgnoreCase) ||
            request.Source.Contains("</mermaid", StringComparison.OrdinalIgnoreCase))
            return Failure(MediaWikiExtensionCapability.SafeStatic, MediaWikiExtensionRenderStatus.InvalidInput,
                "InvalidExtensionInput", "Nested Mermaid extension tags are not supported.");

        foreach (var attribute in request.Attributes)
        {
            if (attribute.Key.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                attribute.Key.Equals("style", StringComparison.OrdinalIgnoreCase) ||
                attribute.Key.Equals("src", StringComparison.OrdinalIgnoreCase) ||
                attribute.Key.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                attribute.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
                return Failure(MediaWikiExtensionCapability.SafeStatic, MediaWikiExtensionRenderStatus.SecurityRejected,
                    "ExtensionSecurityRejected", $"Mermaid attribute '{attribute.Key}' was rejected by the extension boundary.");

            return Failure(MediaWikiExtensionCapability.SafeStatic, MediaWikiExtensionRenderStatus.InvalidInput,
                "InvalidExtensionInput", $"Mermaid attribute '{attribute.Key}' is not in the empty allow-list.");
        }

        if (string.IsNullOrWhiteSpace(request.Source))
            return Failure(MediaWikiExtensionCapability.SafeStatic, MediaWikiExtensionRenderStatus.InvalidInput,
                "InvalidExtensionInput", "Mermaid source must not be empty.");

        if (DangerousMarkup.IsMatch(request.Source))
            return Failure(MediaWikiExtensionCapability.SafeStatic, MediaWikiExtensionRenderStatus.SecurityRejected,
                "ExtensionSecurityRejected", "Mermaid source contained executable or embedded markup and was rejected.");

        var firstSourceLine = request.Source
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("%%", StringComparison.Ordinal));
        if (firstSourceLine is null || !DiagramType.IsMatch(firstSourceLine + " "))
            return Failure(MediaWikiExtensionCapability.SafeStatic, MediaWikiExtensionRenderStatus.InvalidInput,
                "InvalidExtensionInput", "Mermaid source did not begin with an observed, supported diagram type.");

        var html = "<figure class=\"cw-mermaid\" role=\"group\" aria-label=\"Mermaid diagram source (static)\">" +
                   "<figcaption class=\"cw-mermaid__label\">Mermaid diagram source (static; diagram rendering unavailable)</figcaption>" +
                   "<pre class=\"cw-mermaid__source\">" + WebUtility.HtmlEncode(request.Source) + "</pre></figure>";
        return new(
            MediaWikiExtensionCapability.SafeStatic,
            MediaWikiExtensionRenderStatus.StaticFallback,
            html,
            "ExternalRendererUnavailable",
            "Mermaid source was preserved as a bounded static representation; diagram rendering was not evaluated.");
    }

    public static bool TryParseAttributes(
        string tagText,
        out IReadOnlyDictionary<string, string> attributes,
        out bool selfClosing,
        out string? error)
    {
        var parsedAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        attributes = parsedAttributes;
        selfClosing = false;
        error = null;
        var cursor = ExtensionName.Length;
        while (cursor < tagText.Length && char.IsWhiteSpace(tagText[cursor])) cursor++;
        if (cursor < tagText.Length && tagText[cursor] == '/')
        {
            selfClosing = true;
            cursor++;
            while (cursor < tagText.Length && char.IsWhiteSpace(tagText[cursor])) cursor++;
        }

        while (cursor < tagText.Length)
        {
            var nameStart = cursor;
            if (!char.IsLetter(tagText[cursor]) && tagText[cursor] != '_' && tagText[cursor] != ':')
                return InvalidAttribute("Malformed Mermaid attribute name.", out error);
            cursor++;
            while (cursor < tagText.Length && (char.IsLetterOrDigit(tagText[cursor]) || tagText[cursor] is '_' or ':' or '-' or '.')) cursor++;
            var name = tagText[nameStart..cursor];
            while (cursor < tagText.Length && char.IsWhiteSpace(tagText[cursor])) cursor++;
            if (cursor >= tagText.Length || tagText[cursor] != '=') return InvalidAttribute("Mermaid attributes require an explicit value.", out error);
            cursor++;
            while (cursor < tagText.Length && char.IsWhiteSpace(tagText[cursor])) cursor++;
            if (cursor >= tagText.Length) return InvalidAttribute("Mermaid attribute value is missing.", out error);
            string value;
            if (tagText[cursor] is '"' or '\'')
            {
                var quote = tagText[cursor++];
                var valueStart = cursor;
                var closing = tagText.IndexOf(quote, cursor);
                if (closing < 0) return InvalidAttribute("Mermaid attribute quote is not closed.", out error);
                value = tagText[valueStart..closing];
                cursor = closing + 1;
            }
            else
            {
                var valueStart = cursor;
                while (cursor < tagText.Length && !char.IsWhiteSpace(tagText[cursor])) cursor++;
                value = tagText[valueStart..cursor];
            }
            if (!parsedAttributes.TryAdd(name, value)) return InvalidAttribute($"Mermaid attribute '{name}' was repeated.", out error);
            while (cursor < tagText.Length && char.IsWhiteSpace(tagText[cursor])) cursor++;
            if (cursor < tagText.Length && tagText[cursor] == '/')
            {
                selfClosing = true;
                cursor++;
                while (cursor < tagText.Length && char.IsWhiteSpace(tagText[cursor])) cursor++;
            }
        }
        return true;
    }

    private static MediaWikiExtensionRenderResult Failure(
        MediaWikiExtensionCapability capability,
        MediaWikiExtensionRenderStatus status,
        string code,
        string message)
        => new(capability, status, null, code, message,
            status is MediaWikiExtensionRenderStatus.InputLimitExceeded or MediaWikiExtensionRenderStatus.SecurityRejected
                ? MediaWikiDiagnosticSeverity.Error
                : MediaWikiDiagnosticSeverity.Warning);

    private static bool InvalidAttribute(string message, out string? error)
    {
        error = message;
        return false;
    }
}
