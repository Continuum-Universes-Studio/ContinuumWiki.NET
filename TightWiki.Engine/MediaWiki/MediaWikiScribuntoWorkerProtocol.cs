using System.Text.Json;

namespace TightWiki.Engine.MediaWiki;

/// <summary>
/// The intentionally small protocol shared by the experimental host and its
/// disposable MoonSharp worker. The worker receives one request line and must
/// emit one response line; no diagnostic logging is permitted on stdout.
/// </summary>
public static class MediaWikiScribuntoWorkerProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxCorrelationIdCharacters = 64;
    public const int MaxIdentityCharacters = 512;
    public const int MaxFunctionCharacters = 256;
    public const int MaxDiagnosticCharacters = 1_024;
    public const int MaxModuleDependencyCount = 1_024;
    public const int MaxModuleDependencyCharacters = 512;

    public static readonly IReadOnlySet<string> SupportedCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "frame.args",
        "frame.title",
        "frame.parent",
        "module.require",
        "mw.loadData",
        "mw.loadJsonData",
        "mw.text",
        "mw.ustring",
        "mw.title",
        "mw.site",
        "mw.language",
        "mw.html",
        "cw.text",
        "cw.title",
        "cw.page",
        "cw.module",
        "cw.args"
    };

    public static readonly IReadOnlyList<string> DefaultCapabilities =
        // Keep the default request compatible with the deployed worker. New
        // compatibility surfaces are explicit opt-ins until that worker is
        // upgraded; SupportedCapabilities still validates those opt-ins.
        ["frame.args", "frame.title", "frame.parent", "module.require", "mw.loadData", "mw.loadJsonData", "mw.text", "mw.ustring"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string SerializeRequest(MediaWikiScribuntoWorkerRequest request)
        => JsonSerializer.Serialize(request, JsonOptions);

    public static string SerializeResponse(MediaWikiScribuntoWorkerResponse response)
        => JsonSerializer.Serialize(response, JsonOptions);

    public static bool TryParseRequest(
        string json,
        int maxPayloadCharacters,
        out MediaWikiScribuntoWorkerRequest? request,
        out string code,
        out string message)
    {
        request = null;
        code = "ProtocolError";
        message = "The worker request was invalid.";
        if (string.IsNullOrWhiteSpace(json))
        {
            code = "MalformedJson";
            message = "The worker request was empty.";
            return false;
        }
        if (json.Length > maxPayloadCharacters)
        {
            code = "InputLimitExceeded";
            message = "The worker request exceeded the configured input limit.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                code = "MalformedJson";
                message = "The worker request root must be an object.";
                return false;
            }

            if (document.RootElement.TryGetProperty("protocolVersion", out var versionElement)
                && versionElement.ValueKind == JsonValueKind.Number
                && versionElement.TryGetInt32(out var envelopeVersion)
                && envelopeVersion != CurrentVersion)
            {
                code = "UnsupportedProtocolVersion";
                message = "The worker protocol version is unsupported.";
                return false;
            }

            var properties = ReadProperties(document.RootElement, RequestProperties, out var propertyError);
            if (properties is null)
            {
                if (propertyError!.Contains("duplicate", StringComparison.OrdinalIgnoreCase)) code = "MalformedJson";
                message = propertyError!;
                return false;
            }

            if (!ReadInt(properties, "protocolVersion", out var protocolVersion)
                || protocolVersion != CurrentVersion)
            {
                code = "UnsupportedProtocolVersion";
                message = "The worker protocol version is unsupported.";
                return false;
            }
            if (!ReadString(properties, "correlationId", out var correlationId)
                || !IsSafeCorrelationId(correlationId))
            {
                code = "InvalidCorrelationId";
                message = "The worker correlation ID was invalid.";
                return false;
            }
            if (!ReadString(properties, "moduleTitle", out var moduleTitle) || moduleTitle.Length > MaxIdentityCharacters)
                return Invalid(out request, out code, out message, "InvalidModuleTitle", "The module title was invalid.");
            if (!ReadString(properties, "moduleSourceIdentity", out var moduleSourceIdentity) || moduleSourceIdentity.Length > MaxIdentityCharacters)
                return Invalid(out request, out code, out message, "InvalidSourceIdentity", "The module source identity was invalid.");
            if (!ReadString(properties, "sourceWikiIdentity", out var sourceWikiIdentity) || sourceWikiIdentity.Length > MaxIdentityCharacters)
                return Invalid(out request, out code, out message, "InvalidSourceIdentity", "The source wiki identity was invalid.");
            if (!ReadNullableString(properties, "sourceRevisionId", out var sourceRevisionId) || sourceRevisionId?.Length > MaxIdentityCharacters)
                return Invalid(out request, out code, out message, "InvalidSourceRevision", "The source revision identity was invalid.");
            if (!ReadString(properties, "source", out var source))
                return Invalid(out request, out code, out message, "InvalidSource", "The module source was invalid.");
            if (!ReadString(properties, "function", out var function)
                || function.Length == 0 || function.Length > MaxFunctionCharacters || !IsSafeFunction(function))
                return Invalid(out request, out code, out message, "InvalidFunction", "The exported function name was invalid.");
            if (!ReadStringArray(properties, "positionalArguments", out var positionalArguments))
                return Invalid(out request, out code, out message, "InvalidArguments", "The positional arguments were invalid.");
            if (!ReadStringDictionary(properties, "namedArguments", out var namedArguments, out var namedError))
                return Invalid(out request, out code, out message, "DuplicateOrInvalidNamedArgument", namedError!);
            if (!ReadBudget(properties, out var budget, out var budgetError))
                return Invalid(out request, out code, out message, "InvalidBudget", budgetError!);
            if (!ReadStringArray(properties, "capabilities", out var capabilities))
                return Invalid(out request, out code, out message, "InvalidCapabilities", "The capability list was invalid.");
            if (!ReadNullableString(properties, "frameTitle", out var frameTitle)
                || frameTitle is not null && frameTitle.Length > MaxIdentityCharacters)
                return Invalid(out request, out code, out message, "InvalidFrameMetadata", "The frame title was invalid.");
            if (!ReadNullableString(properties, "parentFrameTitle", out var parentFrameTitle)
                || parentFrameTitle is not null && parentFrameTitle.Length > MaxIdentityCharacters)
                return Invalid(out request, out code, out message, "InvalidFrameMetadata", "The parent frame title was invalid.");
            if (!ReadNullableString(properties, "parentFrameSourceRevisionId", out var parentFrameSourceRevisionId)
                || parentFrameSourceRevisionId is not null && parentFrameSourceRevisionId.Length > MaxIdentityCharacters)
                return Invalid(out request, out code, out message, "InvalidFrameMetadata", "The parent frame revision was invalid.");
            if (!ReadNullableStringArray(properties, "invocationChain", out var invocationChain))
                return Invalid(out request, out code, out message, "InvalidFrameMetadata", "The invocation chain was invalid.");
            if (invocationChain.Any(value => value.Length == 0 || value.Length > MaxIdentityCharacters))
                return Invalid(out request, out code, out message, "InvalidFrameMetadata", "The invocation chain contained an invalid title.");
            if (parentFrameTitle is not null && frameTitle is null)
                return Invalid(out request, out code, out message, "InvalidParentFrame", "A parent frame cannot be supplied without a current frame title.");
            if (parentFrameSourceRevisionId is not null && parentFrameTitle is null)
                return Invalid(out request, out code, out message, "InvalidParentFrame", "A parent frame revision cannot be supplied without a parent frame title.");
            if (!ReadModuleDependencies(properties, "dependencies", out var dependencies, out var dependencyError))
                return Invalid(out request, out code, out message, dependencyError!.Value.Code, dependencyError.Value.Message);
            if (!ReadNullableStringArray(properties, "rootDependencies", out var rootDependencies))
                return Invalid(out request, out code, out message, "InvalidDependencyBundle", "The root dependency list was invalid.");

            var capabilitySet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capability in capabilities)
            {
                if (!capabilitySet.Add(capability))
                    return Invalid(out request, out code, out message, "DuplicateCapability", "The capability list contained a duplicate.");
                if (!SupportedCapabilities.Contains(capability))
                    return Invalid(out request, out code, out message, "UnsupportedCapability", "The request contained an unsupported capability.");
            }

            if (positionalArguments.Count + namedArguments.Count > budget.MaxArgumentCount)
                return Invalid(out request, out code, out message, "ArgumentCountLimitExceeded", "The invocation argument count exceeded the configured limit.");
            var argumentCharacters = positionalArguments.Sum(value => value.Length)
                + namedArguments.Sum(pair => pair.Key.Length + pair.Value.Length);
            if (positionalArguments.Any(value => value.Length > budget.MaxIndividualArgumentCharacters)
                || namedArguments.Any(pair => pair.Key.Length + pair.Value.Length > budget.MaxIndividualArgumentCharacters))
                return Invalid(out request, out code, out message, "IndividualArgumentLimitExceeded", "An invocation argument exceeded the configured individual limit.");
            if (argumentCharacters > budget.MaxArgumentCharacters)
                return Invalid(out request, out code, out message, "ArgumentLimitExceeded", "The invocation arguments exceeded the configured limit.");
            if (source.Length > budget.MaxSourceCharacters)
                return Invalid(out request, out code, out message, "SourceLimitExceeded", "The module source exceeded the configured limit.");

            if (!ValidateModuleBundle(
                    moduleTitle, sourceWikiIdentity, rootDependencies, dependencies, budget.MaxDependencySourceCharacters,
                    out var bundleCode, out var bundleMessage))
                return Invalid(out request, out code, out message, bundleCode!, bundleMessage!);

            request = new MediaWikiScribuntoWorkerRequest(
                protocolVersion, correlationId, moduleTitle, moduleSourceIdentity, sourceWikiIdentity,
                sourceRevisionId, source, function, positionalArguments, namedArguments, budget, capabilities,
                dependencies, rootDependencies, frameTitle, parentFrameTitle, parentFrameSourceRevisionId, invocationChain);
            code = string.Empty;
            message = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            code = "MalformedJson";
            message = "The worker request was not valid JSON.";
            return false;
        }
        catch (ArgumentException)
        {
            code = "ProtocolError";
            message = "The worker request contained an invalid value.";
            return false;
        }
    }

    public static bool TryParseResponse(
        string json,
        int maxPayloadCharacters,
        string expectedCorrelationId,
        out MediaWikiScribuntoWorkerResponse? response,
        out string code,
        out string message)
    {
        response = null;
        code = "ProtocolError";
        message = "The worker response was invalid.";
        if (string.IsNullOrWhiteSpace(json))
        {
            code = "EmptyResponse";
            message = "The worker returned no response.";
            return false;
        }
        if (json.Length > maxPayloadCharacters)
        {
            code = "OutputLimitExceeded";
            message = "The worker response exceeded the configured output limit.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var properties = ReadProperties(document.RootElement, ResponseProperties, out var propertyError);
            if (properties is null) { if (propertyError!.Contains("duplicate", StringComparison.OrdinalIgnoreCase)) code = "MalformedJson"; message = propertyError; return false; }
            if (!ReadInt(properties, "protocolVersion", out var protocolVersion) || protocolVersion != CurrentVersion)
            {
                code = "UnsupportedProtocolVersion";
                message = "The worker response protocol version was unsupported.";
                return false;
            }
            if (!ReadString(properties, "correlationId", out var correlationId)
                || !string.Equals(correlationId, expectedCorrelationId, StringComparison.Ordinal))
            {
                code = "CorrelationMismatch";
                message = "The worker response correlation ID did not match the request.";
                return false;
            }
            if (!ReadString(properties, "status", out var status)
                || !ResponseStatuses.Contains(status))
            {
                message = "The worker response status was invalid.";
                return false;
            }
            if (!ReadNullableString(properties, "text", out var text))
            {
                message = "The worker response text field was invalid.";
                return false;
            }
            if (!ReadDiagnostics(properties, "diagnostics", out var diagnostics))
            {
                message = "The worker response diagnostics were invalid.";
                return false;
            }
            if (!ReadNullableString(properties, "resourceLimitOutcome", out var resourceLimitOutcome))
            {
                message = "The worker resource outcome was invalid.";
                return false;
            }
            if (!ReadDependencyReports(properties, "dependencies", out var dependencies))
            {
                message = "The worker response dependencies were invalid.";
                return false;
            }
            response = new MediaWikiScribuntoWorkerResponse(protocolVersion, correlationId, status, text, diagnostics, resourceLimitOutcome, dependencies);
            code = string.Empty;
            message = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            code = "MalformedJson";
            message = "The worker response was not valid JSON.";
            return false;
        }
    }

    private static bool Invalid(
        out MediaWikiScribuntoWorkerRequest? request,
        out string code,
        out string message,
        string invalidCode,
        string invalidMessage)
    {
        request = null;
        code = invalidCode;
        message = invalidMessage;
        return false;
    }

    private static Dictionary<string, JsonElement>? ReadProperties(
        JsonElement root,
        IReadOnlySet<string> allowed,
        out string? error)
    {
        error = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "The protocol root must be an object.";
            return null;
        }
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                error = "The protocol contained an unknown field.";
                return null;
            }
            if (!properties.TryAdd(property.Name, property.Value))
            {
                error = "The protocol contained a duplicate field.";
                return null;
            }
        }
        foreach (var required in allowed)
        {
            if (!properties.ContainsKey(required))
            {
                error = "The protocol omitted a required field.";
                return null;
            }
        }
        return properties;
    }

    private static bool ReadInt(Dictionary<string, JsonElement> properties, string name, out int value)
    {
        value = 0;
        return properties[name].ValueKind == JsonValueKind.Number && properties[name].TryGetInt32(out value);
    }

    private static bool ReadLong(Dictionary<string, JsonElement> properties, string name, out long value)
    {
        value = 0;
        return properties[name].ValueKind == JsonValueKind.Number && properties[name].TryGetInt64(out value);
    }

    private static bool ReadString(Dictionary<string, JsonElement> properties, string name, out string value)
    {
        value = string.Empty;
        return properties[name].ValueKind == JsonValueKind.String
            && (value = properties[name].GetString()!) is not null;
    }

    private static bool ReadNullableString(Dictionary<string, JsonElement> properties, string name, out string? value)
    {
        if (properties[name].ValueKind == JsonValueKind.Null) { value = null; return true; }
        if (properties[name].ValueKind != JsonValueKind.String) { value = null; return false; }
        value = properties[name].GetString();
        return true;
    }

    private static bool ReadStringArray(Dictionary<string, JsonElement> properties, string name, out List<string> values)
    {
        values = [];
        if (properties[name].ValueKind != JsonValueKind.Array) return false;
        foreach (var item in properties[name].EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            values.Add(item.GetString()!);
        }
        return true;
    }

    private static bool ReadNullableStringArray(Dictionary<string, JsonElement> properties, string name, out List<string> values)
    {
        values = [];
        if (properties[name].ValueKind == JsonValueKind.Null) return true;
        return ReadStringArray(properties, name, out values);
    }

    private static bool ReadModuleDependencies(
        Dictionary<string, JsonElement> properties,
        string name,
        out List<MediaWikiScribuntoWorkerModuleDependency> dependencies,
        out (string Code, string Message)? error)
    {
        dependencies = [];
        error = null;
        var element = properties[name];
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Array)
        {
            error = ("InvalidDependencyBundle", "The dependency bundle must be an array or null.");
            return false;
        }
        if (element.GetArrayLength() > MaxModuleDependencyCount)
        {
            error = ("DependencyCountLimitExceeded", "The dependency bundle exceeded the configured module count limit.");
            return false;
        }
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = ("InvalidDependencyBundle", "A dependency entry was not an object.");
                return false;
            }
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!ModuleDependencyProperties.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
                {
                    error = ("InvalidDependencyBundle", "A dependency entry contained an unknown or duplicate field.");
                    return false;
                }
            }
            foreach (var required in ModuleDependencyProperties)
            {
                if (!values.ContainsKey(required))
                {
                    error = ("InvalidDependencyBundle", "A dependency entry omitted a required field.");
                    return false;
                }
            }
            if (!ReadStringValue(values, "sourceWikiIdentity", out var sourceWiki)
                || !ReadStringValue(values, "canonicalModuleTitle", out var title)
                || !ReadStringValue(values, "sourcePageId", out var pageId)
                || !ReadStringValue(values, "sourceRevisionId", out var revisionId)
                || !ReadStringValue(values, "contentModel", out var contentModel)
                || !ReadStringValue(values, "sourceSha256", out var sha256)
                || !ReadStringValue(values, "source", out var source)
                || !ReadStringArrayValue(values, "dependencies", out var relationshipNames))
            {
                error = ("InvalidDependencyBundle", "A dependency entry contained an invalid value.");
                return false;
            }
            dependencies.Add(new(sourceWiki, title, pageId, revisionId, contentModel, sha256, source, relationshipNames));
        }
        return true;
    }

    private static bool ReadStringValue(Dictionary<string, JsonElement> values, string name, out string value)
    {
        value = string.Empty;
        return values[name].ValueKind == JsonValueKind.String && (value = values[name].GetString()!) is not null;
    }

    private static bool ReadStringArrayValue(Dictionary<string, JsonElement> values, string name, out List<string> result)
    {
        result = [];
        if (values[name].ValueKind != JsonValueKind.Array) return false;
        foreach (var value in values[name].EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) return false;
            result.Add(value.GetString()!);
        }
        return true;
    }

    private static bool ReadDependencyReports(
        Dictionary<string, JsonElement> properties,
        string name,
        out List<MediaWikiScribuntoWorkerDependencyReport> dependencies)
    {
        dependencies = [];
        var element = properties[name];
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Array) return false;
        if (element.GetArrayLength() > MaxModuleDependencyCount) return false;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return false;
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
                if (!DependencyReportProperties.Contains(property.Name) || !values.TryAdd(property.Name, property.Value)) return false;
            foreach (var required in DependencyReportProperties)
                if (!values.ContainsKey(required)) return false;
            if (!ReadStringValue(values, "sourceWikiIdentity", out var sourceWiki)
                || !ReadStringValue(values, "canonicalModuleTitle", out var title)
                || !ReadStringValue(values, "sourcePageId", out var pageId)
                || !ReadStringValue(values, "sourceRevisionId", out var revisionId)
                || !ReadStringValue(values, "sourceSha256", out var sha256)
                || !ReadStringValue(values, "status", out var status)
                || !IsSha256(sha256)) return false;
            dependencies.Add(new(sourceWiki, title, pageId, revisionId, sha256, status));
        }
        return true;
    }

    private static bool ValidateModuleBundle(
        string rootTitle,
        string sourceWikiIdentity,
        IReadOnlyList<string> rootDependencies,
        IReadOnlyList<MediaWikiScribuntoWorkerModuleDependency> dependencies,
        long maxSourceCharacters,
        out string? code,
        out string? message)
    {
        code = null;
        message = null;
        if (!MediaWikiModuleTitle.TryParse(rootTitle, out var rootModule) || rootModule.NamespaceId != 828
            || !string.Equals(rootModule.CanonicalTitle, rootTitle, StringComparison.Ordinal))
            return InvalidBundle("InvalidModuleTitle", "The root module title was not canonical.", out code, out message);

        var byTitle = new Dictionary<string, MediaWikiScribuntoWorkerModuleDependency>(StringComparer.OrdinalIgnoreCase);
        var sourceIdentities = new HashSet<string>(StringComparer.Ordinal);
        long sourceCharacters = 0;
        foreach (var dependency in dependencies)
        {
            if (dependency.SourceWikiIdentity.Length == 0 || !string.Equals(dependency.SourceWikiIdentity, sourceWikiIdentity, StringComparison.Ordinal))
                return InvalidBundle("CrossWikiModuleDependency", "A dependency did not belong to the invocation source wiki.", out code, out message);
            if (!MediaWikiModuleTitle.TryParse(dependency.CanonicalModuleTitle, out var module) || module.NamespaceId != 828
                || !string.Equals(module.CanonicalTitle, dependency.CanonicalModuleTitle, StringComparison.Ordinal))
                return InvalidBundle("InvalidDependencyModuleTitle", "A dependency module title was not canonical or was path-like.", out code, out message);
            if (dependency.SourcePageId.Length == 0 || dependency.SourceRevisionId.Length == 0)
                return InvalidBundle("InvalidDependencyIdentity", "A dependency omitted its source page or revision identity.", out code, out message);
            if (dependency.ContentModel.Length == 0 || !IsSupportedContentModel(dependency.ContentModel))
                return InvalidBundle("UnsupportedContentModel", "A dependency used an unsupported content model.", out code, out message);
            if (dependency.Source.Length > maxSourceCharacters)
                return InvalidBundle("DependencySourceLimitExceeded", "A dependency source exceeded the configured source limit.", out code, out message);
            if (!IsSha256(dependency.SourceSha256)
                || !string.Equals(dependency.SourceSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dependency.Source))), StringComparison.OrdinalIgnoreCase))
                return InvalidBundle("DependencySourceHashMismatch", "A dependency source checksum did not match its content.", out code, out message);
            if (!byTitle.TryAdd(module.CanonicalTitle, dependency))
                return InvalidBundle("DuplicateModuleIdentity", "The dependency bundle contained duplicate or ambiguous module identities.", out code, out message);
            if (!sourceIdentities.Add($"{dependency.SourceWikiIdentity}\u001f{dependency.SourcePageId}\u001f{dependency.SourceRevisionId}"))
                return InvalidBundle("DuplicateSourceIdentity", "The dependency bundle reused a source page/revision identity.", out code, out message);
            sourceCharacters = checked(sourceCharacters + dependency.Source.Length);
            if (sourceCharacters > maxSourceCharacters)
                return InvalidBundle("DependencySourceLimitExceeded", "The dependency bundle exceeded the configured source budget.", out code, out message);
        }

        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependencyName in rootDependencies)
        {
            if (!TryCanonicalModuleName(dependencyName, out var canonical))
                return InvalidBundle("InvalidDependencyReference", "A root dependency reference was invalid.", out code, out message);
            var added = rootNames.Add(canonical);
            if (!added || !byTitle.ContainsKey(canonical))
                return InvalidBundle(!added ? "DuplicateDependencyReference" : "MissingRequiredDependency", "A root dependency reference was duplicated or missing from the bundle.", out code, out message);
        }
        foreach (var dependency in dependencies)
        {
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependencyName in dependency.Dependencies)
            {
                if (!TryCanonicalModuleName(dependencyName, out var canonical))
                    return InvalidBundle("InvalidDependencyReference", "A module dependency reference was invalid.", out code, out message);
                var added = references.Add(canonical);
                if (!added || !byTitle.ContainsKey(canonical))
                    return InvalidBundle(!added ? "DuplicateDependencyReference" : "MissingRequiredDependency", "A module dependency reference was duplicated or missing from the bundle.", out code, out message);
            }
        }

        var graph = byTitle.ToDictionary(pair => pair.Key, pair => pair.Value.Dependencies.Select(CanonicalOrEmpty).Where(value => value.Length > 0).ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var rootDependency in rootNames) graph["__root__"] = rootNames.ToArray();
        if (HasCycle("__root__", graph, [], new(StringComparer.OrdinalIgnoreCase)))
            return InvalidBundle("CyclicModuleDependency", "The dependency bundle contained a circular module import.", out code, out message);
        foreach (var module in graph.Keys.Where(key => key != "__root__"))
            if (HasCycle(module, graph, [], new(StringComparer.OrdinalIgnoreCase)))
                return InvalidBundle("CyclicModuleDependency", "The dependency bundle contained a circular module import.", out code, out message);
        return true;
    }

    private static string CanonicalOrEmpty(string value) => TryCanonicalModuleName(value, out var canonical) ? canonical : string.Empty;

    private static bool HasCycle(string node, IReadOnlyDictionary<string, string[]> graph, IReadOnlyList<string> path, HashSet<string> visiting)
    {
        if (visiting.Contains(node)) return true;
        if (path.Contains(node, StringComparer.OrdinalIgnoreCase)) return true;
        if (!graph.TryGetValue(node, out var next)) return false;
        visiting.Add(node);
        foreach (var child in next)
            if (HasCycle(child, graph, path.Append(node).ToArray(), visiting)) return true;
        visiting.Remove(node);
        return false;
    }

    private static bool TryCanonicalModuleName(string value, out string canonical)
    {
        canonical = string.Empty;
        return MediaWikiModuleTitle.TryParse(value, out var module)
            && module.NamespaceId == 828
            && (canonical = module.CanonicalTitle).Length > 0;
    }

    private static bool IsSupportedContentModel(string value)
        => value.Equals("Scribunto", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Scribunto/Lua", StringComparison.OrdinalIgnoreCase)
            || value.Equals("lua", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Json", StringComparison.OrdinalIgnoreCase)
            || value.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || value.Equals("json", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(character => Uri.IsHexDigit(character));

    private static bool InvalidBundle(string invalidCode, string invalidMessage, out string? code, out string? message)
    {
        code = invalidCode;
        message = invalidMessage;
        return false;
    }

    private static bool ReadStringDictionary(
        Dictionary<string, JsonElement> properties,
        string name,
        out Dictionary<string, string> values,
        out string? error)
    {
        values = new(StringComparer.Ordinal);
        error = null;
        if (properties[name].ValueKind != JsonValueKind.Object)
        {
            error = "The named arguments must be an object.";
            return false;
        }
        foreach (var item in properties[name].EnumerateObject())
        {
            if (item.Name.Length == 0 || item.Value.ValueKind != JsonValueKind.String)
            {
                error = "Named argument keys and values must be non-empty strings.";
                return false;
            }
            if (values.Keys.Any(key => string.Equals(key, item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                error = "The named arguments contained duplicate or ambiguous keys.";
                return false;
            }
            values.Add(item.Name, item.Value.GetString()!);
        }
        return true;
    }

    private static bool ReadBudget(
        Dictionary<string, JsonElement> properties,
        out MediaWikiScribuntoWorkerBudget budget,
        out string? error)
    {
        budget = default!;
        error = null;
        var element = properties["budget"];
        if (element.ValueKind != JsonValueKind.Object) { error = "The budget must be an object."; return false; }
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!BudgetProperties.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
            { error = "The budget contained an unknown or duplicate field."; return false; }
        }
        foreach (var required in BudgetProperties)
        {
            if (!values.ContainsKey(required)) { error = "The budget omitted a required field."; return false; }
        }
        var names = new[] { "maxSourceCharacters", "maxArgumentCount", "maxArgumentCharacters", "maxIndividualArgumentCharacters", "maxResultCharacters", "maxDependencyDepth", "maxInputCharacters", "maxOutputCharacters" };
        var ints = new int[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            if (values[names[index]].ValueKind != JsonValueKind.Number || !values[names[index]].TryGetInt32(out ints[index]) || ints[index] < 1)
            { error = "The budget contained a non-positive integer."; return false; }
        }
        if (values["maxInstructionCount"].ValueKind != JsonValueKind.Number || !values["maxInstructionCount"].TryGetInt64(out var instructions) || instructions is < 1 or > 100_000_000
            || values["maxDependencySourceCharacters"].ValueKind != JsonValueKind.Number || !values["maxDependencySourceCharacters"].TryGetInt64(out var dependencySource) || dependencySource is < 1 or > 50_000_000
            || values["maxMemoryBytes"].ValueKind != JsonValueKind.Number || !values["maxMemoryBytes"].TryGetInt64(out var memoryBytes) || memoryBytes < 1
            || values["maxExecutionTimeMilliseconds"].ValueKind != JsonValueKind.Number || !values["maxExecutionTimeMilliseconds"].TryGetInt32(out var executionTimeMilliseconds) || executionTimeMilliseconds < 1)
        { error = "The budget contained an invalid long integer."; return false; }
        if (ints[4] > 200_000 || ints[6] > 2_000_000 || ints[7] > 250_000)
        { error = "The budget exceeded the worker protocol ceiling."; return false; }
        budget = new(
            ints[0], ints[1], ints[2], ints[3], ints[4], instructions, ints[5], dependencySource,
            ints[6], ints[7], memoryBytes, executionTimeMilliseconds);
        return true;
    }

    private static bool ReadDiagnostics(Dictionary<string, JsonElement> properties, string name, out List<MediaWikiScribuntoWorkerDiagnostic> diagnostics)
    {
        diagnostics = [];
        if (properties[name].ValueKind != JsonValueKind.Array) return false;
        foreach (var item in properties[name].EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return false;
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!DiagnosticProperties.Contains(property.Name) || !values.TryAdd(property.Name, property.Value)) return false;
            }
            if (!values.TryGetValue("code", out var code) || code.ValueKind != JsonValueKind.String
                || !values.TryGetValue("message", out var message) || message.ValueKind != JsonValueKind.String
                || !values.TryGetValue("severity", out var severity) || severity.ValueKind != JsonValueKind.String)
                return false;
            var codeText = code.GetString()!;
            var messageText = message.GetString()!;
            var severityText = severity.GetString()!;
            if (codeText.Length == 0 || codeText.Length > MaxDiagnosticCharacters
                || messageText.Length > MaxDiagnosticCharacters
                || severityText is not ("Information" or "Warning" or "Error")) return false;
            diagnostics.Add(new(codeText, messageText, severityText));
        }
        return true;
    }

    private static bool IsSafeCorrelationId(string value)
        => value.Length is > 0 and <= MaxCorrelationIdCharacters
            && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static bool IsSafeFunction(string value)
        => (char.IsLetter(value[0]) || value[0] == '_')
            && value.All(character => char.IsLetterOrDigit(character) || character is '_' or ':');

    private static readonly IReadOnlySet<string> RequestProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "protocolVersion", "correlationId", "moduleTitle", "moduleSourceIdentity", "sourceWikiIdentity", "sourceRevisionId",
        "source", "function", "positionalArguments", "namedArguments", "budget", "capabilities", "dependencies",
        "rootDependencies", "frameTitle", "parentFrameTitle", "parentFrameSourceRevisionId", "invocationChain"
    };

    private static readonly IReadOnlySet<string> ResponseProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "protocolVersion", "correlationId", "status", "text", "diagnostics", "resourceLimitOutcome", "dependencies"
    };

    private static readonly IReadOnlySet<string> BudgetProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "maxSourceCharacters", "maxArgumentCount", "maxArgumentCharacters", "maxIndividualArgumentCharacters",
        "maxResultCharacters", "maxInstructionCount", "maxDependencyDepth", "maxDependencySourceCharacters",
        "maxInputCharacters", "maxOutputCharacters", "maxMemoryBytes", "maxExecutionTimeMilliseconds"
    };

    private static readonly IReadOnlySet<string> DiagnosticProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "code", "message", "severity"
    };

    private static readonly IReadOnlySet<string> ModuleDependencyProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "sourceWikiIdentity", "canonicalModuleTitle", "sourcePageId", "sourceRevisionId", "contentModel", "sourceSha256", "source", "dependencies"
    };

    private static readonly IReadOnlySet<string> DependencyReportProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "sourceWikiIdentity", "canonicalModuleTitle", "sourcePageId", "sourceRevisionId", "sourceSha256", "status"
    };

    private static readonly IReadOnlySet<string> ResponseStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Succeeded", "Failed", "ResourceLimit", "ProtocolError", "SandboxUnavailable", "Terminated"
    };
}

public sealed record MediaWikiScribuntoWorkerBudget(
    int MaxSourceCharacters,
    int MaxArgumentCount,
    int MaxArgumentCharacters,
    int MaxIndividualArgumentCharacters,
    int MaxResultCharacters,
    long MaxInstructionCount,
    int MaxDependencyDepth,
    long MaxDependencySourceCharacters,
    int MaxInputCharacters,
    int MaxOutputCharacters,
    long MaxMemoryBytes,
    int MaxExecutionTimeMilliseconds);

public sealed record MediaWikiScribuntoWorkerRequest(
    int ProtocolVersion,
    string CorrelationId,
    string ModuleTitle,
    string ModuleSourceIdentity,
    string SourceWikiIdentity,
    string? SourceRevisionId,
    string Source,
    string Function,
    IReadOnlyList<string> PositionalArguments,
    IReadOnlyDictionary<string, string> NamedArguments,
    MediaWikiScribuntoWorkerBudget Budget,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<MediaWikiScribuntoWorkerModuleDependency>? Dependencies = null,
    IReadOnlyList<string>? RootDependencies = null,
    string? FrameTitle = null,
    string? ParentFrameTitle = null,
    string? ParentFrameSourceRevisionId = null,
    IReadOnlyList<string>? InvocationChain = null);

public sealed record MediaWikiScribuntoWorkerModuleDependency(
    string SourceWikiIdentity,
    string CanonicalModuleTitle,
    string SourcePageId,
    string SourceRevisionId,
    string ContentModel,
    string SourceSha256,
    string Source,
    IReadOnlyList<string> Dependencies);

public sealed record MediaWikiScribuntoWorkerDependencyReport(
    string SourceWikiIdentity,
    string CanonicalModuleTitle,
    string SourcePageId,
    string SourceRevisionId,
    string SourceSha256,
    string Status);

public sealed record MediaWikiScribuntoWorkerDiagnostic(string Code, string Message, string Severity = "Error");

public sealed record MediaWikiScribuntoWorkerResponse(
    int ProtocolVersion,
    string CorrelationId,
    string Status,
    string? Text,
    IReadOnlyList<MediaWikiScribuntoWorkerDiagnostic> Diagnostics,
    string? ResourceLimitOutcome,
    IReadOnlyList<MediaWikiScribuntoWorkerDependencyReport>? Dependencies = null);
