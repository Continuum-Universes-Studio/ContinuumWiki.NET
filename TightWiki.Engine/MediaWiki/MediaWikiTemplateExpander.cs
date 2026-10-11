using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

public sealed record MediaWikiTemplateDependency(
    string SourceTitle,
    string? SourcePageId,
    string? SourceRevisionId,
    int? TargetRevision,
    string Status);

internal sealed record MediaWikiTemplateExpansionResult(
    string Text,
    IReadOnlyList<MediaWikiCompatibilityDiagnostic> Diagnostics,
    IReadOnlyList<MediaWikiTemplateDependency> Dependencies,
    IReadOnlyList<string> UnsupportedConstructs,
    bool DependsOnCurrentTime,
    IReadOnlyList<MediaWikiScribuntoDependency> ScribuntoDependencies);

/// <summary>
/// Expands only the deliberately supported MediaWiki template subset. The
/// expander returns Wikitext, never HTML, so the normal bounded renderer still
/// owns escaping, links, media, references, and extension-tag handling.
/// </summary>
internal sealed class MediaWikiTemplateExpander(
    ITwMediaWikiImportRepository importRepository,
    MediaWikiWikitextRenderOptions options)
{
    public async Task<MediaWikiTemplateExpansionResult> ExpandAsync(
        string source,
        TwMediaWikiRenderSource provenance,
        bool directTemplatePage,
        CancellationToken cancellationToken = default,
        MediaWikiExpansionContext? expansionContext = null)
    {
        var diagnostics = new List<MediaWikiCompatibilityDiagnostic>();
        var dependencies = new List<MediaWikiTemplateDependency>();
        var unsupported = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.MaxExpansionTime);
        var context = new ExpansionContext(options, provenance, importRepository, diagnostics, dependencies, unsupported, timeout.Token,
            expansionContext ?? MediaWikiExpansionContext.From(provenance, new TwConfiguration(), options.TimeProvider));
        try
        {
            var prepared = ApplyInclusion(source.Length > options.MaxInputCharacters ? source[..options.MaxInputCharacters] : source, directTemplatePage, context);
            if (options.EnableLegacyInfoboxAdapter && options.InfoboxRegistry is not null)
                prepared = MediaWikiInfoboxLegacyAdapter.Adapt(prepared, options.InfoboxRegistry, options.MaxInputCharacters,
                    (code, message) => context.AddDiagnostic(code, message, 0, prepared.Length));
            if (source.Length > options.MaxInputCharacters)
                context.AddDiagnostic("InputLimitExceeded", "MediaWiki source exceeded the configured input limit and was truncated for rendering.", options.MaxInputCharacters, 0, MediaWikiDiagnosticSeverity.Error);
            var result = await ExpandTextAsync(prepared, context, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), [], 0);
            return new(result, diagnostics, dependencies, unsupported, context.DependsOnCurrentTime, context.ScribuntoDependencies);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            context.AddDiagnostic("ExpansionTimeLimitExceeded", "MediaWiki template expansion exceeded the configured time budget.", 0, source.Length, MediaWikiDiagnosticSeverity.Error);
            return new("[MediaWiki template expansion limit exceeded]", diagnostics, dependencies, unsupported, context.DependsOnCurrentTime, context.ScribuntoDependencies);
        }
        catch (ExpansionBudgetExceededException)
        {
            return new("[MediaWiki template expansion limit exceeded]", diagnostics, dependencies, unsupported, context.DependsOnCurrentTime, context.ScribuntoDependencies);
        }
    }

    private async Task<string> ExpandTextAsync(
        string text,
        ExpansionContext context,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<string> templateStack,
        int depth)
    {
        if (!context.TryConsume(text.Length == 0 ? 1 : 1)) return "[MediaWiki expansion limit exceeded]";
        if (depth > context.Options.MaxTemplateExpansionDepth)
        {
            context.AddDiagnostic("ExpansionDepthExceeded", "MediaWiki template expansion exceeded the configured depth limit.", 0, text.Length, MediaWikiDiagnosticSeverity.Error);
            return "[MediaWiki template expansion depth exceeded]";
        }

        var output = new StringBuilder(Math.Min(text.Length, context.Options.MaxExpansionOutputCharacters));
        for (var index = 0; index < text.Length;)
        {
            if (context.HasTerminalFailure)
            {
                // Preserve source that follows a typed execution failure, but
                // do not continue resolving work that cannot produce valid
                // Wikitext. This prevents secondary diagnostics from hiding
                // the first established dependency/runtime failure.
                AppendBounded(output, text[index..], context);
                break;
            }
            context.ThrowIfCancelledOrOverBudget();
            if (text.AsSpan(index).StartsWith("{{{", StringComparison.Ordinal))
            {
                if (!TryReadConstruct(text, index, "{{{", "}}}", out var end, out var body))
                {
                    context.AddDiagnostic("UnbalancedParameter", "Unbalanced MediaWiki parameter delimiter was preserved as text.", index, text.Length - index);
                    AppendBounded(output, text[index..], context);
                    break;
                }

                var parts = SplitTopLevel(body, '|');
                var name = parts[0].Trim();
                var value = string.Empty;
                if (name.Length == 0)
                {
                    context.AddDiagnostic("InvalidParameter", "A MediaWiki parameter name was empty.", index, end - index + 3);
                }
                else if (!parameters.TryGetValue(name, out value))
                {
                    value = parts.Count > 1
                        ? await ExpandTextAsync(string.Join('|', parts.Skip(1)), context, parameters, templateStack, depth + 1)
                        : string.Empty;
                }
                AppendBounded(output, value, context);
                index = end + 3;
                continue;
            }

            if (text.AsSpan(index).StartsWith("{{", StringComparison.Ordinal))
            {
                if (!TryReadConstruct(text, index, "{{", "}}", out var end, out var body))
                {
                    context.AddDiagnostic("UnbalancedDelimiter", "Unbalanced MediaWiki template delimiter was preserved as text.", index, text.Length - index);
                    AppendBounded(output, text[index..], context);
                    break;
                }

                var expanded = await ExpandInvocationAsync(body, text[index..(end + 2)], context, parameters, templateStack, depth, index);
                AppendBounded(output, expanded, context);
                index = end + 2;
                continue;
            }

            var next = FindNextConstruct(text, index);
            AppendBounded(output, text[index..next], context);
            index = next;
        }
        return output.ToString();
    }

    private async Task<string> ExpandInvocationAsync(
        string body,
        string original,
        ExpansionContext context,
        IReadOnlyDictionary<string, string> parentParameters,
        IReadOnlyList<string> templateStack,
        int depth,
        int offset)
    {
        var parts = SplitTopLevel(body, '|');
        var head = parts[0].Trim();
        var special = await ExpandSpecialInvocationAsync(head, parts.Skip(1).ToArray(), original, context, parentParameters, templateStack, depth, offset);
        if (special.Handled) return special.Value;
        if (head.StartsWith('#'))
            return await ExpandParserFunctionAsync(head, parts.Skip(1).ToArray(), original, context, parentParameters, templateStack, depth, offset);

        if (head.StartsWith("subst:", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("safesubst:", StringComparison.OrdinalIgnoreCase))
        {
            context.AddUnsupported("unsupported-template-syntax");
            context.AddDiagnostic("UnsupportedTemplateSyntax", "This MediaWiki template invocation form is not evaluated.", offset, original.Length);
            return original;
        }

        if (head.Contains("{{", StringComparison.Ordinal))
        {
            head = (await ExpandTextAsync(head, context, parentParameters, templateStack, depth + 1)).Trim();
        }

        if (!MediaWikiTemplateTitle.TryParse(head, out var parsedTitle, out var titleReason))
        {
            if (titleReason == "unknown-namespace")
            {
                var separator = head.IndexOf(':');
                var prefix = separator > 0 ? head[..separator].Trim() : head;
                context.AddUnsupported("unresolved-template-namespace");
                context.AddDiagnostic("UnresolvedTemplateNamespace", $"Template namespace mapping for '{prefix}' is not available.", offset, original.Length);
            }
            else
            {
                context.AddDiagnostic("InvalidTemplateName", "A MediaWiki template name was empty or invalid.", offset, original.Length);
            }
            return original;
        }

        if (!context.TryConsume(1)) return "[MediaWiki expansion limit exceeded]";
        TwMediaWikiTemplateTarget target;
        try
        {
            target = await context.Repository.ResolveTransclusionAsync(
                context.Provenance.ImportBatchId ?? 0,
                parsedTitle.NamespaceId,
                parsedTitle.Name,
                context.Provenance.SourceTimestampUtc,
                context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            context.Dependencies.Add(new(parsedTitle.CanonicalTitle, null, null, null, "ResolutionFailed"));
            context.AddUnsupported("template-resolution-failed");
            context.AddDiagnostic("TemplateResolutionFailed", $"MediaWiki transclusion '{parsedTitle.CanonicalTitle}' could not be resolved.", offset, original.Length, MediaWikiDiagnosticSeverity.Error);
            context.RecordTerminalFailure();
            return original;
        }
        context.Dependencies.Add(new(target.SourceTitle, target.SourcePageId, target.SourceRevisionId, target.TargetRevision, target.Status.ToString()));
        if (target.Status == TwMediaWikiTemplateResolutionStatus.Resolved && !target.IsResolved)
        {
            context.AddUnsupported("template-source-unavailable");
            context.AddDiagnostic("TemplateSourceUnavailable", $"MediaWiki transclusion '{parsedTitle.CanonicalTitle}' had no available source body for its resolved revision.", offset, original.Length);
            return original;
        }
        if (!target.IsResolved)
        {
            var code = target.Status switch
            {
                TwMediaWikiTemplateResolutionStatus.Restricted => "RestrictedTemplate",
                TwMediaWikiTemplateResolutionStatus.Ambiguous => "AmbiguousTemplate",
                _ => "MissingTemplate"
            };
            context.AddUnsupported(code.ToLowerInvariant());
            context.AddDiagnostic(code, $"MediaWiki transclusion '{parsedTitle.CanonicalTitle}' was not safely resolved for this imported source.", offset, original.Length);
            return original;
        }

        var templateKey = target.SourcePageId + ":" + target.SourceRevisionId;
        if (templateStack.Contains(templateKey, StringComparer.Ordinal))
        {
            context.AddUnsupported("recursive-template");
            context.AddDiagnostic("RecursiveTemplate", $"Recursive MediaWiki template call '{parsedTitle.CanonicalTitle}' was stopped.", offset, original.Length, MediaWikiDiagnosticSeverity.Error);
            return original;
        }

        var values = await ParseParametersAsync(parts.Skip(1), context, parentParameters, templateStack, depth);
        var bodyWithInclusion = ApplyInclusion(target.Body!, true, context);
        return await ExpandTextAsync(bodyWithInclusion, context, values, templateStack.Append(templateKey).ToArray(), depth + 1);
    }

    private async Task<string> ExpandParserFunctionAsync(
        string head,
        IReadOnlyList<string> rawArguments,
        string original,
        ExpansionContext context,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<string> templateStack,
        int depth,
        int offset)
    {
        var colon = head.IndexOf(':');
        var function = (colon < 0 ? head[1..] : head[1..colon]).Trim().ToLowerInvariant();
        var first = colon < 0 ? string.Empty : head[(colon + 1)..];
        var args = new List<string> { first };
        args.AddRange(rawArguments);
        if (function == "cuinfobox")
            return await ExpandInfoboxInvocationAsync(first, rawArguments, context, parameters, templateStack, depth, offset);
        if (function is "invoke" or "len" or "replace" or "var" or "dpl" or "categorytree" or "string")
        {
            if (function == "invoke")
            {
                return await ExpandScribuntoInvocationAsync(head, rawArguments, original, context, templateStack, depth, offset);
            }
            context.AddUnsupported("unsupported-extension-parser-function");
            context.AddDiagnostic("UnsupportedExtensionFunction", $"MediaWiki extension parser function '#{function}' is not enabled.", offset, original.Length);
            context.AddDiagnostic("UnsupportedParserFunction", $"MediaWiki parser function '#{function}' is not supported.", offset, original.Length);
            return original;
        }
        if (function is not ("if" or "ifeq" or "switch" or "expr" or "ifexpr" or "titleparts" or "rel2abs" or "time"))
        {
            context.AddUnsupported("parser-function");
            context.AddDiagnostic("UnsupportedParserFunction", $"MediaWiki parser function '#{function}' is not supported.", offset, original.Length);
            return original;
        }

        if (function is "titleparts" or "rel2abs" or "time")
        {
            var expandedArgs = new List<string>();
            foreach (var argument in args)
                expandedArgs.Add(await ExpandTextAsync(argument, context, parameters, templateStack, depth + 1));
            return function switch
            {
                "titleparts" => ExpandTitleParts(expandedArgs, context, offset, original.Length),
                "rel2abs" => ExpandRelativePath(expandedArgs, context, offset, original.Length),
                "time" => ExpandTime(expandedArgs, context, offset, original.Length),
                _ => original
            };
        }

        if (function == "expr")
        {
            var expression = await ExpandTextAsync(args[0], context, parameters, templateStack, depth + 1);
            if (!RestrictedExpression.TryEvaluate(expression, context.Options.MaxExpressionTokens, out var value, out var error))
            {
                context.AddDiagnostic("InvalidArithmeticExpression", error ?? "The arithmetic expression was rejected.", offset, original.Length);
                return original;
            }
            return value.ToString("G29", CultureInfo.InvariantCulture);
        }

        if (function == "ifexpr")
        {
            var expression = await ExpandTextAsync(args[0], context, parameters, templateStack, depth + 1);
            if (!RestrictedExpression.TryEvaluate(expression, context.Options.MaxExpressionTokens, out var value, out var error))
            {
                context.AddDiagnostic("InvalidArithmeticExpression", error ?? "The arithmetic expression was rejected.", offset, original.Length);
                return original;
            }
            return await ExpandSelectedAsync(value != 0m ? args.ElementAtOrDefault(1) : args.ElementAtOrDefault(2), context, parameters, templateStack, depth);
        }

        if (function == "if")
        {
            var condition = await ExpandTextAsync(args[0], context, parameters, templateStack, depth + 1);
            return await ExpandSelectedAsync(!string.IsNullOrWhiteSpace(condition), args.ElementAtOrDefault(1), args.ElementAtOrDefault(2), context, parameters, templateStack, depth);
        }

        if (function == "ifeq")
        {
            var left = await ExpandTextAsync(args[0], context, parameters, templateStack, depth + 1);
            var right = await ExpandTextAsync(args.ElementAtOrDefault(1) ?? string.Empty, context, parameters, templateStack, depth + 1);
            return await ExpandSelectedAsync(string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal), args.ElementAtOrDefault(2), args.ElementAtOrDefault(3), context, parameters, templateStack, depth);
        }

        var switchValue = (await ExpandTextAsync(args[0], context, parameters, templateStack, depth + 1)).Trim();
        string? selected = null;
        string? fallback = null;
        var fallThroughMatch = false;
        foreach (var argument in args.Skip(1))
        {
            var equals = FindTopLevelEquals(argument);
            if (equals < 0)
            {
                if (fallback is null && string.Equals(argument.Trim(), "#default", StringComparison.OrdinalIgnoreCase)) fallback = string.Empty;
                else if (string.Equals(argument.Trim(), switchValue, StringComparison.Ordinal)) fallThroughMatch = true;
                continue;
            }
            var key = argument[..equals].Trim();
            var value = argument[(equals + 1)..];
            if (string.Equals(key, "#default", StringComparison.OrdinalIgnoreCase)) fallback = value;
            else if (fallThroughMatch || string.Equals(key, switchValue, StringComparison.Ordinal)) { selected = value; break; }
        }
        return await ExpandSelectedAsync(selected ?? fallback, context, parameters, templateStack, depth);
    }

    private async Task<string> ExpandInfoboxInvocationAsync(
        string type,
        IReadOnlyList<string> rawArguments,
        ExpansionContext context,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<string> templateStack,
        int depth,
        int offset)
    {
        var actualType = string.IsNullOrWhiteSpace(type) ? "generic" : type.Trim();
        var values = new List<MediaWikiInfoboxArgument>();
        var positional = 1;
        foreach (var argument in rawArguments)
        {
            var equals = FindTopLevelEquals(argument);
            if (equals >= 0)
            {
                var name = argument[..equals].Trim();
                if (name.Length == 0) continue;
                values.Add(new(name, await ExpandTextAsync(argument[(equals + 1)..].Trim(), context, parameters, templateStack, depth + 1)));
            }
            else
            {
                values.Add(new(positional.ToString(CultureInfo.InvariantCulture), await ExpandTextAsync(argument.Trim(), context, parameters, templateStack, depth + 1)));
                positional++;
            }
        }

        if (context.Options.InfoboxRegistry is null)
        {
            context.AddUnsupported("continuum-infobox");
            context.AddDiagnostic("UnsupportedContinuumInfobox", "The ContinuumInfobox schema registry is not enabled.", offset, Math.Max(1, type.Length));
            return "";
        }
        if (!context.Options.InfoboxRegistry.TryGet(actualType, out _))
            context.AddDiagnostic("InfoboxSchemaFallback", $"Infobox schema '{actualType}' was not found; the bounded generic schema was used.", offset, Math.Max(1, type.Length));
        if (!MediaWikiInfoboxCompatibility.TryBuildModel(actualType, values, context.Options.InfoboxRegistry,
                context.Options.MaxInfoboxFields, context.Options.MaxInfoboxCharacters, out var model, out var error))
        {
            context.AddDiagnostic("InfoboxLimitExceeded", error, offset, Math.Max(1, type.Length), MediaWikiDiagnosticSeverity.Error);
            return "";
        }
        if (string.IsNullOrWhiteSpace(model.Title))
            model = model with { Title = string.IsNullOrWhiteSpace(context.Expansion.CurrentPageTitle) ? model.Label : context.Expansion.CurrentPageTitle };
        return MediaWikiInfoboxCompatibility.Encode(model);
    }

    private async Task<string> ExpandScribuntoInvocationAsync(
        string head,
        IReadOnlyList<string> rawArguments,
        string original,
        ExpansionContext context,
        IReadOnlyList<string> templateStack,
        int depth,
        int offset)
    {
        var body = string.Join('|', new[] { head }.Concat(rawArguments));
        if (!MediaWikiScribuntoInvocationParser.TryParse(original, body, context.Expansion, templateStack, offset, out var invocation))
        {
            context.AddUnsupported("malformed-scribunto-invocation");
            context.AddDiagnostic("MalformedScribuntoInvocation", "The Scribunto #invoke syntax could not be represented by the typed boundary.", offset, original.Length);
            return original;
        }
        var execution = await context.Options.ScribuntoExecutor.ExecuteAsync(invocation, context, new MediaWikiScribuntoExecutionContext(
            new(invocation.CallingPageTitle, invocation.CallingSourcePageId, invocation.CallingSourceRevisionId,
                context.Expansion.RevisionTimestampUtc, context.Expansion.ContentModel,
                new MediaWikiSourceWikiIdentity(context.Expansion.SourceWikiIdentity ?? string.Empty)),
            new MediaWikiScribuntoExecutionBudget(
                MaxInvocations: context.Options.MaxTemplateExpansionDepth,
                MaxModuleDependencyDepth: context.Options.MaxTemplateExpansionDepth,
                MaxModuleSourceCharacters: context.Options.MaxInputCharacters,
                MaxArgumentCharacters: context.Options.MaxInputCharacters,
                MaxResultCharacters: context.Options.MaxExpansionOutputCharacters,
                MaxInstructionCount: context.Options.MaxExpansionOperations,
                MaxExecutionTime: context.Options.MaxExpansionTime),
            templateStack.Count,
            templateStack));
        foreach (var diagnostic in execution.Diagnostics) context.AddDiagnostic(diagnostic.Code, diagnostic.Message, diagnostic.Offset, diagnostic.Length, diagnostic.Severity);
        foreach (var dependency in execution.Dependencies) context.ScribuntoDependencies.Add(dependency);
        context.RecordExecutionFailure(execution);
        if (!execution.Executed) context.AddUnsupported("scribunto-execution-rejected");
        if (!execution.Executed || execution.Text is null) return original;

        // Lua output is Wikitext, not trusted HTML. Re-enter the same bounded
        // expander so templates, parser functions, and nested #invoke calls
        // share the outer operation budget and recursion context.
        var invocationKey = $"invoke:{invocation.Module.CanonicalTitle}:{invocation.ExportedFunction}";
        if (templateStack.Contains(invocationKey, StringComparer.Ordinal))
        {
            context.AddUnsupported("recursive-scribunto-invocation");
            context.AddDiagnostic("RecursiveScribuntoInvocation", $"Recursive MediaWiki #invoke call '{invocationKey}' was stopped.", offset, original.Length, MediaWikiDiagnosticSeverity.Error);
            return original;
        }

        return await ExpandTextAsync(
            execution.Text,
            context,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            templateStack.Append(invocationKey).ToArray(),
            depth + 1);
    }

    private async Task<(bool Handled, string Value)> ExpandSpecialInvocationAsync(
        string head,
        IReadOnlyList<string> rawArguments,
        string original,
        ExpansionContext context,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<string> templateStack,
        int depth,
        int offset)
    {
        var colon = head.IndexOf(':');
        var name = (colon < 0 ? head : head[..colon]).Trim();
        var key = name.ToUpperInvariant();
        var argument = colon < 0 ? null : head[(colon + 1)..];
        var args = new List<string>();
        if (argument is not null) args.Add(argument);
        args.AddRange(rawArguments);

        if (key.StartsWith("#", StringComparison.Ordinal)) return (false, string.Empty);
        if (key is "PAGENAME" or "PAGENAMEE" or "FULLPAGENAME" or "FULLPAGENAMEE" or "BASEPAGENAME" or "BASEPAGENAMEE" or "SUBPAGENAME" or "SUBPAGENAMEE" or "ROOTPAGENAME" or "ROOTPAGENAMEE" or "NAMESPACE" or "NAMESPACENUMBER" or "TALKPAGENAME" or "TALKPAGENAMEE" or "SUBJECTPAGENAME" or "SUBJECTPAGENAMEE")
            return (true, ExpandTitleMagicWord(key, argument, context, offset, original.Length));
        if (key is "SITENAME" or "CONTENTLANGUAGE" or "REVISIONID" or "REVISIONTIMESTAMP" or "REVISIONYEAR" or "REVISIONMONTH" or "REVISIONDAY")
            return (true, ExpandMetadataMagicWord(key, context, offset, original.Length));
        if (key is "CURRENTYEAR" or "CURRENTMONTH" or "CURRENTDAY" or "CURRENTDAY2" or "CURRENTTIME" or "CURRENTTIMESTAMP" or "LOCALYEAR" or "LOCALMONTH" or "LOCALDAY" or "LOCALDAY2" or "LOCALTIME" or "LOCALTIMESTAMP")
            return (true, ExpandClockMagicWord(key, context));

        if (key is "DISPLAYTITLE" or "DEFAULTSORT" or "DEFAULTCATEGORYSORT" or "TOC" or "NOTOC" or "FORCETOC" or "CURRENTVERSION" or "REVISIONUSER" or "REVISIONUSER2")
        {
            context.AddUnsupported("unsupported-magic-word");
            context.AddDiagnostic("UnsupportedMagicWord", $"MediaWiki magic word '{name}' is not enabled.", offset, original.Length);
            return (true, original);
        }

        if (key is "LC" or "UC" or "LCFIRST" or "UCFIRST" or "PADLEFT" or "PADRIGHT" or "FORMATNUM")
        {
            var value = args.Count == 0 ? string.Empty : await ExpandTextAsync(args[0], context, parameters, templateStack, depth + 1);
            return (true, ExpandStringFunction(key, value, args.Skip(1).ToArray(), context, offset, original.Length));
        }

        return (false, string.Empty);
    }

    private string ExpandTitleMagicWord(string key, string? explicitTitle, ExpansionContext context, int offset, int length)
    {
        var title = string.IsNullOrWhiteSpace(explicitTitle) ? context.Expansion.CurrentPageTitle : explicitTitle.Trim().Replace('_', ' ');
        var parsed = ParseTitle(title, context.Expansion.CurrentNamespaceId);
        var page = parsed.PageName;
        var namespaceId = parsed.NamespaceId;
        var isEncoded = key is "PAGENAMEE" or "FULLPAGENAMEE" or "BASEPAGENAMEE" or "SUBPAGENAMEE" or "ROOTPAGENAMEE" or "TALKPAGENAMEE" or "SUBJECTPAGENAMEE";
        var baseKey = isEncoded ? key[..^1] : key;
        var value = baseKey switch
        {
            "FULLPAGENAME" => parsed.FullTitle,
            "PAGENAME" => page,
            "BASEPAGENAME" => page[..Math.Max(0, page.LastIndexOf('/'))].TrimEnd('/'),
            "SUBPAGENAME" => page[(page.LastIndexOf('/') + 1)..],
            "ROOTPAGENAME" => page.Split('/')[0],
            "NAMESPACE" => NamespaceName(namespaceId),
            "NAMESPACENUMBER" => namespaceId.ToString(CultureInfo.InvariantCulture),
            "TALKPAGENAME" => BuildRelatedTitle(parsed, true),
            "SUBJECTPAGENAME" => BuildRelatedTitle(parsed, false),
            _ => string.Empty
        };
        if (value.Length == 0 && (key is "BASEPAGENAME" or "BASEPAGENAMEE")) value = page;
        return isEncoded ? EncodeTitle(value) : value;
    }

    private string ExpandMetadataMagicWord(string key, ExpansionContext context, int offset, int length)
    {
        var timestamp = context.Expansion.RevisionTimestampUtc;
        if (key == "SITENAME") return context.Expansion.SiteName;
        if (key == "CONTENTLANGUAGE") return context.Expansion.ContentLanguage;
        if (key == "REVISIONID") return RequireMetadata(context.Expansion.SourceRevisionId, "RevisionId", context, offset, length);
        if (timestamp is null)
        {
            context.AddDiagnostic("MissingRevisionMetadata", $"MediaWiki metadata '{key}' has no historical source timestamp.", offset, length);
            return string.Empty;
        }
        return key switch
        {
            "REVISIONTIMESTAMP" => timestamp.Value.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            "REVISIONYEAR" => timestamp.Value.Year.ToString(CultureInfo.InvariantCulture),
            "REVISIONMONTH" => timestamp.Value.Month.ToString("00", CultureInfo.InvariantCulture),
            "REVISIONDAY" => timestamp.Value.Day.ToString("00", CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    private static string RequireMetadata(string? value, string name, ExpansionContext context, int offset, int length)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value;
        context.AddDiagnostic("MissingRevisionMetadata", $"MediaWiki metadata '{name}' was unavailable.", offset, length);
        return string.Empty;
    }

    private static string ExpandClockMagicWord(string key, ExpansionContext context)
    {
        context.DependsOnCurrentTime = true;
        var utc = context.Expansion.NowUtc.UtcDateTime;
        var local = TimeZoneInfo.ConvertTime(context.Expansion.NowUtc, context.Expansion.TimeZone).DateTime;
        var value = key.StartsWith("LOCAL", StringComparison.Ordinal) ? local : utc;
        return key switch
        {
            "CURRENTYEAR" or "LOCALYEAR" => value.ToString("yyyy", CultureInfo.InvariantCulture),
            "CURRENTMONTH" or "LOCALMONTH" => value.ToString("MM", CultureInfo.InvariantCulture),
            "CURRENTDAY" or "LOCALDAY" => value.ToString("d", CultureInfo.InvariantCulture),
            "CURRENTDAY2" or "LOCALDAY2" => value.ToString("dd", CultureInfo.InvariantCulture),
            "CURRENTTIME" or "LOCALTIME" => value.ToString("HHmmss", CultureInfo.InvariantCulture),
            "CURRENTTIMESTAMP" or "LOCALTIMESTAMP" => value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    private string ExpandStringFunction(string key, string value, IReadOnlyList<string> args, ExpansionContext context, int offset, int length)
    {
        var culture = context.Expansion.Culture;
        if (key == "LC") return value.ToLower(culture);
        if (key == "UC") return value.ToUpper(culture);
        if (key == "LCFIRST") return value.Length == 0 ? value : value[..1].ToLower(culture) + value[1..];
        if (key == "UCFIRST") return value.Length == 0 ? value : value[..1].ToUpper(culture) + value[1..];
        if (key is "PADLEFT" or "PADRIGHT")
        {
            if (!int.TryParse(args.ElementAtOrDefault(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width < 0 || width > context.Options.MaxFunctionOutputCharacters)
            {
                context.AddDiagnostic("InvalidPaddingWidth", "MediaWiki padding width was rejected by the output limit.", offset, length);
                return value;
            }
            var pad = args.ElementAtOrDefault(1) ?? "0";
            if (pad.Length == 0) pad = "0";
            var count = Math.Max(0, width - value.Length);
            var padding = string.Concat(Enumerable.Repeat(pad, (count + pad.Length - 1) / pad.Length))[..count];
            return key == "PADLEFT" ? padding + value : value + padding;
        }
        if (key == "FORMATNUM")
        {
            var raw = args.FirstOrDefault()?.Trim();
            if (raw is "R" or "NOSEP") return value;
            if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number.ToString("N", culture).TrimEnd('0').TrimEnd(culture.NumberFormat.NumberDecimalSeparator.ToCharArray());
            context.AddDiagnostic("InvalidFormatNumber", "MediaWiki formatnum input was not numeric.", offset, length);
        }
        return value;
    }

    private static string ExpandTitleParts(IReadOnlyList<string> args, ExpansionContext context, int offset, int length)
    {
        var title = args.ElementAtOrDefault(0) ?? string.Empty;
        var parts = title.Split('/');
        if (!int.TryParse(args.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) count = parts.Length;
        if (!int.TryParse(args.ElementAtOrDefault(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) start = 1;
        if (count == 0) return string.Empty;
        var from = start > 0 ? start - 1 : Math.Max(0, parts.Length + start);
        if (from >= parts.Length || from < 0) return string.Empty;
        var selected = count > 0 ? parts.Skip(from).Take(count) : parts.Skip(Math.Max(0, parts.Length + count)).Take(Math.Max(0, from - (parts.Length + count) + 1));
        var value = string.Join('/', selected);
        return value.Length <= context.Options.MaxFunctionOutputCharacters ? value : LimitedFunctionOutput(context, value, offset, length);
    }

    private static string ExpandRelativePath(IReadOnlyList<string> args, ExpansionContext context, int offset, int length)
    {
        var relative = args.ElementAtOrDefault(0)?.Trim() ?? string.Empty;
        var baseTitle = args.ElementAtOrDefault(1)?.Trim();
        if (baseTitle is null) baseTitle = context.Expansion.CurrentPageTitle;
        var root = baseTitle.Replace('\\', '/').Split('/').ToList();
        if (root.Count > 0) root.RemoveAt(root.Count - 1);
        foreach (var part in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (root.Count == 0) { context.AddDiagnostic("UnsafeRelativePath", "MediaWiki relative path escaped its title root.", offset, length); return string.Empty; }
                root.RemoveAt(root.Count - 1);
            }
            else if (part.IndexOf('\0') >= 0) { context.AddDiagnostic("UnsafeRelativePath", "MediaWiki relative path contained a NUL character.", offset, length); return string.Empty; }
            else root.Add(part);
        }
        var value = string.Join('/', root);
        return value.Length <= context.Options.MaxFunctionOutputCharacters ? value : LimitedFunctionOutput(context, value, offset, length);
    }

    private static string ExpandTime(IReadOnlyList<string> args, ExpansionContext context, int offset, int length)
    {
        var format = args.ElementAtOrDefault(0) ?? string.Empty;
        DateTime value;
        var timestamp = args.ElementAtOrDefault(1)?.Trim();
        if (string.IsNullOrWhiteSpace(timestamp))
        {
            context.DependsOnCurrentTime = true;
            value = context.Expansion.NowUtc.UtcDateTime;
        }
        else if (timestamp.StartsWith('@') && long.TryParse(timestamp[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 4102444800)
        {
            value = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        else if (!DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
        {
            context.AddDiagnostic("InvalidTimeValue", "MediaWiki #time timestamp was rejected.", offset, length);
            return string.Empty;
        }
        var output = new StringBuilder();
        for (var index = 0; index < format.Length; index++)
        {
            if (format[index] != '%') { output.Append(format[index]); continue; }
            if (++index >= format.Length) { context.AddDiagnostic("UnsupportedTimeFormat", "MediaWiki #time contained an incomplete format token.", offset, length); return string.Empty; }
            var token = format[index];
            var replacement = token switch
            {
                'Y' => value.ToString("yyyy", CultureInfo.InvariantCulture),
                'y' => value.ToString("yy", CultureInfo.InvariantCulture),
                'm' => value.ToString("MM", CultureInfo.InvariantCulture),
                'n' => value.ToString("M", CultureInfo.InvariantCulture),
                'd' => value.ToString("dd", CultureInfo.InvariantCulture),
                'j' => value.ToString("d", CultureInfo.InvariantCulture),
                'H' => value.ToString("HH", CultureInfo.InvariantCulture),
                'G' => value.ToString("H", CultureInfo.InvariantCulture),
                'i' => value.ToString("mm", CultureInfo.InvariantCulture),
                's' => value.ToString("ss", CultureInfo.InvariantCulture),
                'c' => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                'U' => new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                '%' => "%",
                _ => null
            };
            if (replacement is null) { context.AddDiagnostic("UnsupportedTimeFormat", $"MediaWiki #time token '%{token}' is not enabled.", offset, length); return string.Empty; }
            output.Append(replacement);
            if (output.Length > context.Options.MaxFunctionOutputCharacters) return LimitedFunctionOutput(context, output.ToString(), offset, length);
        }
        return output.ToString();
    }

    private static string LimitedFunctionOutput(ExpansionContext context, string value, int offset, int length)
    {
        context.AddDiagnostic("FunctionOutputLimitExceeded", "MediaWiki function output exceeded the configured limit.", offset, length, MediaWikiDiagnosticSeverity.Error);
        return value[..context.Options.MaxFunctionOutputCharacters];
    }

    private static (int NamespaceId, string PageName, string FullTitle) ParseTitle(string title, string? fallbackNamespace)
    {
        var normalized = title.Trim().Replace('_', ' ');
        var separator = normalized.IndexOf(':');
        var namespaceId = int.TryParse(fallbackNamespace, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFallback) ? parsedFallback : 0;
        if (separator > 0 && TryNamespaceId(normalized[..separator], out var parsed)) { namespaceId = parsed; normalized = normalized[(separator + 1)..]; }
        return (namespaceId, normalized, namespaceId == 0 ? normalized : NamespaceName(namespaceId) + ":" + normalized);
    }

    private static string BuildRelatedTitle((int NamespaceId, string PageName, string FullTitle) parsed, bool talk)
    {
        var id = parsed.NamespaceId;
        var isTalk = id % 2 == 1 || id == 829;
        var subject = isTalk ? Math.Max(0, id - 1) : id;
        var target = talk ? subject + 1 : subject;
        return target == 0 ? parsed.PageName : NamespaceName(target) + ":" + parsed.PageName;
    }

    private static string NamespaceName(int id) => id switch
    {
        0 => string.Empty, 1 => "Talk", 2 => "User", 3 => "User talk", 4 => "Project", 5 => "Project talk",
        6 => "File", 7 => "File talk", 8 => "MediaWiki", 9 => "MediaWiki talk", 10 => "Template", 11 => "Template talk",
        12 => "Help", 13 => "Help talk", 14 => "Category", 15 => "Category talk", 828 => "Module", 829 => "Module talk", _ => id.ToString(CultureInfo.InvariantCulture)
    };

    private static bool TryNamespaceId(string name, out int id)
    {
        var normalized = name.Trim().Replace('_', ' ');
        for (var candidate = 0; candidate <= 829; candidate++)
            if (string.Equals(NamespaceName(candidate), normalized, StringComparison.OrdinalIgnoreCase)) { id = candidate; return true; }
        if (string.Equals(normalized, "Image", StringComparison.OrdinalIgnoreCase)) { id = 6; return true; }
        id = 0; return false;
    }

    private static string EncodeTitle(string value) => Uri.EscapeDataString(value).Replace("%20", "_");

    private async Task<string> ExpandSelectedAsync(
        string? value,
        ExpansionContext context,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<string> templateStack,
        int depth)
        => value is null ? string.Empty : await ExpandTextAsync(value, context, parameters, templateStack, depth + 1);

    private async Task<string> ExpandSelectedAsync(
        bool selected,
        string? yes,
        string? no,
        ExpansionContext context,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<string> templateStack,
        int depth)
        => await ExpandSelectedAsync(selected ? yes : no, context, parameters, templateStack, depth);

    private async Task<Dictionary<string, string>> ParseParametersAsync(
        IEnumerable<string> arguments,
        ExpansionContext context,
        IReadOnlyDictionary<string, string> parentParameters,
        IReadOnlyList<string> templateStack,
        int depth)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = 1;
        foreach (var argument in arguments)
        {
            var equals = FindTopLevelEquals(argument);
            if (equals >= 0)
            {
                var name = argument[..equals].Trim();
                if (name.Length > 0)
                    values[name] = await ExpandTextAsync(argument[(equals + 1)..].Trim(), context, parentParameters, templateStack, depth + 1);
            }
            else
            {
                values[positional.ToString(CultureInfo.InvariantCulture)] = await ExpandTextAsync(argument.Trim(), context, parentParameters, templateStack, depth + 1);
                positional++;
            }
        }
        return values;
    }

    private static string ApplyInclusion(string source, bool transcluding, ExpansionContext? context = null)
    {
        var only = ExtractTagBodies(source, "onlyinclude", context, out var hadOnly);
        if (transcluding && hadOnly) return string.Join('\n', only);

        var result = source;
        result = transcluding
            ? RemoveTagBodies(result, "noinclude", context)
            : RemoveTagBodies(result, "includeonly", context);
        result = StripTags(result, "onlyinclude", context);
        result = StripTags(result, transcluding ? "includeonly" : "noinclude", context);
        return result;
    }

    private static List<string> ExtractTagBodies(string source, string tag, ExpansionContext? context, out bool found)
    {
        var results = new List<string>();
        var index = 0;
        found = false;
        while (TryFindTag(source, tag, index, out var openStart, out var openEnd, out var closeStart, out var closeEnd))
        {
            found = true;
            results.Add(source[(openEnd + 1)..closeStart]);
            index = closeEnd + 1;
        }
        if (!found && source.Contains('<' + tag, StringComparison.OrdinalIgnoreCase))
            context?.AddDiagnostic("MalformedInclusionTag", $"Malformed <{tag}> inclusion boundary was preserved.", 0, source.Length);
        return results;
    }

    private static string RemoveTagBodies(string source, string tag, ExpansionContext? context)
    {
        var output = new StringBuilder(source);
        while (TryFindTag(output.ToString(), tag, 0, out var openStart, out _, out var closeStart, out var closeEnd))
            output.Remove(openStart, closeEnd - openStart + 1);
        if (source.Contains('<' + tag, StringComparison.OrdinalIgnoreCase) && !source.Contains("</" + tag + ">", StringComparison.OrdinalIgnoreCase))
            context?.AddDiagnostic("MalformedInclusionTag", $"Malformed <{tag}> inclusion boundary was preserved.", 0, source.Length);
        return output.ToString();
    }

    private static string StripTags(string source, string tag, ExpansionContext? context)
    {
        var result = new StringBuilder(source);
        var marker = "<" + tag;
        var index = 0;
        while ((index = result.ToString().IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var close = result.ToString().IndexOf('>', index + marker.Length);
            if (close < 0) break;
            result.Remove(index, close - index + 1);
        }
        return result.ToString().Replace("</" + tag + ">", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFindTag(string text, string tag, int start, out int openStart, out int openEnd, out int closeStart, out int closeEnd)
    {
        openStart = text.IndexOf('<' + tag, start, StringComparison.OrdinalIgnoreCase);
        if (openStart < 0) { openEnd = closeStart = closeEnd = -1; return false; }
        openEnd = text.IndexOf('>', openStart + tag.Length + 1);
        closeStart = openEnd < 0 ? -1 : text.IndexOf("</" + tag + ">", openEnd + 1, StringComparison.OrdinalIgnoreCase);
        closeEnd = closeStart < 0 ? -1 : closeStart + tag.Length + 2;
        return openEnd >= 0 && closeStart >= 0;
    }

    private static int FindNextConstruct(string text, int start)
    {
        var template = text.IndexOf("{{", start, StringComparison.Ordinal);
        var parameter = text.IndexOf("{{{", start, StringComparison.Ordinal);
        if (parameter >= 0 && (template < 0 || parameter <= template)) return parameter;
        return template < 0 ? text.Length : template;
    }

    private static bool TryReadConstruct(string text, int start, string open, string close, out int end, out string body)
    {
        var stack = new List<string> { open };
        for (var index = start + open.Length; index < text.Length; index++)
        {
            if (text.AsSpan(index).StartsWith("{{{", StringComparison.Ordinal)) { stack.Add("{{{"); index += 2; continue; }
            if (text.AsSpan(index).StartsWith("{{", StringComparison.Ordinal)) { stack.Add("{{"); index++; continue; }
            if (stack[^1] == "{{{" && text.AsSpan(index).StartsWith("}}}", StringComparison.Ordinal))
            {
                stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) { end = index; body = text[(start + open.Length)..end]; return true; }
                index += 2;
                continue;
            }
            if (stack[^1] == "{{" && text.AsSpan(index).StartsWith("}}", StringComparison.Ordinal))
            {
                stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) { end = index; body = text[(start + open.Length)..end]; return true; }
                index++;
            }
        }
        end = -1;
        body = string.Empty;
        return false;
    }

    private static List<string> SplitTopLevel(string value, char separator)
    {
        var result = new List<string>();
        var start = 0;
        var stack = new List<string>();
        var links = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value.AsSpan(index).StartsWith("{{{", StringComparison.Ordinal)) { stack.Add("{{{"); index += 2; continue; }
            if (value.AsSpan(index).StartsWith("{{", StringComparison.Ordinal)) { stack.Add("{{"); index++; continue; }
            if (value.AsSpan(index).StartsWith("[[", StringComparison.Ordinal)) { links++; index++; continue; }
            if (links > 0 && value.AsSpan(index).StartsWith("]]", StringComparison.Ordinal)) { links--; index++; continue; }
            if (stack.Count > 0 && stack[^1] == "{{{" && value.AsSpan(index).StartsWith("}}}", StringComparison.Ordinal)) { stack.RemoveAt(stack.Count - 1); index += 2; continue; }
            if (stack.Count > 0 && stack[^1] == "{{" && value.AsSpan(index).StartsWith("}}", StringComparison.Ordinal)) { stack.RemoveAt(stack.Count - 1); index++; continue; }
            if (value[index] == separator && stack.Count == 0 && links == 0) { result.Add(value[start..index]); start = index + 1; }
        }
        result.Add(value[start..]);
        return result;
    }

    private static int FindTopLevelEquals(string value)
    {
        var parts = SplitTopLevel(value, '=');
        return parts.Count == 1 ? -1 : parts[0].Length;
    }

    private static void AppendBounded(StringBuilder output, string value, ExpansionContext context)
    {
        if (output.Length >= context.Options.MaxExpansionOutputCharacters) return;
        var remaining = context.Options.MaxExpansionOutputCharacters - output.Length;
        if (value.Length <= remaining) output.Append(value);
        else
        {
            output.Append(value.AsSpan(0, remaining));
            context.AddDiagnostic("ExpansionOutputLimitExceeded", "MediaWiki template expansion exceeded the configured output budget.", 0, 0, MediaWikiDiagnosticSeverity.Error);
        }
    }

    private sealed class ExpansionContext(
        MediaWikiWikitextRenderOptions options,
        TwMediaWikiRenderSource provenance,
        ITwMediaWikiImportRepository repository,
        List<MediaWikiCompatibilityDiagnostic> diagnostics,
        List<MediaWikiTemplateDependency> dependencies,
        List<string> unsupported,
        CancellationToken cancellationToken,
        MediaWikiExpansionContext expansion) : IMediaWikiScribuntoExecutionHost
    {
        private static readonly Regex StaticModuleLoader = new("\\b(?:require|mw\\.loadData|mw\\.loadJsonData)\\s*\\(\\s*['\"]([^'\"]+)['\"]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _operations;
        private bool _budgetDiagnostic;
        public MediaWikiWikitextRenderOptions Options { get; } = options;
        public TwMediaWikiRenderSource Provenance { get; } = provenance;
        public ITwMediaWikiImportRepository Repository { get; } = repository;
        public List<MediaWikiCompatibilityDiagnostic> Diagnostics { get; } = diagnostics;
        public List<MediaWikiTemplateDependency> Dependencies { get; } = dependencies;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public MediaWikiExpansionContext Expansion { get; } = expansion;
        public bool DependsOnCurrentTime { get; set; }
        public List<MediaWikiScribuntoDependency> ScribuntoDependencies { get; } = [];
        public bool HasTerminalFailure { get; private set; }

        public bool TryConsume(int count)
        {
            _operations = checked(_operations + count);
            if (_operations <= Options.MaxExpansionOperations && _clock.Elapsed <= Options.MaxExpansionTime) return true;
            AddBudgetDiagnostic("ExpansionBudgetExceeded", "MediaWiki template expansion exceeded its operation or time budget.");
            return false;
        }

        public void ThrowIfCancelledOrOverBudget()
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (!TryConsume(1)) throw new ExpansionBudgetExceededException();
        }

        public void AddDiagnostic(string code, string message, int offset, int length, MediaWikiDiagnosticSeverity severity = MediaWikiDiagnosticSeverity.Warning)
        {
            if (Diagnostics.Count < Options.MaxDiagnostics) Diagnostics.Add(new(code, message, offset, length, 0, 0, severity));
        }

        public void AddUnsupported(string value)
        {
            if (!unsupported.Contains(value, StringComparer.Ordinal)) unsupported.Add(value);
        }

        public async Task<string?> ResolveModuleSourceAsync(MediaWikiModuleTitle module, DateTime? asOfUtc, CancellationToken cancellationToken = default)
            => (await Repository.ResolveModuleAsync(Provenance.ImportBatchId ?? 0, module.Name, asOfUtc, cancellationToken)).Body;

        public async Task<MediaWikiScribuntoModuleBundle?> ResolveModuleBundleAsync(
            MediaWikiModuleTitle module,
            DateTime? asOfUtc,
            CancellationToken cancellationToken = default)
        {
            var rootTarget = await Repository.ResolveModuleAsync(Provenance.ImportBatchId ?? 0, module.Name, asOfUtc, cancellationToken);
            if (!rootTarget.IsResolved || rootTarget.Body is null || rootTarget.SourcePageId is null || rootTarget.SourceRevisionId is null)
                return null;

            var root = CreateModuleSource(module, rootTarget, []);
            var resolved = new Dictionary<string, MediaWikiScribuntoModuleSource>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<MediaWikiScribuntoModuleSource>();
            pending.Enqueue(root);
            var rootDependencies = root;
            while (pending.Count > 0 && resolved.Count <= 1_024)
            {
                var current = pending.Dequeue();
                var references = StaticModuleLoader.Matches(current.Source)
                    .Select(match => match.Groups[1].Value)
                    .Select(value => MediaWikiModuleTitle.TryParse(value, out var parsed) ? parsed : null)
                    .Where(value => value is not null && value.NamespaceId == 828)
                    .Cast<MediaWikiModuleTitle>()
                    .DistinctBy(value => value.CanonicalTitle, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                current = current with { Dependencies = references };
                if (!resolved.TryAdd(current.Module.CanonicalTitle, current)) continue;
                if (string.Equals(current.Module.CanonicalTitle, module.CanonicalTitle, StringComparison.OrdinalIgnoreCase)) rootDependencies = current;
                foreach (var reference in references)
                {
                    if (resolved.ContainsKey(reference.CanonicalTitle)) continue;
                    var target = await Repository.ResolveModuleAsync(Provenance.ImportBatchId ?? 0, reference.Name, asOfUtc, cancellationToken);
                    if (!target.IsResolved || target.Body is null || target.SourcePageId is null || target.SourceRevisionId is null) continue;
                    pending.Enqueue(CreateModuleSource(reference, target, []));
                }
            }

            return new(rootDependencies, resolved.Values.Where(value => !string.Equals(value.Module.CanonicalTitle, module.CanonicalTitle, StringComparison.OrdinalIgnoreCase)).ToArray());
        }

        private MediaWikiScribuntoModuleSource CreateModuleSource(
            MediaWikiModuleTitle module,
            TwMediaWikiModuleTarget target,
            IReadOnlyList<MediaWikiModuleTitle> dependencies)
            => new(
                module,
                new MediaWikiSourceWikiIdentity(Expansion.SourceWikiIdentity ?? string.Empty),
                target.SourcePageId!,
                target.SourceRevisionId!,
                string.IsNullOrWhiteSpace(target.ContentModel) ? "Scribunto" : target.ContentModel!,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Body!))),
                target.Body!,
                dependencies);

        public async Task<string?> ResolveTemplateTextAsync(string templateTitle, DateTime? asOfUtc, CancellationToken cancellationToken = default)
            => (await Repository.ResolveTemplateAsync(Provenance.ImportBatchId ?? 0, templateTitle, asOfUtc, cancellationToken)).Body;

        public Task<MediaWikiScribuntoPageMetadata?> ResolvePageMetadataAsync(string title, DateTime? asOfUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<MediaWikiScribuntoPageMetadata?>(new(
                Expansion.CurrentPageTitle, Expansion.SourcePageId, Expansion.SourceRevisionId,
                Expansion.RevisionTimestampUtc, Expansion.ContentModel,
                new MediaWikiSourceWikiIdentity(Expansion.SourceWikiIdentity ?? string.Empty)));

        public void RegisterDependency(MediaWikiScribuntoDependency dependency) => ScribuntoDependencies.Add(dependency);

        public void RecordExecutionFailure(MediaWikiScribuntoExecutionResult execution)
        {
            if (HasTerminalFailure || execution.Executed) return;

            // The rejecting executor deliberately reports a warning and stays
            // recoverable. An error-valued typed result has no valid Wikitext
            // result, so later expansion can only create secondary evidence
            // (for example, a misleading wall-clock timeout).
            if (execution.Diagnostics.Any(value => value.Severity == MediaWikiDiagnosticSeverity.Error))
                HasTerminalFailure = true;
        }

        public void RecordTerminalFailure() => HasTerminalFailure = true;

        private void AddBudgetDiagnostic(string code, string message)
        {
            if (_budgetDiagnostic) return;
            _budgetDiagnostic = true;
            AddDiagnostic(code, message, 0, 0, MediaWikiDiagnosticSeverity.Error);
        }
    }

    private sealed class ExpansionBudgetExceededException : Exception;

    private sealed class RestrictedExpression
    {
        private readonly string _text;
        private readonly int _maxTokens;
        private int _index;
        private int _tokens;

        private RestrictedExpression(string text, int maxTokens) { _text = text; _maxTokens = maxTokens; }

        public static bool TryEvaluate(string text, int maxTokens, out decimal value, out string? error)
        {
            value = 0;
            error = null;
            if (text.Length == 0 || text.Length > 512) { error = "The arithmetic expression is empty or too long."; return false; }
            var parser = new RestrictedExpression(text, maxTokens);
            try
            {
                value = parser.ParseComparison();
                parser.SkipWhitespace();
                if (parser._index != parser._text.Length) throw new FormatException("Unsupported arithmetic token.");
                return true;
            }
            catch (DivideByZeroException) { error = "Division by zero is not allowed."; return false; }
            catch (OverflowException) { error = "Arithmetic overflow is not allowed."; return false; }
            catch (FormatException ex) { error = ex.Message; return false; }
        }

        private decimal ParseComparison()
        {
            var left = ParseAdditive();
            while (true)
            {
                SkipWhitespace();
                var op = Match("==") ? "==" : Match("!=") ? "!=" : Match("<=") ? "<=" : Match(">=") ? ">=" : Match("<") ? "<" : Match(">") ? ">" : null;
                if (op is null) return left;
                var right = ParseAdditive();
                left = op switch { "==" => left == right ? 1 : 0, "!=" => left != right ? 1 : 0, "<" => left < right ? 1 : 0, "<=" => left <= right ? 1 : 0, ">" => left > right ? 1 : 0, ">=" => left >= right ? 1 : 0, _ => 0 };
            }
        }

        private decimal ParseAdditive()
        {
            var value = ParseMultiplicative();
            while (true)
            {
                SkipWhitespace();
                if (Match("+")) value = checked(value + ParseMultiplicative());
                else if (Match("-")) value = checked(value - ParseMultiplicative());
                else return value;
            }
        }

        private decimal ParseMultiplicative()
        {
            var value = ParseUnary();
            while (true)
            {
                SkipWhitespace();
                if (Match("*")) value = checked(value * ParseUnary());
                else if (Match("/")) value = checked(value / ParseUnary());
                else if (Match("%")) value = checked(value % ParseUnary());
                else return value;
            }
        }

        private decimal ParseUnary()
        {
            SkipWhitespace();
            if (Match("+")) return ParseUnary();
            if (Match("-")) return checked(-ParseUnary());
            if (Match("(")) { var value = ParseComparison(); if (!Match(")")) throw new FormatException("Missing closing parenthesis."); return value; }
            return ParseNumber();
        }

        private decimal ParseNumber()
        {
            SkipWhitespace();
            var start = _index;
            while (_index < _text.Length && (char.IsDigit(_text[_index]) || _text[_index] == '.')) { _index++; _tokens++; if (_tokens > _maxTokens) throw new FormatException("Arithmetic expression token limit exceeded."); }
            if (start == _index || !decimal.TryParse(_text[start.._index], NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) throw new FormatException("Invalid numeric literal.");
            return value;
        }

        private bool Match(string token)
        {
            SkipWhitespace();
            if (!_text.AsSpan(_index).StartsWith(token, StringComparison.Ordinal)) return false;
            _index += token.Length;
            _tokens++;
            if (_tokens > _maxTokens) throw new FormatException("Arithmetic expression token limit exceeded.");
            return true;
        }

        private void SkipWhitespace() { while (_index < _text.Length && char.IsWhiteSpace(_text[_index])) _index++; }
    }
}
