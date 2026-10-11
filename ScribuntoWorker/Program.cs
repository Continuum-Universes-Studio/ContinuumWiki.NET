using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using TightWiki.Engine.MediaWiki;

namespace ContinuumWiki.ScribuntoWorker;

internal static class Program
{
    private const int AbsoluteInputLimit = 2_000_000;
    private const int AbsoluteOutputLimit = 250_000;

    public static int Main()
    {
        var correlationId = string.Empty;
        try
        {
            var line = ReadBoundedLine(AbsoluteInputLimit, out var lineError);
            if (line is null)
                return Write(Response("ProtocolError", correlationId, lineError ?? "The worker received no request.", lineError is null ? "MalformedJson" : "InputLimitExceeded"));
            var trailing = ReadBoundedText(4_096, out var trailingOverflow);
            if (trailingOverflow || !string.IsNullOrWhiteSpace(trailing))
                return Write(Response("ProtocolError", correlationId, "The worker received an unexpected message sequence.", "UnexpectedMessageSequence"));

            if (!MediaWikiScribuntoWorkerProtocol.TryParseRequest(line, AbsoluteInputLimit, out var request, out var code, out var message))
            {
                correlationId = TryGetCorrelationId(line);
                return Write(Response("ProtocolError", correlationId, message, code));
            }

            correlationId = request!.CorrelationId;
            return Write(Execute(request));
        }
        catch (Exception)
        {
            return Write(Response("Failed", correlationId, "The Scribunto worker failed without exposing host details.", "WorkerFailure"));
        }
    }

    private static MediaWikiScribuntoWorkerResponse Execute(MediaWikiScribuntoWorkerRequest request)
    {
        var script = new Script(CoreModules.Preset_HardSandbox);
        script.Options.DebugPrint = _ => { };
        script.Options.DebugInput = _ => throw new ScriptRuntimeException("Input is unavailable in the Scribunto worker.");
        var state = new ExecutionState(script, request);
        try
        {
            RegisterRestrictedApi(script, state, request.Capabilities);
            var module = ExecuteLuaModule(request.ModuleTitle, request.Source, "Scribunto", state, immutable: false);
            if (!module.Success) return Failure(request.CorrelationId, module.ErrorCode!, module.ErrorMessage!);
            if (module.Value!.Type != DataType.Table) return Failure(request.CorrelationId, "ModuleResultInvalid", "The module did not return a table.");

            var exported = module.Value.Table.Get(request.Function);
            if (exported.Type != DataType.Function) return Failure(request.CorrelationId, "ExportNotFound", "The requested module export was not found.");

            var frame = new Table(script);
            var arguments = new Table(script);
            for (var index = 0; index < request.PositionalArguments.Count; index++)
                arguments.Set(index + 1, DynValue.NewString(request.PositionalArguments[index]));
            foreach (var pair in request.NamedArguments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                arguments.Set(pair.Key, DynValue.NewString(pair.Value));
            frame.Set("args", DynValue.NewTable(arguments));
            if (request.Capabilities.Contains("frame.title", StringComparer.Ordinal))
                frame.Set("getTitle", DynValue.NewCallback((_, _) => request.FrameTitle is null ? DynValue.Nil : DynValue.NewString(request.FrameTitle)));
            if (request.Capabilities.Contains("frame.parent", StringComparer.Ordinal))
            {
                frame.Set("getParent", DynValue.NewCallback((_, _) =>
                {
                    if (request.ParentFrameTitle is null) return DynValue.Nil;
                    var parent = new Table(script);
                    parent.Set("getTitle", DynValue.NewCallback((_, _) => DynValue.NewString(request.ParentFrameTitle)));
                    parent.Set("args", DynValue.NewTable(new Table(script)));
                    return DynValue.NewTable(parent);
                }));
            }
            if (request.Capabilities.Contains("frame.callParserFunction", StringComparer.Ordinal))
            {
                frame.Set("callParserFunction", DynValue.NewCallback((_, _) =>
                    throw new ScriptRuntimeException("The bounded parser-function bridge rejected the requested parser function; Cargo and extension parser functions are not enabled.")));
            }

            var result = ResumeBounded(state, exported, DynValue.NewTable(frame));
            if (!result.Success) return Failure(request.CorrelationId, result.ErrorCode!, result.ErrorMessage!);
            var text = result.Value!.Type == DataType.Nil ? string.Empty : result.Value.CastToString();
            if (text.Length > request.Budget.MaxResultCharacters)
                return ResourceLimit(request.CorrelationId, "ResultLimitExceeded", "The module result exceeded the configured limit.");
            return new(MediaWikiScribuntoWorkerProtocol.CurrentVersion, request.CorrelationId, "Succeeded", text,
                [new("ScribuntoWorkerCompleted", "The MoonSharp invocation completed.", "Information")], null,
                state.ExecutedModules.Values.ToArray());
        }
        catch (Exception exception)
        {
            var (code, message) = ClassifyException(exception);
            return Failure(request.CorrelationId, code, message);
        }
    }

    private static BoundedValue ExecuteLuaModule(
        string canonicalTitle,
        string source,
        string contentModel,
        ExecutionState state,
        bool immutable)
    {
        var cacheKey = (immutable ? "data:" : "require:") + canonicalTitle;
        if (state.ModuleCache.TryGetValue(cacheKey, out var cached)) return new(true, cached, null, null);
        if (state.Loading.Contains(canonicalTitle)) return new(false, null, "CircularModuleDependency", "The module dependency graph contained a circular import.");
        state.Loading.Add(canonicalTitle);
        if (state.Loading.Count - 1 > state.Request.Budget.MaxDependencyDepth)
        {
            state.Loading.Remove(canonicalTitle);
            return new(false, null, "DependencyDepthExceeded", "The module dependency depth exceeded the configured limit.");
        }
        try
        {
            state.RegisterExecuted(canonicalTitle, source, contentModel);
            var loaded = state.Script.LoadString(source, null, canonicalTitle);
            var result = ResumeBounded(state, loaded);
            if (!result.Success) return result;
            if (immutable && result.Value!.Type != DataType.Table)
                return new(false, null, "LoadDataResultInvalid", "mw.loadData requires a table result.");
            var value = immutable ? MakeImmutable(result.Value!, state.Script) : result.Value!;
            state.ModuleCache[cacheKey] = value;
            return new(true, value, null, null);
        }
        finally
        {
            state.Loading.Remove(canonicalTitle);
        }
    }

    private static BoundedValue ResumeBounded(ExecutionState state, DynValue function, params DynValue[] arguments)
    {
        var slice = Math.Max(1, Math.Min(10_000, state.Request.Budget.MaxInstructionCount));
        var coroutine = state.Script.CreateCoroutine(function).Coroutine;
        coroutine.AutoYieldCounter = slice;
        DynValue result = DynValue.Nil;
        for (long count = 0; coroutine.State != CoroutineState.Dead; count++)
        {
            if (state.InstructionUnits > state.Request.Budget.MaxInstructionCount - slice)
                return new(false, null, "InstructionLimitExceeded", "The Lua instruction/work budget was exceeded.");
            state.InstructionUnits += slice;
            result = count == 0 ? coroutine.Resume(arguments) : coroutine.Resume();
        }
        return new(true, result, null, null);
    }

    private static void RegisterRestrictedApi(Script script, ExecutionState state, IReadOnlyList<string> capabilities)
    {
        var mw = new Table(script);
        if (capabilities.Contains("mw.text", StringComparer.Ordinal)) mw.Set("text", DynValue.NewTable(RegisterTextApi(script)));
        if (capabilities.Contains("mw.ustring", StringComparer.Ordinal)) mw.Set("ustring", DynValue.NewTable(RegisterUStringApi(script)));
        if (capabilities.Contains("mw.title", StringComparer.Ordinal)) mw.Set("title", DynValue.NewTable(RegisterTitleApi(script, state)));
            if (capabilities.Contains("mw.ext.cargo", StringComparer.Ordinal))
            {
                var ext = new Table(script);
                var cargo = new Table(script);
                cargo.Set("query", DynValue.NewCallback((_, args) => QueryCargoSnapshot(state, args)));
                ext.Set("cargo", DynValue.NewTable(cargo));
                mw.Set("ext", DynValue.NewTable(ext));
            }
        if (capabilities.Contains("mw.site", StringComparer.Ordinal)) mw.Set("site", DynValue.NewTable(RegisterSiteApi(script, state)));
        if (capabilities.Contains("mw.language", StringComparer.Ordinal)) mw.Set("language", DynValue.NewTable(RegisterLanguageApi(script)));
        if (capabilities.Contains("mw.html", StringComparer.Ordinal)) mw.Set("html", DynValue.NewTable(RegisterHtmlApi(script)));
        if (capabilities.Contains("module.require", StringComparer.Ordinal))
            script.Globals.Set("require", DynValue.NewCallback((_, args) => RequireModule(state, args)));
        if (capabilities.Contains("mw.loadData", StringComparer.Ordinal))
            mw.Set("loadData", DynValue.NewCallback((_, args) => LoadData(state, args)));
        if (capabilities.Contains("mw.loadJsonData", StringComparer.Ordinal))
            mw.Set("loadJsonData", DynValue.NewCallback((_, args) => LoadJsonData(state, args)));
        script.Globals.Set("mw", DynValue.NewTable(mw));

        var cw = new Table(script);
        if (capabilities.Contains("cw.text", StringComparer.Ordinal))
            cw.Set("text", DynValue.NewTable(RegisterContinuumTextApi(script)));
        if (capabilities.Contains("cw.args", StringComparer.Ordinal))
        {
            var args = new Table(script);
            args.Set("get", DynValue.NewCallback((_, callbackArgs) =>
            {
                var key = callbackArgs.AsType(0, "cw.args.get", DataType.String).String;
                return state.Request.NamedArguments.TryGetValue(key, out var named)
                    ? DynValue.NewString(named)
                    : state.Request.PositionalArguments.FirstOrDefault(value => key == "1") is { } positional
                        ? DynValue.NewString(positional) : DynValue.Nil;
            }));
            args.Set("has", DynValue.NewCallback((_, callbackArgs) =>
            {
                var key = callbackArgs.AsType(0, "cw.args.has", DataType.String).String;
                return DynValue.NewBoolean(state.Request.NamedArguments.ContainsKey(key) || key == "1" && state.Request.PositionalArguments.Count > 0);
            }));
            cw.Set("args", DynValue.NewTable(args));
        }
        if (capabilities.Contains("cw.title", StringComparer.Ordinal))
        {
            var title = new Table(script);
            title.Set("current", DynValue.NewString(state.Request.FrameTitle ?? string.Empty));
            title.Set("normalize", DynValue.NewCallback((_, callbackArgs) =>
                DynValue.NewString(callbackArgs.AsType(0, "cw.title.normalize", DataType.String).String.Trim().Replace('_', ' '))));
            title.Set("namespace", DynValue.NewCallback((_, callbackArgs) =>
            {
                var value = callbackArgs.AsType(0, "cw.title.namespace", DataType.String).String;
                var separator = value.IndexOf(':');
                return DynValue.NewString(separator > 0 ? value[..separator] : "");
            }));
            cw.Set("title", DynValue.NewTable(title));
        }
        if (capabilities.Contains("cw.page", StringComparer.Ordinal))
            cw.Set("page", DynValue.NewTable(RegisterUnavailablePageApi(script)));
        if (capabilities.Contains("cw.module", StringComparer.Ordinal))
        {
            var module = new Table(script);
            module.Set("name", DynValue.NewString(state.Request.ModuleTitle));
            module.Set("revision", DynValue.NewString(state.Request.SourceRevisionId ?? string.Empty));
            var dependencies = new Table(script);
            foreach (var dependency in state.Request.Dependencies ?? []) dependencies.Set(dependency.CanonicalModuleTitle, DynValue.NewString(dependency.SourceRevisionId));
            module.Set("dependencies", DynValue.NewTable(dependencies));
            cw.Set("module", DynValue.NewTable(module));
        }
        script.Globals.Set("cw", DynValue.NewTable(cw));
    }

    private static Table RegisterTitleApi(Script script, ExecutionState state)
    {
        var title = new Table(script);
        var current = state.Request.FrameTitle ?? string.Empty;
        title.Set("text", DynValue.NewString(current));
        title.Set("prefixedText", DynValue.NewString(current));
        title.Set("namespace", DynValue.NewNumber(NamespaceId(current)));
        title.Set("getCurrentTitle", DynValue.NewCallback((_, _) => DynValue.NewTable(title)));
        title.Set("getContent", DynValue.NewCallback((_, _) => DynValue.Nil));
        title.Set("exists", DynValue.NewBoolean(false));
        title.Set("new", DynValue.NewCallback((_, args) =>
        {
            var value = args.AsType(0, "mw.title.new", DataType.String).String.Trim().Replace('_', ' ');
            var result = new Table(script);
            result.Set("text", DynValue.NewString(value));
            result.Set("prefixedText", DynValue.NewString(value));
            result.Set("namespace", DynValue.NewNumber(NamespaceId(value)));
            result.Set("exists", DynValue.NewBoolean(false));
            return DynValue.NewTable(result);
        }));
        return title;
    }

    private static Table RegisterSiteApi(Script script, ExecutionState state)
    {
        var site = new Table(script);
        var identity = state.Request.SourceWikiIdentity;
        site.Set("name", DynValue.NewString(identity));
        site.Set("identifier", DynValue.NewString(identity));
        site.Set("language", DynValue.Nil);
        site.Set("namespaces", DynValue.NewTable(new Table(script)));
        return site;
    }

    private static Table RegisterUnavailablePageApi(Script script)
    {
        var page = new Table(script);
        page.Set("exists", DynValue.NewCallback((_, args) =>
        {
            args.AsType(0, "cw.page.exists", DataType.String);
            return DynValue.False;
        }));
        page.Set("metadata", DynValue.NewCallback((_, args) =>
        {
            args.AsType(0, "cw.page.metadata", DataType.String);
            return DynValue.Nil;
        }));
        return page;
    }

    private static Table RegisterLanguageApi(Script script)
    {
        var language = new Table(script);
        language.Set("getCode", DynValue.NewCallback((_, _) => DynValue.Nil));
        language.Set("lc", DynValue.NewCallback((_, args) => DynValue.NewString(args.AsType(0, "mw.language.lc", DataType.String).String.ToLowerInvariant())));
        language.Set("uc", DynValue.NewCallback((_, args) => DynValue.NewString(args.AsType(0, "mw.language.uc", DataType.String).String.ToUpperInvariant())));
        language.Set("lcfirst", DynValue.NewCallback((_, args) => DynValue.NewString(ChangeFirstRune(args.AsType(0, "mw.language.lcfirst", DataType.String).String, lower: true))));
        language.Set("ucfirst", DynValue.NewCallback((_, args) => DynValue.NewString(ChangeFirstRune(args.AsType(0, "mw.language.ucfirst", DataType.String).String, lower: false))));
        return language;
    }

    private static Table RegisterHtmlApi(Script script)
    {
        var html = new Table(script);
        html.Set("element", DynValue.NewCallback((_, args) =>
        {
            var tag = args.AsType(0, "mw.html.element", DataType.String).String.ToLowerInvariant();
            if (!AllowedHtmlTags.Contains(tag)) throw new ScriptRuntimeException("mw.html.element received an unsupported tag.");
            var text = args.Count > 1 && args[1].Type != DataType.Nil
                ? args.AsType(1, "mw.html.element", DataType.String).String : string.Empty;
            if (text.Length > 32_768) throw new ScriptRuntimeException("mw.html.element text limit exceeded.");
            var attributes = args.Count > 2 && args[2].Type != DataType.Nil
                ? args.AsType(2, "mw.html.element", DataType.Table).Table : null;
            var renderedAttributes = attributes is null ? string.Empty : RenderHtmlAttributes(attributes);
            return DynValue.NewString($"<{tag}{renderedAttributes}>{WebUtility.HtmlEncode(text)}</{tag}>");
        }));
        return html;
    }

    private static readonly IReadOnlySet<string> AllowedHtmlTags = new HashSet<string>(StringComparer.Ordinal)
        { "b", "code", "div", "em", "i", "li", "p", "span", "strong", "sub", "sup", "u", "ul" };

    private static string RenderHtmlAttributes(Table attributes)
    {
        var builder = new StringBuilder();
        foreach (var pair in attributes.Pairs.OrderBy(pair => pair.Key.String, StringComparer.Ordinal))
        {
            if (pair.Key.Type != DataType.String || !AllowedHtmlAttributes.Contains(pair.Key.String))
                throw new ScriptRuntimeException("mw.html.element received an unsupported attribute.");
            if (pair.Value.Type != DataType.String) throw new ScriptRuntimeException("mw.html.element attributes must be strings.");
            var value = pair.Value.String;
            if (value.Length > 4_096) throw new ScriptRuntimeException("mw.html.element attribute limit exceeded.");
            builder.Append(' ').Append(pair.Key.String).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append("\"");
        }
        return builder.ToString();
    }

    private static readonly IReadOnlySet<string> AllowedHtmlAttributes = new HashSet<string>(StringComparer.Ordinal)
        { "class", "id", "title" };

    private static string ChangeFirstRune(string value, bool lower)
    {
        var first = value.EnumerateRunes().FirstOrDefault();
        if (first == default) return value;
        var replacement = (lower ? first.ToString().ToLowerInvariant() : first.ToString().ToUpperInvariant());
        return replacement + value[first.Utf16SequenceLength..];
    }

    private static int NamespaceId(string title)
    {
        var separator = title.IndexOf(':');
        return separator > 0 ? 1 : 0;
    }

    private static Table RegisterContinuumTextApi(Script script)
    {
        var text = RegisterTextApi(script);
        text.Set("replace", DynValue.NewCallback((_, args) =>
        {
            var value = args.AsType(0, "cw.text.replace", DataType.String).String;
            var oldValue = args.AsType(1, "cw.text.replace", DataType.String).String;
            var replacement = args.AsType(2, "cw.text.replace", DataType.String).String;
            if (oldValue.Length == 0) throw new ScriptRuntimeException("cw.text.replace requires a non-empty search value.");
            return DynValue.NewString(value.Replace(oldValue, replacement, StringComparison.Ordinal));
        }));
        text.Set("escape", DynValue.NewCallback((_, args) =>
            DynValue.NewString(WebUtility.HtmlEncode(args.AsType(0, "cw.text.escape", DataType.String).String))));
        return text;
    }

    private static DynValue RequireModule(ExecutionState state, CallbackArguments args)
    {
        var requested = args.AsType(0, "require", DataType.String).String;
        var canonical = CanonicalModuleName(requested, "require");
        var current = state.CurrentModule;
        if (!state.IsDeclaredDependency(current, canonical))
            throw new ScriptRuntimeException("The requested module was not an explicitly declared dependency.");
        if (!state.Modules.TryGetValue(canonical, out var dependency))
            throw new ScriptRuntimeException("The requested module was not present in the validated dependency bundle.");
        if (!IsLuaContentModel(dependency.ContentModel))
            throw new ScriptRuntimeException("The requested module used an unsupported content model for require.");
        var result = ExecuteLuaModule(canonical, dependency.Source, dependency.ContentModel, state, immutable: false);
        if (!result.Success) throw new ScriptRuntimeException(result.ErrorMessage!);
        return result.Value!;
    }

    private static DynValue QueryCargoSnapshot(ExecutionState state, CallbackArguments args)
    {
        if (args.Count < 2)
            throw new ScriptRuntimeException("CargoQueryInvalid: table and field arguments are required.");
        var table = args.AsType(0, "mw.ext.cargo.query", DataType.String).String;
        var fields = args.AsType(1, "mw.ext.cargo.query", DataType.String).String;
        var result = state.Request.CargoResults?.SingleOrDefault(value =>
            string.Equals(value.Table, table, StringComparison.Ordinal)
            && string.Equals(value.Fields, fields, StringComparison.Ordinal));
        if (result is null)
            throw new ScriptRuntimeException("CargoDataUnavailable: no authorized pre-resolved Cargo result matched the query.");
        var rows = new Table(state.Script);
        var rowIndex = 1;
        foreach (var sourceRow in result.Rows)
        {
            var row = new Table(state.Script);
            foreach (var cell in sourceRow)
                row.Set(cell.Key, cell.Value is null ? DynValue.Nil : DynValue.NewString(cell.Value));
            rows.Set(rowIndex++, DynValue.NewTable(row));
        }
        return DynValue.NewTable(rows);
    }

    private static DynValue LoadData(ExecutionState state, CallbackArguments args)
    {
        var requested = args.AsType(0, "mw.loadData", DataType.String).String;
        var canonical = CanonicalModuleName(requested, "mw.loadData");
        EnsureDeclaredAndPresent(state, canonical, "mw.loadData");
        var dependency = state.Modules[canonical];
        if (!IsLuaContentModel(dependency.ContentModel))
            throw new ScriptRuntimeException("mw.loadData requires a Lua/Scribunto module content model.");
        var result = ExecuteLuaModule(canonical, dependency.Source, dependency.ContentModel, state, immutable: true);
        if (!result.Success) throw new ScriptRuntimeException(result.ErrorMessage!);
        return result.Value!;
    }

    private static DynValue LoadJsonData(ExecutionState state, CallbackArguments args)
    {
        var requested = args.AsType(0, "mw.loadJsonData", DataType.String).String;
        var canonical = CanonicalModuleName(requested, "mw.loadJsonData");
        EnsureDeclaredAndPresent(state, canonical, "mw.loadJsonData");
        var dependency = state.Modules[canonical];
        if (!IsJsonContentModel(dependency.ContentModel))
            throw new ScriptRuntimeException("mw.loadJsonData requires a JSON content model.");
        var cacheKey = "json:" + canonical;
        if (state.ModuleCache.TryGetValue(cacheKey, out var cached)) return cached;
        state.RegisterExecuted(canonical, dependency.Source, dependency.ContentModel);
        try
        {
            using var document = JsonDocument.Parse(dependency.Source, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            var value = ConvertJson(document.RootElement, state, 0);
            var immutable = value.Type == DataType.Table ? MakeImmutable(value, state.Script) : value;
            state.ModuleCache[cacheKey] = immutable;
            return immutable;
        }
        catch (JsonException)
        {
            throw new ScriptRuntimeException("The bundled JSON module was malformed or exceeded its bounds.");
        }
    }

    private static void EnsureDeclaredAndPresent(ExecutionState state, string canonical, string function)
    {
        if (!state.IsDeclaredDependency(state.CurrentModule, canonical))
            throw new ScriptRuntimeException($"{function} requested an undeclared module dependency.");
        if (!state.Modules.ContainsKey(canonical))
            throw new ScriptRuntimeException($"{function} requested a module missing from the validated dependency bundle.");
    }

    private static string CanonicalModuleName(string value, string function)
    {
        if (!MediaWikiModuleTitle.TryParse(value, out var module) || module.NamespaceId != 828)
            throw new ScriptRuntimeException($"{function} received an invalid or path-like module name.");
        return module.CanonicalTitle;
    }

    private static bool IsLuaContentModel(string value)
        => value.Equals("Scribunto", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Scribunto/Lua", StringComparison.OrdinalIgnoreCase)
            || value.Equals("lua", StringComparison.OrdinalIgnoreCase);

    private static bool IsJsonContentModel(string value)
        => value.Equals("Json", StringComparison.OrdinalIgnoreCase)
            || value.Equals("json", StringComparison.OrdinalIgnoreCase)
            || value.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    private static DynValue ConvertJson(JsonElement element, ExecutionState state, int depth)
    {
        if (depth > 32 || ++state.JsonNodes > 10_000)
            throw new ScriptRuntimeException("The JSON module exceeded its bounded data limits.");
        return element.ValueKind switch
        {
            JsonValueKind.Null => DynValue.Nil,
            JsonValueKind.True => DynValue.NewBoolean(true),
            JsonValueKind.False => DynValue.NewBoolean(false),
            JsonValueKind.String => DynValue.NewString(element.GetString()!),
            JsonValueKind.Number when element.TryGetDouble(out var number) && double.IsFinite(number) => DynValue.NewNumber(number),
            JsonValueKind.Array => ConvertJsonArray(element, state, depth),
            JsonValueKind.Object => ConvertJsonObject(element, state, depth),
            _ => throw new ScriptRuntimeException("The JSON module contained an unsupported value.")
        };
    }

    private static DynValue ConvertJsonArray(JsonElement element, ExecutionState state, int depth)
    {
        var table = new Table(state.Script);
        var index = 1;
        foreach (var item in element.EnumerateArray()) table.Set(index++, ConvertJson(item, state, depth + 1));
        return DynValue.NewTable(table);
    }

    private static DynValue ConvertJsonObject(JsonElement element, ExecutionState state, int depth)
    {
        var table = new Table(state.Script);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new ScriptRuntimeException("The JSON module contained duplicate object keys.");
            table.Set(property.Name, ConvertJson(property.Value, state, depth + 1));
        }
        return DynValue.NewTable(table);
    }

    private static DynValue MakeImmutable(DynValue value, Script script)
        => MakeImmutable(value, script, new HashSet<Table>());

    private static DynValue MakeImmutable(DynValue value, Script script, HashSet<Table> visiting)
    {
        if (value.Type != DataType.Table) return value;
        var backing = value.Table;
        if (!visiting.Add(backing)) throw new ScriptRuntimeException("The mw.loadData result contained a recursive table.");
        foreach (var key in backing.Keys.ToArray())
        {
            var item = backing.Get(key);
            if (item.Type == DataType.Table) backing.Set(key, MakeImmutable(item, script, visiting));
        }
        var proxy = new Table(script);
        var metatable = new Table(script);
        metatable.Set("__index", DynValue.NewTable(backing));
        metatable.Set("__newindex", DynValue.NewCallback((_, _) => throw new ScriptRuntimeException("The mw.loadData result is immutable.")));
        metatable.Set("__len", DynValue.NewCallback((_, _) => DynValue.NewNumber(backing.Length)));
        proxy.MetaTable = metatable;
        visiting.Remove(backing);
        return DynValue.NewTable(proxy);
    }

    private static Table RegisterTextApi(Script script)
    {
        var text = new Table(script);
        text.Set("trim", DynValue.NewCallback((_, args) => DynValue.NewString(args.AsType(0, "mw.text.trim", DataType.String).String.Trim())));
        text.Set("split", DynValue.NewCallback((_, args) =>
        {
            var value = args.AsType(0, "mw.text.split", DataType.String).String;
            var separator = args.AsType(1, "mw.text.split", DataType.String).String;
            if (args.Count > 2 && args[2].Type != DataType.Nil)
            {
                RequireType(args, 2, DataType.Boolean, "mw.text.split");
                if (!args[2].Boolean) throw new ScriptRuntimeException("mw.text.split pattern matching is unsupported in the experimental worker.");
            }
            var table = new Table(script);
            var parts = value.Split(separator, StringSplitOptions.None);
            for (var index = 0; index < parts.Length; index++) table.Set(index + 1, DynValue.NewString(parts[index]));
            return DynValue.NewTable(table);
        }));
        text.Set("nowiki", DynValue.NewCallback((_, args) => DynValue.NewString(Nowiki(args.AsType(0, "mw.text.nowiki", DataType.String).String))));
        text.Set("encode", DynValue.NewCallback((_, args) =>
        {
            var value = args.AsType(0, "mw.text.encode", DataType.String).String;
            if (args.Count > 1 && args[1].Type != DataType.Nil) RequireType(args, 1, DataType.String, "mw.text.encode");
            var charset = args.Count > 1 && args[1].Type != DataType.Nil ? args[1].String : "<>&\"'\u00a0";
            return DynValue.NewString(Encode(value, charset));
        }));
        text.Set("decode", DynValue.NewCallback((_, args) =>
        {
            var value = args.AsType(0, "mw.text.decode", DataType.String).String;
            if (args.Count > 1 && args[1].Type != DataType.Nil) RequireType(args, 1, DataType.Boolean, "mw.text.decode");
            var decodeNamed = args.Count > 1 && args[1].Type != DataType.Nil && args[1].Boolean;
            return DynValue.NewString(Decode(value, decodeNamed));
        }));
        text.Set("listToText", DynValue.NewCallback((_, args) =>
        {
            var list = args.AsType(0, "mw.text.listToText", DataType.Table).Table;
            if (args.Count > 1 && args[1].Type != DataType.Nil) RequireType(args, 1, DataType.String, "mw.text.listToText");
            if (args.Count > 2 && args[2].Type != DataType.Nil) RequireType(args, 2, DataType.String, "mw.text.listToText");
            var separator = args.Count > 1 && args[1].Type != DataType.Nil ? args[1].String : ", ";
            var conjunction = args.Count > 2 && args[2].Type != DataType.Nil ? args[2].String : " and ";
            var values = new List<string>();
            for (var index = 1; index <= 1024; index++)
            {
                var value = list.Get(index);
                if (value.Type == DataType.Nil) break;
                if (value.Type is not (DataType.String or DataType.Number or DataType.Boolean)) throw new ScriptRuntimeException("mw.text.listToText accepts scalar list values only.");
                values.Add(value.CastToString());
            }
            if (list.Get(1025).Type != DataType.Nil) throw new ScriptRuntimeException("mw.text.listToText list limit exceeded.");
            return DynValue.NewString(values.Count switch
            {
                0 => string.Empty,
                1 => values[0],
                2 => values[0] + conjunction + values[1],
                _ => string.Join(separator, values.Take(values.Count - 1)) + conjunction + values[^1]
            });
        }));
        text.Set("jsonEncode", DynValue.NewCallback((_, args) =>
        {
            var value = args.AsType(0, "mw.text.jsonEncode", DataType.Table);
            var json = EncodeJson(value.Table, 0, new HashSet<Table>());
            if (json.Length > 100_000) throw new ScriptRuntimeException("mw.text.jsonEncode output limit exceeded.");
            return DynValue.NewString(json);
        }));
        text.Set("jsonDecode", DynValue.NewCallback((_, args) =>
        {
            var json = args.AsType(0, "mw.text.jsonDecode", DataType.String).String;
            if (json.Length > 100_000) throw new ScriptRuntimeException("mw.text.jsonDecode input limit exceeded.");
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                return DecodeJson(document.RootElement, script, 0);
            }
            catch (JsonException)
            {
                throw new ScriptRuntimeException("mw.text.jsonDecode received invalid JSON.");
            }
        }));
        return text;
    }

    private static string EncodeJson(Table table, int depth, HashSet<Table> visiting)
    {
        if (depth >= 16 || !visiting.Add(table)) throw new ScriptRuntimeException("mw.text.jsonEncode encountered a recursive or deeply nested table.");
        try
        {
            var entries = table.Pairs.ToArray();
            var array = entries.Length == 0 || entries.All(pair => pair.Key.Type == DataType.Number && pair.Key.Number == Math.Truncate(pair.Key.Number) && pair.Key.Number >= 1 && pair.Key.Number <= 1024);
            if (array)
            {
                var max = entries.Length == 0 ? 0 : (int)entries.Max(pair => pair.Key.Number);
                if (max != entries.Length || entries.Any(pair => pair.Key.Number < 1)) throw new ScriptRuntimeException("mw.text.jsonEncode does not accept sparse arrays.");
                return "[" + string.Join(',', Enumerable.Range(1, max).Select(index => EncodeJsonValue(table.Get(index), depth + 1, visiting))) + "]";
            }
            if (entries.Any(pair => pair.Key.Type != DataType.String)) throw new ScriptRuntimeException("mw.text.jsonEncode object keys must be strings.");
            return "{" + string.Join(',', entries.OrderBy(pair => pair.Key.String, StringComparer.Ordinal).Select(pair => JsonSerializer.Serialize(pair.Key.String) + ":" + EncodeJsonValue(pair.Value, depth + 1, visiting))) + "}";
        }
        finally { visiting.Remove(table); }
    }

    private static string EncodeJsonValue(DynValue value, int depth, HashSet<Table> visiting)
        => value.Type switch
        {
            DataType.Nil => "null",
            DataType.Boolean => value.Boolean ? "true" : "false",
            DataType.Number => double.IsFinite(value.Number) ? JsonSerializer.Serialize(value.Number) : throw new ScriptRuntimeException("mw.text.jsonEncode does not accept non-finite numbers."),
            DataType.String => JsonSerializer.Serialize(value.String),
            DataType.Table => EncodeJson(value.Table, depth, visiting),
            _ => throw new ScriptRuntimeException("mw.text.jsonEncode received an unsupported Lua value.")
        };

    private static DynValue DecodeJson(JsonElement element, Script script, int depth)
    {
        if (depth >= 16) throw new ScriptRuntimeException("mw.text.jsonDecode nesting limit exceeded.");
        return element.ValueKind switch
        {
            JsonValueKind.Null => DynValue.Nil,
            JsonValueKind.True => DynValue.True,
            JsonValueKind.False => DynValue.False,
            JsonValueKind.Number when element.TryGetDouble(out var number) && double.IsFinite(number) => DynValue.NewNumber(number),
            JsonValueKind.String => DynValue.NewString(element.GetString()!),
            JsonValueKind.Array => DecodeJsonArray(element, script, depth),
            JsonValueKind.Object => DecodeJsonObject(element, script, depth),
            _ => throw new ScriptRuntimeException("mw.text.jsonDecode received unsupported JSON.")
        };
    }

    private static DynValue DecodeJsonArray(JsonElement element, Script script, int depth)
    {
        var table = new Table(script);
        var index = 1;
        foreach (var item in element.EnumerateArray()) table.Set(index++, DecodeJson(item, script, depth + 1));
        return DynValue.NewTable(table);
    }

    private static DynValue DecodeJsonObject(JsonElement element, Script script, int depth)
    {
        var table = new Table(script);
        foreach (var property in element.EnumerateObject()) table.Set(property.Name, DecodeJson(property.Value, script, depth + 1));
        return DynValue.NewTable(table);
    }

    private static Table RegisterUStringApi(Script script)
    {
        var ustring = new Table(script);
        ustring.Set("len", DynValue.NewCallback((_, args) => DynValue.NewNumber(args.AsType(0, "mw.ustring.len", DataType.String).String.EnumerateRunes().Count())));
        ustring.Set("lower", DynValue.NewCallback((_, args) => DynValue.NewString(args.AsType(0, "mw.ustring.lower", DataType.String).String.ToLowerInvariant())));
        ustring.Set("upper", DynValue.NewCallback((_, args) => DynValue.NewString(args.AsType(0, "mw.ustring.upper", DataType.String).String.ToUpperInvariant())));
        ustring.Set("sub", DynValue.NewCallback((_, args) =>
        {
            var value = args.AsType(0, "mw.ustring.sub", DataType.String).String.EnumerateRunes().ToArray();
            var start = IntegerArgument(args, 1, "mw.ustring.sub");
            var end = args.Count > 2 && args[2].Type != DataType.Nil ? IntegerArgument(args, 2, "mw.ustring.sub") : value.Length;
            var first = NormalizeIndex(start, value.Length, 1);
            var last = NormalizeIndex(end, value.Length, value.Length);
            if (first > last || value.Length == 0) return DynValue.NewString(string.Empty);
            return DynValue.NewString(string.Concat(value[(first - 1)..last].Select(rune => rune.ToString())));
        }));
        ustring.Set("find", DynValue.NewCallback((_, args) =>
        {
            var source = args.AsType(0, "mw.ustring.find", DataType.String).String.EnumerateRunes().ToArray();
            var pattern = args.AsType(1, "mw.ustring.find", DataType.String).String.EnumerateRunes().ToArray();
            var init = args.Count > 2 && args[2].Type != DataType.Nil ? IntegerArgument(args, 2, "mw.ustring.find") : 1;
            if (args.Count > 3 && args[3].Type != DataType.Nil) RequireType(args, 3, DataType.Boolean, "mw.ustring.find");
            var plain = args.Count <= 3 || args[3].Type == DataType.Nil || args[3].Boolean;
            if (!plain) throw new ScriptRuntimeException("mw.ustring.find pattern matching is unsupported in the experimental worker.");
            var from = Math.Max(1, NormalizeIndex(init, source.Length, 1));
            var found = FindRunes(source, pattern, from - 1);
            return found < 0 ? DynValue.Nil : DynValue.NewTuple(DynValue.NewNumber(found + 1), DynValue.NewNumber(found + pattern.Length));
        }));
        return ustring;
    }

    private static int IntegerArgument(CallbackArguments args, int index, string function)
    {
        var value = args.AsType(index, function, DataType.Number).Number;
        if (value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue) throw new ScriptRuntimeException($"{function} requires an integer index.");
        return (int)value;
    }

    private static void RequireType(CallbackArguments args, int index, DataType expected, string function)
    {
        if (args[index].Type != expected) throw new ScriptRuntimeException($"{function} received an invalid argument type.");
    }

    private static int NormalizeIndex(int index, int length, int fallback)
        => index == 0 ? fallback : index < 0 ? Math.Max(1, length + index + 1) : Math.Min(length, index);

    private static int FindRunes(IReadOnlyList<System.Text.Rune> source, IReadOnlyList<System.Text.Rune> pattern, int start)
    {
        if (pattern.Count == 0) return Math.Min(start, source.Count);
        for (var index = start; index + pattern.Count <= source.Count; index++)
        {
            var equal = true;
            for (var offset = 0; offset < pattern.Count; offset++) if (source[index + offset] != pattern[offset]) { equal = false; break; }
            if (equal) return index;
        }
        return -1;
    }

    private static string Encode(string value, string charset)
    {
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (!charset.Contains(rune.ToString(), StringComparison.Ordinal)) { builder.Append(rune.ToString()); continue; }
            builder.Append(rune.Value switch
            {
                '<' => "&lt;", '>' => "&gt;", '&' => "&amp;", '"' => "&quot;", '\u00a0' => "&nbsp;",
                _ => "&#" + rune.Value.ToString(CultureInfo.InvariantCulture) + ";"
            });
        }
        return builder.ToString();
    }

    private static string Decode(string value, bool decodeNamedEntities)
    {
        if (decodeNamedEntities) return WebUtility.HtmlDecode(value);
        return Regex.Replace(value, "&(?:lt|gt|amp|quot|nbsp);|&#(?:x[0-9a-fA-F]+|[0-9]+);", match =>
        {
            if (match.Value is "&lt;") return "<";
            if (match.Value is "&gt;") return ">";
            if (match.Value is "&amp;") return "&";
            if (match.Value is "&quot;") return "\"";
            if (match.Value is "&nbsp;") return "\u00a0";
            var number = match.Value.StartsWith("&#x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(match.Value[3..^1], 16) : int.Parse(match.Value[2..^1], CultureInfo.InvariantCulture);
            return char.ConvertFromUtf32(number);
        });
    }

    private static string Nowiki(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("{", "&#123;", StringComparison.Ordinal).Replace("}", "&#125;", StringComparison.Ordinal).Replace("[", "&#91;", StringComparison.Ordinal)
            .Replace("]", "&#93;", StringComparison.Ordinal).Replace("|", "&#124;", StringComparison.Ordinal).Replace("=", "&#61;", StringComparison.Ordinal);

    private static MediaWikiScribuntoWorkerResponse Failure(string correlation, string code, string message)
        => new(MediaWikiScribuntoWorkerProtocol.CurrentVersion, correlation, "Failed", null, [new(code, Sanitize(message), "Error")], null);

    private static MediaWikiScribuntoWorkerResponse ResourceLimit(string correlation, string code, string message)
        => new(MediaWikiScribuntoWorkerProtocol.CurrentVersion, correlation, "ResourceLimit", null, [new(code, message, "Error")], code);

    private static MediaWikiScribuntoWorkerResponse Response(string status, string correlation, string message, string code)
        => new(MediaWikiScribuntoWorkerProtocol.CurrentVersion, correlation, status, null, [new(code, Sanitize(message), "Error")], null);

    private static (string Code, string Message) ClassifyException(Exception exception)
    {
        var message = Sanitize(exception.Message);
        if (message.Contains("instruction", StringComparison.OrdinalIgnoreCase)) return ("InstructionLimitExceeded", "The Lua instruction/work budget was exceeded.");
        if (message.Contains("circular import", StringComparison.OrdinalIgnoreCase)) return ("CircularModuleDependency", "The module dependency graph contained a circular import.");
        if (message.Contains("undeclared module dependency", StringComparison.OrdinalIgnoreCase)
            || message.Contains("explicitly declared dependency", StringComparison.OrdinalIgnoreCase)) return ("UndeclaredModuleDependency", "The module requested a dependency that was not explicitly declared.");
        if (message.Contains("missing from the validated dependency bundle", StringComparison.OrdinalIgnoreCase)) return ("MissingModuleDependency", "The requested module was missing from the validated dependency bundle.");
        if (message.Contains("path-like module name", StringComparison.OrdinalIgnoreCase)) return ("InvalidModuleReference", "The module reference was invalid or path-like.");
        if (message.Contains("immutable", StringComparison.OrdinalIgnoreCase)) return ("ImmutableDataViolation", "The loaded data was immutable.");
        if (message.Contains("malformed or exceeded", StringComparison.OrdinalIgnoreCase)) return ("MalformedJsonData", "The bundled JSON data was malformed or exceeded its bounds.");
        if (message.Contains("dependency depth", StringComparison.OrdinalIgnoreCase)) return ("DependencyDepthExceeded", "The module dependency depth exceeded its configured limit.");
        if (message.Contains("mw.text.split pattern", StringComparison.OrdinalIgnoreCase) || message.Contains("mw.ustring.find pattern", StringComparison.OrdinalIgnoreCase)) return ("UnsupportedApi", message);
        if (message.Contains("parser-function bridge", StringComparison.OrdinalIgnoreCase)) return ("UnsupportedParserFunction", "The requested parser function is not enabled in the bounded worker bridge.");
        if (exception is SyntaxErrorException) return ("LuaSyntaxError", message);
        return ("LuaRuntimeError", message);
    }

    private static string Sanitize(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "The worker reported an unspecified error.";
        var clean = message.Replace('\r', ' ').Replace('\n', ' ');
        return clean.Length <= 1_024 ? clean : clean[..1_024];
    }

    private static string? ReadBoundedLine(int maximumCharacters, out string? error)
    {
        error = null;
        var builder = new StringBuilder();
        while (true)
        {
            var character = Console.In.Read();
            if (character < 0) break;
            if (character == '\n') break;
            if (character != '\r') builder.Append((char)character);
            if (builder.Length > maximumCharacters) { error = "The worker request exceeded the configured input limit."; return null; }
        }
        if (builder.Length == 0) return null;
        return builder.ToString();
    }

    private static string ReadBoundedText(int maximumCharacters, out bool overflow)
    {
        overflow = false;
        var builder = new StringBuilder();
        while (true)
        {
            var character = Console.In.Read();
            if (character < 0) break;
            if (builder.Length < maximumCharacters) builder.Append((char)character);
            else overflow = true;
        }
        return builder.ToString();
    }

    private static string TryGetCorrelationId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("correlationId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException) { return string.Empty; }
    }

    private static int Write(MediaWikiScribuntoWorkerResponse response)
    {
        var json = MediaWikiScribuntoWorkerProtocol.SerializeResponse(response);
        if (json.Length > AbsoluteOutputLimit) json = MediaWikiScribuntoWorkerProtocol.SerializeResponse(Response("ProtocolError", response.CorrelationId, "The worker response exceeded the output limit.", "OutputLimitExceeded"));
        Console.Out.WriteLine(json);
        return 0;
    }

    private sealed class ExecutionState
    {
        public ExecutionState(Script script, MediaWikiScribuntoWorkerRequest request)
        {
            Script = script;
            Request = request;
            foreach (var dependency in request.Dependencies ?? [])
                Modules[dependency.CanonicalModuleTitle] = dependency;
        }

        public Script Script { get; }
        public MediaWikiScribuntoWorkerRequest Request { get; }
        public Dictionary<string, MediaWikiScribuntoWorkerModuleDependency> Modules { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DynValue> ModuleCache { get; } = new(StringComparer.Ordinal);
        public List<string> Loading { get; } = [];
        public Dictionary<string, MediaWikiScribuntoWorkerDependencyReport> ExecutedModules { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long InstructionUnits { get; set; }
        public int JsonNodes { get; set; }
        public string CurrentModule => Loading.Count == 0 ? Request.ModuleTitle : Loading[^1];

        public bool IsDeclaredDependency(string currentModule, string requested)
        {
            var relationships = string.Equals(currentModule, Request.ModuleTitle, StringComparison.OrdinalIgnoreCase)
                ? Request.RootDependencies ?? []
                : Modules.TryGetValue(currentModule, out var dependency) ? dependency.Dependencies : [];
            return relationships.Any(value => string.Equals(CanonicalModuleNameForState(value), requested, StringComparison.OrdinalIgnoreCase));
        }

        public void RegisterExecuted(string canonicalTitle, string source, string contentModel)
        {
            if (ExecutedModules.ContainsKey(canonicalTitle)) return;
            if (string.Equals(canonicalTitle, Request.ModuleTitle, StringComparison.OrdinalIgnoreCase))
            {
                ExecutedModules[canonicalTitle] = new(
                    Request.SourceWikiIdentity,
                    canonicalTitle,
                    Request.ModuleSourceIdentity,
                    Request.SourceRevisionId ?? "unversioned",
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source))),
                    "Executed");
                return;
            }
            var dependency = Modules[canonicalTitle];
            ExecutedModules[canonicalTitle] = new(
                dependency.SourceWikiIdentity,
                dependency.CanonicalModuleTitle,
                dependency.SourcePageId,
                dependency.SourceRevisionId,
                dependency.SourceSha256,
                "Executed");
        }

        private static string CanonicalModuleNameForState(string value)
            => MediaWikiModuleTitle.TryParse(value, out var module) ? module.CanonicalTitle : value;
    }

    private sealed record BoundedValue(bool Success, DynValue? Value, string? ErrorCode, string? ErrorMessage);
}
