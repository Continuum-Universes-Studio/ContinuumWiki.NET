using System.Collections.ObjectModel;

namespace TightWiki.Engine.MediaWiki;

/// <summary>
/// The bounded namespace/title grammar used by MediaWiki transclusion. The
/// namespace names and aliases intentionally mirror the import planner's
/// built-in mapping; this is not a general MediaWiki title parser.
/// </summary>
public sealed record MediaWikiTemplateTitle(
    string Name,
    int NamespaceId,
    string NamespaceName,
    string CanonicalTitle)
{
    private static readonly IReadOnlyDictionary<string, (int Id, string Name)> Namespaces =
        new ReadOnlyDictionary<string, (int Id, string Name)>(new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["main"] = (0, ""), ["talk"] = (1, "Talk"),
            ["user"] = (2, "User"), ["user talk"] = (3, "User talk"),
            ["project"] = (4, "Project"), ["project talk"] = (5, "Project talk"),
            ["file"] = (6, "File"), ["image"] = (6, "File"),
            ["file talk"] = (7, "File talk"), ["image talk"] = (7, "File talk"),
            ["mediawiki"] = (8, "MediaWiki"), ["mediawiki talk"] = (9, "MediaWiki talk"),
            ["template"] = (10, "Template"), ["template talk"] = (11, "Template talk"),
            ["help"] = (12, "Help"), ["help talk"] = (13, "Help talk"),
            ["category"] = (14, "Category"), ["category talk"] = (15, "Category talk"),
            ["module"] = (828, "Module"), ["module talk"] = (829, "Module talk")
        });

    public static bool TryParse(string value, out MediaWikiTemplateTitle title, out string reason)
    {
        title = null!;
        reason = string.Empty;
        if (value is null)
        {
            reason = "empty";
            return false;
        }

        string normalized;
        try { normalized = Uri.UnescapeDataString(value); }
        catch (UriFormatException)
        {
            reason = "invalid-escape";
            return false;
        }

        normalized = CollapseWhitespace(normalized.Replace('_', ' ')).Trim();
        if (normalized.Length == 0)
        {
            reason = "empty";
            return false;
        }

        var namespaceId = 10;
        var namespaceName = "Template";
        var separator = normalized.IndexOf(':');
        if (separator == 0)
        {
            normalized = normalized[1..].Trim();
            namespaceId = 0;
            namespaceName = string.Empty;
        }
        else if (separator > 0)
        {
            var prefix = normalized[..separator].Trim();
            if (!Namespaces.TryGetValue(prefix, out var mapping))
            {
                reason = "unknown-namespace";
                return false;
            }
            namespaceId = mapping.Id;
            namespaceName = mapping.Name;
            normalized = normalized[(separator + 1)..].Trim();
        }

        if (normalized.Length == 0 || normalized.StartsWith(':') || normalized.Contains('|') || normalized.Any(char.IsControl))
        {
            reason = "invalid-name";
            return false;
        }

        var canonical = namespaceId == 0 ? normalized : $"{namespaceName}:{normalized}";
        title = new(normalized, namespaceId, namespaceName, canonical);
        return true;
    }

    private static string CollapseWhitespace(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace) result.Append(' ');
            result.Append(character);
            pendingSpace = false;
        }
        return result.ToString();
    }
}
