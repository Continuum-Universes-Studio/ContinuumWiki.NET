using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Collections.ObjectModel;

namespace TightWiki.Engine.MediaWiki;

/// <summary>
/// Immutable, JSON-safe representation of the schema subset used by the
/// ContinuumInfobox compatibility adapter.  It intentionally contains no PHP
/// callbacks, HTML, URLs, or database/provider references.
/// </summary>
public sealed record MediaWikiInfoboxSchema(
    string Type,
    string Label,
    string TitleField = "name",
    string? SubtitleField = null,
    string? ImageField = "image",
    string? CaptionField = "caption",
    IReadOnlyList<MediaWikiInfoboxField>? Fields = null,
    IReadOnlyList<MediaWikiInfoboxGroup>? Groups = null,
    IReadOnlyDictionary<string, string>? Aliases = null,
    bool RenderUnknownFields = true,
    IReadOnlyList<string>? Extends = null,
    IReadOnlyList<string>? Packs = null)
{
    [JsonIgnore]
    public IReadOnlyList<MediaWikiInfoboxField> EffectiveFields { get; } = Array.AsReadOnly((Fields ?? []).ToArray());
    [JsonIgnore]
    public IReadOnlyList<MediaWikiInfoboxGroup> EffectiveGroups { get; } = Array.AsReadOnly((Groups ?? []).ToArray());
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> EffectiveAliases { get; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(Aliases ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
}

public sealed record MediaWikiInfoboxField(string Name, string Label, string? Group = null);

public sealed record MediaWikiInfoboxGroup(string Name, string Label, string Layout = "nested");

public sealed record MediaWikiInfoboxFieldValue(string Name, string Label, string Value, string? Group, bool Horizontal, string? GroupLabel = null);

public sealed record MediaWikiInfoboxGroupValue(string Name, string Label, bool Horizontal);

internal sealed record MediaWikiInfoboxArgument(string Name, string Value, string? Group = null, bool Horizontal = false);

public sealed record MediaWikiInfoboxRenderModel(
    string Type,
    string Label,
    string? Title,
    string? Subtitle,
    string? Image,
    string? Caption,
    IReadOnlyList<MediaWikiInfoboxFieldValue> Fields,
    IReadOnlyList<MediaWikiInfoboxGroupValue>? Groups = null);

public sealed record MediaWikiInfoboxSchemaDocument(
    int SchemaVersion,
    IReadOnlyList<MediaWikiInfoboxSchema> Schemas);

/// <summary>Bounded registry for native and legacy ContinuumInfobox data.</summary>
public sealed class MediaWikiInfoboxRegistry
{
    private readonly IReadOnlyDictionary<string, MediaWikiInfoboxSchema> _schemas;

    public MediaWikiInfoboxRegistry(IEnumerable<MediaWikiInfoboxSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var schemaList = schemas.ToList();
        if (schemaList.Count > 128) throw new ArgumentException("Infobox schema count exceeded its bound.", nameof(schemas));
        var raw = new Dictionary<string, MediaWikiInfoboxSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in schemaList)
        {
            var type = NormalizeToken(schema.Type);
            if (type.Length == 0 || type.Length > 64) throw new ArgumentException("Infobox schema type is invalid.", nameof(schemas));
            if (!raw.TryAdd(type, NormalizeSchema(schema with { Type = type })))
                throw new ArgumentException($"Duplicate infobox schema '{type}'.", nameof(schemas));
        }
        if (raw.Count == 0) throw new ArgumentException("At least one infobox schema is required.", nameof(schemas));
        if (!raw.ContainsKey("generic")) throw new ArgumentException("The registry requires a generic schema.", nameof(schemas));

        var resolved = new Dictionary<string, MediaWikiInfoboxSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in raw.Keys.OrderBy(value => value, StringComparer.Ordinal))
            resolved[type] = Resolve(raw, type, [], 0);
        foreach (var schema in resolved.Values)
        {
            var validTargets = schema.EffectiveFields.Select(field => MediaWikiInfoboxCompatibility.NormalizeField(field.Name))
                .Concat(new string?[] { schema.TitleField, schema.SubtitleField, schema.ImageField, schema.CaptionField }.Where(value => value is not null).Cast<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (schema.EffectiveAliases.Any(pair => !validTargets.Contains(MediaWikiInfoboxCompatibility.NormalizeField(pair.Value))))
                throw new ArgumentException($"Infobox schema '{schema.Type}' contains an alias for an unknown field.");
        }
        _schemas = resolved;
    }

    public static MediaWikiInfoboxRegistry Default { get; } = new([
        new MediaWikiInfoboxSchema("generic", "Infobox"),
        new MediaWikiInfoboxSchema("species", "Species", Fields: [
            new("name", "Name", "overview"), new("image", "Image", "overview"),
            new("caption", "Caption", "overview"), new("status", "Status", "overview"),
            new("aliases", "Aliases", "overview"), new("pronunciation", "Pronunciation", "pronunciation"),
            new("taxonomy", "Taxonomy", "taxonomy"), new("biology", "Biology", "biology"),
            new("appearance", "Appearance", "appearance"), new("origin", "Origin", "origin"),
            new("society", "Society", "society"), new("relations", "Relations", "relations"),
            new("continuity", "Continuity", "continuity")
        ], Groups: [
            new("overview", "Overview"), new("pronunciation", "Pronunciation"), new("taxonomy", "Taxonomy"),
            new("biology", "Biology"), new("appearance", "Appearance"), new("origin", "Origin"),
            new("society", "Society"), new("relations", "Relations"), new("continuity", "Continuity")
        ]),
        new MediaWikiInfoboxSchema("character", "Character"),
        new MediaWikiInfoboxSchema("divine", "Divine")
    ]);

    public bool TryGet(string type, out MediaWikiInfoboxSchema schema)
        => _schemas.TryGetValue(NormalizeToken(type), out schema!);

    public MediaWikiInfoboxSchema GetOrGeneric(string type)
        => TryGet(type, out var schema) ? schema : _schemas["generic"] with
        {
            Type = NormalizeToken(type) is { Length: > 0 } normalized ? normalized : "generic",
            Label = string.IsNullOrWhiteSpace(type) ? "Infobox" : type.Trim()
        };

    public string ToJson(bool indented = true)
        => JsonSerializer.Serialize(new MediaWikiInfoboxSchemaDocument(1, _schemas.Values.OrderBy(value => value.Type, StringComparer.Ordinal).ToList()), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented });

    public static MediaWikiInfoboxRegistry FromJson(string json, int maxCharacters = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > maxCharacters) throw new InvalidDataException("Infobox schema JSON exceeded its size budget.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("schemaVersion", out var version)
            || version.GetInt32() != 1
            || !document.RootElement.TryGetProperty("schemas", out var schemasElement))
            throw new InvalidDataException("Infobox schema JSON requires schemaVersion 1 and schemas.");
        var documentModel = JsonSerializer.Deserialize<MediaWikiInfoboxSchemaDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Infobox schema JSON did not contain schemas.");
        if (documentModel.Schemas.Count > 128) throw new InvalidDataException("Infobox schema count exceeded its bound.");
        return new(documentModel.Schemas);
    }

    private static MediaWikiInfoboxSchema Resolve(
        IReadOnlyDictionary<string, MediaWikiInfoboxSchema> raw,
        string type,
        IReadOnlyList<string> stack,
        int depth)
    {
        if (depth > 8) throw new ArgumentException("Infobox schema inheritance exceeded its depth bound.");
        if (stack.Contains(type, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Infobox schema inheritance cycle includes '{type}'.");
        var schema = raw[type];
        var parents = (schema.Extends ?? []).Concat(schema.Packs ?? []).Select(NormalizeToken).Where(value => value.Length > 0).ToList();
        var merged = new MediaWikiInfoboxSchema(type, schema.Label, schema.TitleField, schema.SubtitleField, schema.ImageField, schema.CaptionField,
            [], [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), schema.RenderUnknownFields);
        foreach (var parent in parents)
        {
            if (!raw.ContainsKey(parent)) throw new ArgumentException($"Infobox schema '{type}' references missing schema part '{parent}'.");
            merged = Merge(merged, Resolve(raw, parent, stack.Append(type).ToArray(), depth + 1));
        }
        return Merge(merged, schema with { Extends = null, Packs = null });
    }

    private static MediaWikiInfoboxSchema Merge(MediaWikiInfoboxSchema parent, MediaWikiInfoboxSchema child)
    {
        var fields = parent.EffectiveFields.Concat(child.EffectiveFields).GroupBy(field => MediaWikiInfoboxCompatibility.NormalizeField(field.Name), StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).ToList();
        var groups = parent.EffectiveGroups.Concat(child.EffectiveGroups).GroupBy(group => NormalizeToken(group.Name), StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).ToList();
        var aliases = new Dictionary<string, string>(parent.EffectiveAliases, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in child.EffectiveAliases) aliases[MediaWikiInfoboxCompatibility.NormalizeField(pair.Key)] = MediaWikiInfoboxCompatibility.NormalizeField(pair.Value);
        return child with { Fields = fields, Groups = groups, Aliases = aliases };
    }

    private static MediaWikiInfoboxSchema NormalizeSchema(MediaWikiInfoboxSchema schema)
    {
        if (schema.EffectiveFields.Count > 128 || schema.EffectiveGroups.Count > 64 || (schema.EffectiveAliases.Count > 256))
            throw new ArgumentException($"Infobox schema '{schema.Type}' exceeded its field, group, or alias bound.");
        var fields = schema.EffectiveFields.Select(field => field with { Name = MediaWikiInfoboxCompatibility.NormalizeField(field.Name), Group = field.Group is null ? null : NormalizeToken(field.Group) }).ToList();
        if (fields.Any(field => field.Name.Length == 0 || field.Label.Length > 256)) throw new ArgumentException($"Infobox schema '{schema.Type}' contains an invalid field.");
        if (fields.GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) throw new ArgumentException($"Infobox schema '{schema.Type}' contains duplicate fields.");
        var groups = schema.EffectiveGroups.Select(group => group with { Name = NormalizeToken(group.Name), Layout = group.Layout.Trim().ToLowerInvariant() }).ToList();
        if (groups.Any(group => group.Name.Length == 0 || group.Layout is not ("nested" or "horizontal"))) throw new ArgumentException($"Infobox schema '{schema.Type}' contains an invalid group layout.");
        if (groups.GroupBy(group => group.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) throw new ArgumentException($"Infobox schema '{schema.Type}' contains duplicate groups.");
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in schema.EffectiveAliases)
        {
            var alias = MediaWikiInfoboxCompatibility.NormalizeField(pair.Key);
            var target = MediaWikiInfoboxCompatibility.NormalizeField(pair.Value);
            if (alias.Length == 0 || target.Length == 0 || !aliases.TryAdd(alias, target)) throw new ArgumentException($"Infobox schema '{schema.Type}' contains invalid or duplicate aliases.");
        }
        return schema with { Fields = fields, Groups = groups, Aliases = aliases, TitleField = MediaWikiInfoboxCompatibility.NormalizeField(schema.TitleField), SubtitleField = schema.SubtitleField is null ? null : MediaWikiInfoboxCompatibility.NormalizeField(schema.SubtitleField), ImageField = schema.ImageField is null ? null : MediaWikiInfoboxCompatibility.NormalizeField(schema.ImageField), CaptionField = schema.CaptionField is null ? null : MediaWikiInfoboxCompatibility.NormalizeField(schema.CaptionField), Extends = schema.Extends?.Select(NormalizeToken).ToList(), Packs = schema.Packs?.Select(NormalizeToken).ToList() };
    }

    internal static string NormalizeToken(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 64));
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '-') builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            if (builder.Length == 64) break;
        }
        return builder.ToString().Trim('-');
    }
}

internal static class MediaWikiInfoboxCompatibility
{
    internal const string TokenPrefix = "\uE000CWINFOBOX:";
    internal const char TokenSuffix = '\uE001';

    internal static bool TryBuildModel(
        string type,
        IReadOnlyList<MediaWikiInfoboxArgument> arguments,
        MediaWikiInfoboxRegistry registry,
        int maxFields,
        int maxCharacters,
        out MediaWikiInfoboxRenderModel model,
        out string error)
    {
        model = null!;
        error = string.Empty;
        if (arguments.Count > maxFields) { error = "The infobox field count exceeded the configured budget."; return false; }
        var schema = registry.GetOrGeneric(type);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            var key = NormalizeField(argument.Name);
            if (key.Length == 0) continue;
            if (schema.EffectiveAliases.TryGetValue(key, out var alias)) key = NormalizeField(alias);
            if (!values.ContainsKey(key)) values[key] = argument.Value;
            if (values.Sum(pair => pair.Key.Length + pair.Value.Length) > maxCharacters)
            {
                error = "The infobox content exceeded the configured budget.";
                return false;
            }
        }

        var known = new HashSet<string>(schema.EffectiveFields.Select(field => NormalizeField(field.Name)), StringComparer.OrdinalIgnoreCase);
        var rendered = new List<MediaWikiInfoboxFieldValue>();
        var groups = schema.EffectiveGroups.Select(group => new MediaWikiInfoboxGroupValue(group.Name, group.Label, string.Equals(group.Layout, "horizontal", StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var field in schema.EffectiveFields)
        {
            var key = NormalizeField(field.Name);
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            var supplied = arguments.LastOrDefault(argument => string.Equals(NormalizeField(argument.Name), key, StringComparison.OrdinalIgnoreCase));
            var group = supplied?.Group is null ? field.Group : MediaWikiInfoboxRegistry.NormalizeToken(supplied.Group);
            var groupDefinition = groups.FirstOrDefault(candidate => string.Equals(candidate.Name, group, StringComparison.OrdinalIgnoreCase));
            rendered.Add(new(key, field.Label, value, group, supplied?.Horizontal == true || groupDefinition?.Horizontal == true, groupDefinition?.Label));
        }
        if (schema.RenderUnknownFields)
        {
            foreach (var pair in values)
            {
                if (known.Contains(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                rendered.Add(new(pair.Key, Humanize(pair.Key), pair.Value, "additional", false, "Additional"));
            }
        }

        string? Read(string? key) => key is not null && values.TryGetValue(NormalizeField(key), out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        var title = Read(schema.TitleField);
        var subtitle = Read(schema.SubtitleField);
        var image = Read(schema.ImageField);
        var caption = Read(schema.CaptionField);
        if (rendered.Any(field => string.Equals(field.Group, "additional", StringComparison.OrdinalIgnoreCase)) && !groups.Any(group => group.Name.Equals("additional", StringComparison.OrdinalIgnoreCase)))
            groups.Add(new("additional", "Additional", false));
        model = new(schema.Type, schema.Label, title, subtitle, image, caption, rendered, groups);
        return true;
    }

    internal static string Encode(MediaWikiInfoboxRenderModel model)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(model, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return TokenPrefix + Convert.ToBase64String(json).Replace('+', '-').Replace('/', '_').TrimEnd('=') + TokenSuffix;
    }

    internal static bool TryDecode(string value, out MediaWikiInfoboxRenderModel model)
    {
        model = null!;
        if (!value.StartsWith(TokenPrefix, StringComparison.Ordinal) || !value.EndsWith(TokenSuffix)) return false;
        var encoded = value[TokenPrefix.Length..^1].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        try
        {
            model = JsonSerializer.Deserialize<MediaWikiInfoboxRenderModel>(Convert.FromBase64String(encoded), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return model is not null && model.Type.Length <= 64 && model.Fields.Count <= 512;
        }
        catch (FormatException) { return false; }
        catch (JsonException) { return false; }
    }

    internal static string NormalizeField(string value)
    {
        var normalized = value.Trim().Replace('[', '.').Replace(']', '.');
        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>();
        foreach (var segment in segments)
        {
            var builder = new StringBuilder(Math.Min(segment.Length, 64));
            foreach (var character in segment.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character) || character is '_' or '-') builder.Append(character);
                else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
                if (builder.Length == 64) break;
            }
            if (builder.Length > 0) result.Add(builder.ToString().Trim('_'));
            if (result.Count == 8) break;
        }
        return string.Join('.', result.Where(value => value.Length > 0));
    }

    private static string Humanize(string value)
        => string.Join(' ', value.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
}

internal static class MediaWikiInfoboxLegacyAdapter
{
    private static readonly Regex InfoboxTag = new("<infobox(?:\\s[^>]*)?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const int MaxElements = 512;
    private const int MaxDepth = 16;
    private const int MaxAttributes = 64;

    internal static string Adapt(string source, MediaWikiInfoboxRegistry registry, int maxCharacters, Action<string, string>? diagnostic = null)
    {
        if (source.Length > maxCharacters || !InfoboxTag.IsMatch(source)) return source;
        try
        {
            var opening = InfoboxTag.Match(source);
            var closingStart = source.IndexOf("</infobox>", opening.Index + opening.Length, StringComparison.OrdinalIgnoreCase);
            if (closingStart < 0)
            {
                diagnostic?.Invoke("UnsupportedPortableInfoboxElement", "Legacy <infobox> markup was not closed and was left for the normal unsupported-tag path.");
                return source;
            }
            var closingEnd = closingStart + "</infobox>".Length;
            var fragment = source[opening.Index..closingEnd];
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = maxCharacters,
                MaxCharactersFromEntities = 0
            };
            if (!ValidateXmlEnvelope(fragment, settings, out var envelopeError))
            {
                diagnostic?.Invoke("PortableInfoboxLimitExceeded", envelopeError!);
                return source;
            }
            using var reader = XmlReader.Create(new StringReader(fragment), settings);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root is null || !root.Name.LocalName.Equals("infobox", StringComparison.OrdinalIgnoreCase)) return source;
            var elements = 0;
            var attributes = 0;
            if (!ValidateElement(root, null, 0, ref elements, ref attributes, out var unsupportedCode, out var unsupportedMessage))
            {
                diagnostic?.Invoke(unsupportedCode!, unsupportedMessage!);
                return source;
            }
            var type = root.Attribute("type")?.Value ?? root.Attribute("template")?.Value ?? "generic";
            var arguments = new List<MediaWikiInfoboxArgument>();
            foreach (var element in root.Descendants().Where(value => value.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase) || value.Name.LocalName.Equals("image", StringComparison.OrdinalIgnoreCase) || value.Name.LocalName.Equals("caption", StringComparison.OrdinalIgnoreCase) || value.Name.LocalName.Equals("data", StringComparison.OrdinalIgnoreCase)))
            {
                var key = element.Attribute("source")?.Value
                    ?? (element.Name.LocalName.Equals("caption", StringComparison.OrdinalIgnoreCase) ? "caption" : null)
                    ?? (element.Name.LocalName.Equals("image", StringComparison.OrdinalIgnoreCase) ? "image" : null);
                if (string.IsNullOrWhiteSpace(key)) continue;
                var value = element.Value.Trim();
                if (value.Length == 0) continue;
                var group = element.Ancestors().FirstOrDefault(value => value.Name.LocalName.Equals("group", StringComparison.OrdinalIgnoreCase));
                var header = group?.Elements().FirstOrDefault(value => value.Name.LocalName.Equals("header", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                var layout = group?.Attribute("layout")?.Value;
                arguments.Add(new(key, value, header, string.Equals(layout, "horizontal", StringComparison.OrdinalIgnoreCase)));
            }
            if (!MediaWikiInfoboxCompatibility.TryBuildModel(type, arguments, registry, 512, maxCharacters, out var model, out _)) return source;
            var token = MediaWikiInfoboxCompatibility.Encode(model);
            return source[..opening.Index] + token + source[closingEnd..];
        }
        catch (XmlException)
        {
            diagnostic?.Invoke("MalformedPortableInfobox", "Legacy <infobox> markup was not well-formed and was left for the normal unsupported-tag path.");
            return source;
        }
        catch (InvalidOperationException)
        {
            diagnostic?.Invoke("UnsafePortableInfobox", "Legacy <infobox> markup could not be safely adapted and was left unchanged.");
            return source;
        }
    }

    private static bool ValidateElement(XElement element, string? parentName, int depth, ref int elements, ref int attributes, out string? code, out string? message)
    {
        code = null;
        message = null;
        if (++elements > MaxElements || depth > MaxDepth || (attributes += element.Attributes().Count()) > MaxAttributes)
        {
            code = "PortableInfoboxLimitExceeded";
            message = "Legacy <infobox> markup exceeded the bounded XML element, depth, or attribute limit.";
            return false;
        }
        var name = element.Name.LocalName.ToLowerInvariant();
        var allowed = name switch
        {
            "infobox" => element.Attributes().All(attribute => attribute.Name.LocalName is "type" or "template"),
            "title" or "data" => element.Attributes().All(attribute => attribute.Name.LocalName == "source"),
            "image" or "caption" => element.Attributes().All(attribute => attribute.Name.LocalName == "source"),
            "group" => element.Attributes().All(attribute => attribute.Name.LocalName == "layout"),
            "label" => parentName == "data" && !element.HasAttributes,
            "header" => parentName == "group" && !element.HasAttributes,
            "default" or "format" => parentName == "data" && !element.HasAttributes,
            "audio" => parentName == "data" && element.Attributes().All(attribute => attribute.Name.LocalName == "source"),
            "panel" or "navigation" => false,
            _ => false
        };
        if (!allowed)
        {
            code = name is "panel" or "navigation" ? "UnsupportedPortableInfoboxConstruct" : "UnsupportedPortableInfoboxElement";
            message = $"PortableInfobox element or attribute '{name}' is not in the verified compatibility subset.";
            return false;
        }
        foreach (var child in element.Elements())
            if (!ValidateElement(child, name, depth + 1, ref elements, ref attributes, out code, out message)) return false;
        return true;
    }

    private static bool ValidateXmlEnvelope(string fragment, XmlReaderSettings settings, out string? error)
    {
        error = null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(fragment), settings);
            var elements = 0;
            var attributes = 0;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (++elements > MaxElements || reader.Depth > MaxDepth || (attributes += reader.AttributeCount) > MaxAttributes)
                    {
                        error = "Legacy <infobox> markup exceeded the bounded XML element, depth, or attribute limit.";
                        return false;
                    }
                }
            }
            return true;
        }
        catch (XmlException exception)
        {
            error = $"Legacy <infobox> XML was rejected: {exception.Message}";
            return false;
        }
    }
}
