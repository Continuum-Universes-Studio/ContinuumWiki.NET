using System.Text.RegularExpressions;

namespace MediaWikiInventory;

public sealed record MediaWikiModuleInvocation(
    string RawText,
    string? ModuleTitle,
    string? Function,
    IReadOnlyList<string> PositionalArguments,
    IReadOnlyDictionary<string, string> NamedArguments,
    int Offset,
    bool IsMalformed = false,
    bool IsDynamic = false);

public sealed record MediaWikiModuleScanResult(
    IReadOnlyList<MediaWikiModuleInvocation> Invocations,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> UncertainDependencies,
    IReadOnlyList<PlanFinding> Findings);

/// <summary>
/// Bounded lexical inventory for Scribunto references. It records static
/// evidence and uncertainty; it never parses or executes Lua.
/// </summary>
public static class MediaWikiScribuntoScanner
{
    private static readonly Regex StaticLoader = new("\\b(?:require|mw\\.loadData|mw\\.loadJsonData)\\s*\\(\\s*['\"]([^'\"]+)['\"]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DynamicLoader = new(@"\b(?:require|mw\.loadData|mw\.loadJsonData)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static MediaWikiModuleScanResult Scan(string text, string? sourcePageId = null, string? sourceRevisionId = null)
    {
        var invocations = new List<MediaWikiModuleInvocation>();
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uncertain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var findings = new List<PlanFinding>();
        var index = 0;
        while ((index = IndexOfInvoke(text, index)) >= 0)
        {
            if (!TryReadInvocation(text, index, out var end, out var body))
            {
                invocations.Add(new(text[index..Math.Min(text.Length, index + 128)], null, null, [], new Dictionary<string, string>(), index, true));
                findings.Add(new(PlanFindingSeverity.Warning, "MalformedModuleInvocation", "A #invoke call has no balanced closing delimiter; static module analysis is incomplete.", sourcePageId, sourceRevisionId));
                break;
            }
            var parsed = ParseInvocation(text[index..(end + 2)], body, index);
            invocations.Add(parsed);
            if (!string.IsNullOrWhiteSpace(parsed.ModuleTitle)) dependencies.Add(NormalizeModule(parsed.ModuleTitle));
            if (parsed.IsMalformed || parsed.IsDynamic)
            {
                if (!string.IsNullOrWhiteSpace(parsed.ModuleTitle)) uncertain.Add(NormalizeModule(parsed.ModuleTitle));
                findings.Add(new(PlanFindingSeverity.Warning, parsed.IsMalformed ? "MalformedModuleInvocation" : "DynamicModuleReference", parsed.IsMalformed
                    ? "A #invoke call is incomplete and requires runtime-compatible review."
                    : "A #invoke module or function is dynamic; static analysis cannot determine its complete dependency set.", sourcePageId, sourceRevisionId));
            }
            index = end + 2;
        }

        foreach (Match match in StaticLoader.Matches(text)) dependencies.Add(NormalizeModule(match.Groups[1].Value));
        var staticLoaderOffsets = StaticLoader.Matches(text).Select(match => match.Index).ToHashSet();
        foreach (Match match in DynamicLoader.Matches(text))
        {
            if (!staticLoaderOffsets.Contains(match.Index))
            {
                uncertain.Add("<dynamic>");
                findings.Add(new(PlanFindingSeverity.Warning, "DynamicModuleReference", "Lua module loading uses a non-literal argument; static analysis cannot resolve it.", sourcePageId, sourceRevisionId));
            }
        }

        return new(invocations, dependencies.Order(StringComparer.OrdinalIgnoreCase).ToArray(), uncertain.Order(StringComparer.OrdinalIgnoreCase).ToArray(), findings);
    }

    private static int IndexOfInvoke(string text, int start)
        => text.IndexOf("{{#invoke:", start, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadInvocation(string text, int start, out int end, out string body)
    {
        var depth = 0;
        for (var index = start; index < text.Length - 1; index++)
        {
            if (text.AsSpan(index).StartsWith("{{", StringComparison.Ordinal)) { depth++; index++; continue; }
            if (text.AsSpan(index).StartsWith("}}", StringComparison.Ordinal))
            {
                depth--;
                if (depth == 0) { end = index; body = text[(start + 2)..index]; return true; }
                index++;
            }
        }
        end = -1;
        body = string.Empty;
        return false;
    }

    private static MediaWikiModuleInvocation ParseInvocation(string raw, string body, int offset)
    {
        var parts = SplitTopLevel(body, '|');
        var head = parts.ElementAtOrDefault(0)?.Trim() ?? string.Empty;
        var moduleText = head.StartsWith("#invoke:", StringComparison.OrdinalIgnoreCase) ? head[8..].Trim() : string.Empty;
        var function = parts.ElementAtOrDefault(1)?.Trim();
        var dynamic = moduleText.Contains("{{", StringComparison.Ordinal) || function?.Contains("{{", StringComparison.Ordinal) == true;
        var module = moduleText.Length == 0 ? null : NormalizeModule(moduleText);
        var positional = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in parts.Skip(2))
        {
            var equals = argument.IndexOf('=');
            if (equals > 0) named[argument[..equals].Trim()] = argument[(equals + 1)..];
            else positional.Add(argument);
        }
        return new(raw, module, function, positional, named, offset, module is null || string.IsNullOrWhiteSpace(function), dynamic);
    }

    private static List<string> SplitTopLevel(string value, char separator)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value.AsSpan(index).StartsWith("{{", StringComparison.Ordinal)) { depth++; index++; continue; }
            if (value.AsSpan(index).StartsWith("}}", StringComparison.Ordinal) && depth > 0) { depth--; index++; continue; }
            if (value[index] == separator && depth == 0) { result.Add(value[start..index]); start = index + 1; }
        }
        result.Add(value[start..]);
        return result;
    }

    public static string NormalizeModule(string value)
    {
        var normalized = value.Trim().Replace('_', ' ');
        var separator = normalized.IndexOf(':');
        if (separator >= 0 && (string.Equals(normalized[..separator].Trim(), "Module", StringComparison.OrdinalIgnoreCase) || string.Equals(normalized[..separator].Trim(), "Module talk", StringComparison.OrdinalIgnoreCase))) normalized = normalized[(separator + 1)..].Trim();
        return normalized;
    }
}
