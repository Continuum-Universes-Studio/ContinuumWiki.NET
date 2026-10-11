using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

/// <summary>
/// A sanitized stylesheet and the exact imported revision from which it came.
/// The CSS is a typed render asset; callers must still choose a CSP-compatible
/// delivery mechanism.
/// </summary>
public sealed record MediaWikiStylesheet(
    string SourceTitle,
    string SourcePageId,
    string SourceRevisionId,
    int TargetRevision,
    DateTime? SourceTimestampUtc,
    string ContentModel,
    string SourceSha256,
    string ScopeSelector,
    string SanitizedCss,
    string CacheKey,
    string SanitizerPolicyVersion = MediaWikiTemplateStylesCompatibility.SanitizerPolicyVersion,
    string? SanitizedCssSha256 = null,
    long? ImportBatchId = null,
    int ReferenceOrder = 0);

internal sealed class MediaWikiStylesheetRegistry(int maxReferences)
{
    private readonly List<MediaWikiStylesheet> _stylesheets = [];
    private readonly HashSet<string> _identities = new(StringComparer.Ordinal);

    public int ReferenceCount { get; private set; }
    public IReadOnlyList<MediaWikiStylesheet> Stylesheets => _stylesheets;

    public bool TryRegister(MediaWikiStylesheet stylesheet, out string? diagnosticCode)
    {
        diagnosticCode = null;
        if (++ReferenceCount > maxReferences)
        {
            diagnosticCode = "TemplateStylesCssResourceLimitExceeded";
            return false;
        }

        if (!_identities.Add(stylesheet.CacheKey)) return true;
        _stylesheets.Add(stylesheet with { ReferenceOrder = _stylesheets.Count });
        return true;
    }
}

public sealed record MediaWikiTemplateStylesResolution(
    MediaWikiStylesheet? Stylesheet,
    string? DiagnosticCode,
    string Message,
    MediaWikiDiagnosticSeverity Severity);

public sealed record MediaWikiStylesheetCssSanitization(
    string? SanitizedCss,
    string? DiagnosticCode,
    string Message,
    MediaWikiDiagnosticSeverity Severity);

/// <summary>
/// Resolves TemplateStyles through the imported page/revision repository and
/// sanitizes only a deliberately bounded CSS subset. No filesystem, network,
/// PHP, JavaScript, or browser runtime is involved.
/// </summary>
public static class MediaWikiTemplateStylesCompatibility
{
    public const string ExtensionName = "templatestyles";
    public const string SanitizerPolicyVersion = "cw-templatestyles-v1";
    public const string ScopeSelector = ".wiki-page-body";
    public const string CssContentModel = "css";
    public const string SanitizedCssContentModel = "sanitized-css";
    public const string TextCssContentModel = "text/css";

    private static readonly Regex SafeProperty = new(
        "^[a-z][a-z0-9-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeSelectorCharacters = new(
        "^[A-Za-z0-9_ .#:*+>~,-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "align-content", "align-items", "align-self", "background", "background-color",
        "border", "border-bottom", "border-bottom-color", "border-bottom-style", "border-bottom-width",
        "border-color", "border-left", "border-left-color", "border-left-style", "border-left-width",
        "border-radius", "border-right", "border-right-color", "border-right-style", "border-right-width",
        "border-style", "border-top", "border-top-color", "border-top-style", "border-top-width",
        "border-width", "box-sizing", "color", "column-gap", "display", "flex", "flex-basis",
        "flex-direction", "flex-grow", "flex-shrink", "flex-wrap", "font-family", "font-size",
        "font-style", "font-weight", "gap", "grid-column", "grid-row", "grid-template-columns",
        "grid-template-rows", "height", "justify-content", "letter-spacing", "line-height",
        "margin", "margin-block", "margin-block-end", "margin-block-start", "margin-bottom",
        "margin-inline", "margin-inline-end", "margin-inline-start", "margin-left", "margin-right",
        "margin-top", "max-height", "max-width", "min-height", "min-width", "opacity", "overflow",
        "overflow-wrap", "padding", "padding-block", "padding-block-end", "padding-block-start",
        "padding-bottom", "padding-inline", "padding-inline-end", "padding-inline-start",
        "padding-left", "padding-right", "padding-top", "text-align", "text-decoration",
        "text-transform", "vertical-align", "white-space", "width"
    };

    public static async Task<MediaWikiTemplateStylesResolution> ResolveAsync(
        ITwMediaWikiImportRepository repository,
        TwMediaWikiRenderSource provenance,
        string? sourceReference,
        MediaWikiWikitextRenderOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceReference))
            return Failure("TemplateStylesMissingSource", "The TemplateStyles tag requires a non-empty src attribute.");

        var sourceTitle = sourceReference.Trim().Replace('_', ' ');
        if (!TryValidateSourceTitle(sourceTitle, out var sourceError))
            return Failure("TemplateStylesInvalidSource", sourceError);

        if (provenance.ImportBatchId is null)
            return Failure("TemplateStylesSourceUnavailable", "TemplateStyles requires an imported MediaWiki source context.");

        var target = await repository.ResolveTemplateAsync(
            provenance.ImportBatchId.Value,
            sourceTitle,
            provenance.SourceTimestampUtc,
            cancellationToken);

        if (target.Status == TwMediaWikiTemplateResolutionStatus.Missing)
            return Failure("TemplateStylesSourceUnavailable", $"Stylesheet source '{sourceTitle}' was not available in the imported source.");
        if (target.Status != TwMediaWikiTemplateResolutionStatus.Resolved || !target.IsResolved)
            return Failure("TemplateStylesSourceRevisionUnavailable", $"No eligible historical stylesheet revision was available for '{sourceTitle}'.");

        var contentModel = target.ContentModel?.Trim() ?? string.Empty;
        if (!IsSupportedContentModel(contentModel))
            return Failure("TemplateStylesUnsupportedContentModel", $"Stylesheet source '{sourceTitle}' used unsupported content model '{contentModel}'.");

        var sanitized = SanitizeCss(target.Body!, options);
        if (sanitized.SanitizedCss is null)
            return new(null, sanitized.DiagnosticCode, sanitized.Message, sanitized.Severity);

        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Body!)));
        var sanitizedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized.SanitizedCss)));
        var cacheKey = $"{SanitizerPolicyVersion}:{provenance.ImportBatchId}:{target.SourcePageId}:{target.SourceRevisionId}:{sourceHash}:{sanitizedHash}";
        return new(
            new MediaWikiStylesheet(
                target.SourceTitle,
                target.SourcePageId!,
                target.SourceRevisionId!,
                target.TargetRevision!.Value,
                target.SourceTimestampUtc,
                contentModel,
                sourceHash,
                ScopeSelector,
                sanitized.SanitizedCss,
                cacheKey,
                SanitizerPolicyVersion,
                sanitizedHash,
                provenance.ImportBatchId),
            null,
            "TemplateStyles source was resolved and sanitized.",
            MediaWikiDiagnosticSeverity.Information);
    }

    public static bool TryParseAttributes(
        string tagText,
        out IReadOnlyDictionary<string, string> attributes,
        out bool selfClosing,
        out string? error)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        attributes = parsed;
        selfClosing = false;
        error = null;
        var cursor = ExtensionName.Length;
        while (cursor < tagText.Length && char.IsWhiteSpace(tagText[cursor])) cursor++;
        while (cursor < tagText.Length)
        {
            if (tagText[cursor] == '/')
            {
                selfClosing = true;
                cursor++;
                while (cursor < tagText.Length && char.IsWhiteSpace(tagText[cursor])) cursor++;
                if (cursor < tagText.Length)
                {
                    error = "TemplateStyles tag contained trailing characters after '/'.";
                    return false;
                }
                break;
            }

            var nameStart = cursor;
            if (!char.IsLetter(tagText[cursor]) && tagText[cursor] != '_')
            {
                error = "TemplateStyles attribute name was malformed.";
                return false;
            }
            cursor++;
            while (cursor < tagText.Length && (char.IsLetterOrDigit(tagText[cursor]) || tagText[cursor] is '_' or '-' or ':')) cursor++;
            var name = tagText[nameStart..cursor];
            while (cursor < tagText.Length && char.IsWhiteSpace(tagText[cursor])) cursor++;
            if (cursor >= tagText.Length || tagText[cursor] != '=')
            {
                error = "TemplateStyles attributes require explicit values.";
                return false;
            }
            cursor++;
            while (cursor < tagText.Length && char.IsWhiteSpace(tagText[cursor])) cursor++;
            if (cursor >= tagText.Length)
            {
                error = "TemplateStyles attribute value was missing.";
                return false;
            }

            string value;
            if (tagText[cursor] is '"' or '\'')
            {
                var quote = tagText[cursor++];
                var valueStart = cursor;
                var closing = tagText.IndexOf(quote, cursor);
                if (closing < 0)
                {
                    error = "TemplateStyles attribute quote was not closed.";
                    return false;
                }
                value = tagText[valueStart..closing];
                cursor = closing + 1;
            }
            else
            {
                var valueStart = cursor;
                while (cursor < tagText.Length && !char.IsWhiteSpace(tagText[cursor]) && tagText[cursor] != '/') cursor++;
                value = tagText[valueStart..cursor];
            }

            if (!parsed.TryAdd(name, value))
            {
                error = $"TemplateStyles attribute '{name}' was repeated.";
                return false;
            }
            while (cursor < tagText.Length && char.IsWhiteSpace(tagText[cursor])) cursor++;
        }

        foreach (var name in parsed.Keys)
        {
            if (!name.Equals("src", StringComparison.OrdinalIgnoreCase))
            {
                error = $"TemplateStyles attribute '{name}' is not supported.";
                return false;
            }
        }
        return true;
    }

    public static MediaWikiStylesheetCssSanitization SanitizeCss(string css, MediaWikiWikitextRenderOptions options)
    {
        if (css.Length > options.MaxTemplateStylesSourceCharacters)
            return FailureCss("TemplateStylesCssResourceLimitExceeded", "TemplateStyles source CSS exceeded the configured source-size budget.");

        if (!TryRemoveComments(css, out var source))
            return FailureCss("TemplateStylesCssSyntaxInvalid", "TemplateStyles CSS contained an unterminated comment.");

        var cursor = 0;
        var ruleCount = 0;
        var selectorCount = 0;
        var declarationCount = 0;
        var output = new StringBuilder(Math.Min(source.Length, options.MaxTemplateStylesOutputCharacters));
        while (true)
        {
            SkipWhitespace(source, ref cursor);
            if (cursor >= source.Length) break;
            if (source[cursor] == '@')
                return FailureCss("TemplateStylesCssSecurityRejected", "TemplateStyles at-rules, including @import, are not permitted.");

            var selectorStart = cursor;
            var openBrace = FindNextTopLevel(source, ref cursor, '{', out var selectorError);
            if (selectorError is not null)
                return FailureCss("TemplateStylesCssSyntaxInvalid", selectorError);
            if (openBrace < 0)
                return FailureCss("TemplateStylesCssSyntaxInvalid", "TemplateStyles CSS contained a rule without a declaration block.");

            var selectorText = source[selectorStart..openBrace].Trim();
            if (!TryScopeSelectors(selectorText, options.MaxTemplateStylesSelectorComplexity, out var scopedSelectors, out var selectorsInRule, out var selectorMessage))
            {
                var selectorCode = selectorMessage.Contains("security", StringComparison.OrdinalIgnoreCase)
                    ? "TemplateStylesCssSecurityRejected"
                    : selectorMessage.Contains("resource limit", StringComparison.OrdinalIgnoreCase)
                        ? "TemplateStylesCssResourceLimitExceeded"
                        : "TemplateStylesCssSyntaxInvalid";
                return FailureCss(selectorCode, selectorMessage);
            }

            cursor = openBrace + 1;
            var closeBrace = FindClosingBrace(source, ref cursor, out var blockError);
            if (blockError is not null)
                return FailureCss("TemplateStylesCssSyntaxInvalid", blockError);
            if (closeBrace < 0)
                return FailureCss("TemplateStylesCssSyntaxInvalid", "TemplateStyles CSS contained an unclosed declaration block.");

            if (++ruleCount > options.MaxTemplateStylesRules)
                return FailureCss("TemplateStylesCssResourceLimitExceeded", "TemplateStyles CSS exceeded the rule-count budget.");
            selectorCount += selectorsInRule;
            if (selectorCount > options.MaxTemplateStylesSelectors)
                return FailureCss("TemplateStylesCssResourceLimitExceeded", "TemplateStyles CSS exceeded the selector-count budget.");

            var declarations = source[(openBrace + 1)..closeBrace];
            if (!TrySanitizeDeclarations(declarations, options.MaxTemplateStylesDeclarations - declarationCount, out var sanitizedDeclarations, out var declarationsInRule, out var declarationCode, out var declarationMessage))
                return FailureCss(declarationCode!, declarationMessage);
            declarationCount += declarationsInRule;
            if (declarationCount > options.MaxTemplateStylesDeclarations)
                return FailureCss("TemplateStylesCssResourceLimitExceeded", "TemplateStyles CSS exceeded the declaration-count budget.");

            var rule = scopedSelectors + "{" + sanitizedDeclarations + "}";
            if (output.Length + rule.Length + 1 > options.MaxTemplateStylesOutputCharacters)
                return FailureCss("TemplateStylesCssResourceLimitExceeded", "Sanitized TemplateStyles CSS exceeded the output-size budget.");
            output.Append(rule).Append('\n');
            cursor = closeBrace + 1;
        }

        return new(output.ToString(), null, "TemplateStyles CSS was accepted.", MediaWikiDiagnosticSeverity.Information);
    }

    private static bool TryValidateSourceTitle(string value, out string message)
    {
        message = string.Empty;
        if (!value.StartsWith("Template:", StringComparison.OrdinalIgnoreCase))
        {
            message = "TemplateStyles src must name a page in the Template namespace.";
            return false;
        }

        var title = value["Template:".Length..].Trim();
        if (title.Length == 0 || title.Length > 255 || title.Contains('\0') || title.Contains('\n') || title.Contains('\r') ||
            title.Contains("://", StringComparison.Ordinal) || title.StartsWith("/", StringComparison.Ordinal) ||
            title.Contains('\\') || title.Split('/').Any(part => part is "." or ".."))
        {
            message = "TemplateStyles src was not a safe local Template page reference.";
            return false;
        }
        return true;
    }

    private static bool IsSupportedContentModel(string contentModel)
        => contentModel.Equals(CssContentModel, StringComparison.OrdinalIgnoreCase)
        || contentModel.Equals(SanitizedCssContentModel, StringComparison.OrdinalIgnoreCase)
        || contentModel.Equals(TextCssContentModel, StringComparison.OrdinalIgnoreCase);

    private static bool TryRemoveComments(string source, out string result)
    {
        var builder = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                var close = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    result = string.Empty;
                    return false;
                }
                index = close + 1;
                continue;
            }
            builder.Append(source[index]);
        }
        result = builder.ToString();
        return true;
    }

    private static int FindNextTopLevel(string source, ref int cursor, char expected, out string? error)
    {
        var start = cursor;
        var quote = '\0';
        var parentheses = 0;
        for (; cursor < source.Length; cursor++)
        {
            var value = source[cursor];
            if (quote != '\0')
            {
                if (value == quote && (cursor == 0 || source[cursor - 1] != '\\')) quote = '\0';
                continue;
            }
            if (value is '"' or '\'') { quote = value; continue; }
            if (value == '(') { parentheses++; continue; }
            if (value == ')') { if (--parentheses < 0) break; continue; }
            if (parentheses == 0 && value == expected)
            {
                error = null;
                return cursor;
            }
            if (parentheses == 0 && value is '}' or ';')
            {
                error = "TemplateStyles CSS contained an unexpected rule delimiter.";
                return -1;
            }
        }
        error = quote != '\0' ? "TemplateStyles CSS contained an unclosed string." :
            parentheses != 0 ? "TemplateStyles CSS contained unbalanced parentheses." : null;
        return -1;
    }

    private static int FindClosingBrace(string source, ref int cursor, out string? error)
    {
        var quote = '\0';
        var parentheses = 0;
        for (; cursor < source.Length; cursor++)
        {
            var value = source[cursor];
            if (quote != '\0')
            {
                if (value == quote && (cursor == 0 || source[cursor - 1] != '\\')) quote = '\0';
                continue;
            }
            if (value is '"' or '\'') { quote = value; continue; }
            if (value == '(') { parentheses++; continue; }
            if (value == ')') { if (--parentheses < 0) break; continue; }
            if (parentheses == 0 && value == '{')
            {
                error = "Nested CSS blocks are not supported.";
                return -1;
            }
            if (parentheses == 0 && value == '}')
            {
                error = null;
                return cursor;
            }
        }
        error = quote != '\0' ? "TemplateStyles CSS contained an unclosed string." :
            parentheses != 0 ? "TemplateStyles CSS contained unbalanced parentheses." : null;
        return -1;
    }

    private static bool TryScopeSelectors(
        string selectorText,
        int maxComplexity,
        out string scoped,
        out int selectorCount,
        out string message)
    {
        var selectors = selectorText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        selectorCount = selectors.Length;
        if (selectors.Length == 0)
        {
            scoped = string.Empty;
            message = "TemplateStyles CSS contained an empty selector.";
            return false;
        }

        var scopedSelectors = new List<string>(selectors.Length);
        foreach (var selector in selectors)
        {
            if (selector.Length > 512 || !SafeSelectorCharacters.IsMatch(selector) ||
                selector.Contains('[', StringComparison.Ordinal) || selector.Contains(']', StringComparison.Ordinal) ||
                selector.Contains('(', StringComparison.Ordinal) || selector.Contains(')', StringComparison.Ordinal) ||
                selector.Contains('\\', StringComparison.Ordinal) || selector.Contains('&', StringComparison.Ordinal) ||
                selector.Contains('@', StringComparison.Ordinal) || selector.Contains('<', StringComparison.Ordinal))
            {
                scoped = string.Empty;
                message = "TemplateStyles selector failed the security or syntax boundary.";
                return false;
            }

            var parts = selector.Split([' ', '\t', '>', '+', '~'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length + selector.Count(value => value is '>' or '+' or '~') > maxComplexity)
            {
                scoped = string.Empty;
                message = "TemplateStyles selector complexity exceeded the configured resource limit.";
                return false;
            }
            if (parts.Any(part => part.Equals("html", StringComparison.OrdinalIgnoreCase)
                || part.Equals("body", StringComparison.OrdinalIgnoreCase)
                || part.Equals("head", StringComparison.OrdinalIgnoreCase)
                || part.Equals(":root", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith(":global", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith(":host", StringComparison.OrdinalIgnoreCase)))
            {
                scoped = string.Empty;
                message = "TemplateStyles selector attempted to escape the wiki content scope security boundary.";
                return false;
            }
            scopedSelectors.Add(selector.TrimStart().StartsWith('*') ? ScopeSelector + " *" : ScopeSelector + " " + selector.Trim());
        }

        scoped = string.Join(',', scopedSelectors);
        message = string.Empty;
        return true;
    }

    private static bool TrySanitizeDeclarations(
        string declarations,
        int remainingBudget,
        out string sanitized,
        out int count,
        out string? code,
        out string message)
    {
        var parts = declarations.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        count = 0;
        var output = new StringBuilder(declarations.Length);
        foreach (var part in parts)
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
            {
                sanitized = string.Empty;
                code = "TemplateStylesCssSyntaxInvalid";
                message = "TemplateStyles CSS contained a declaration without a property and value.";
                return false;
            }

            var property = part[..colon].Trim();
            var value = part[(colon + 1)..].Trim();
            if (!SafeProperty.IsMatch(property) || !AllowedProperties.Contains(property) || value.Length == 0 || value.Length > 512)
            {
                sanitized = string.Empty;
                code = "TemplateStylesCssSecurityRejected";
                message = $"TemplateStyles CSS property '{property}' was not in the safe allow-list.";
                return false;
            }
            if (IsDangerousCssValue(value) || value.Contains('<') || value.Contains('>') || value.Contains('{') || value.Contains('}') ||
                value.Contains('\\') || value.Contains("!important", StringComparison.OrdinalIgnoreCase))
            {
                sanitized = string.Empty;
                code = "TemplateStylesCssSecurityRejected";
                message = $"TemplateStyles CSS value for '{property}' contained a prohibited construct.";
                return false;
            }
            if (++count > remainingBudget)
            {
                sanitized = string.Empty;
                code = "TemplateStylesCssResourceLimitExceeded";
                message = "TemplateStyles CSS exceeded the declaration-count budget.";
                return false;
            }
            output.Append(property).Append(':').Append(value).Append(';');
        }

        sanitized = output.ToString();
        code = null;
        message = string.Empty;
        return true;
    }

    private static void SkipWhitespace(string source, ref int cursor)
    {
        while (cursor < source.Length && char.IsWhiteSpace(source[cursor])) cursor++;
    }

    private static bool IsDangerousCssValue(string value)
    {
        var compact = string.Concat(value.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();
        return compact.Contains("@import", StringComparison.Ordinal)
            || compact.Contains("url(", StringComparison.Ordinal)
            || compact.Contains("expression(", StringComparison.Ordinal)
            || compact.Contains("javascript:", StringComparison.Ordinal)
            || compact.Contains("behavior:", StringComparison.Ordinal)
            || compact.Contains("-moz-binding", StringComparison.Ordinal)
            || compact.Contains("var(", StringComparison.Ordinal)
            || compact.Contains("attr(", StringComparison.Ordinal)
            || compact.Contains("env(", StringComparison.Ordinal);
    }

    private static MediaWikiTemplateStylesResolution Failure(string code, string message)
        => new(null, code, message, code.Contains("Security", StringComparison.Ordinal) ? MediaWikiDiagnosticSeverity.Error : MediaWikiDiagnosticSeverity.Warning);

    private static MediaWikiStylesheetCssSanitization FailureCss(string code, string message)
        => new(null, code, message, code.Contains("Security", StringComparison.Ordinal) || code.Contains("Limit", StringComparison.Ordinal)
            ? MediaWikiDiagnosticSeverity.Error : MediaWikiDiagnosticSeverity.Warning);
}
