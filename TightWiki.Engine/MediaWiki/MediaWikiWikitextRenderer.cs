using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

public sealed class MediaWikiWikitextRenderOptions
{
    public int MaxInputCharacters { get; init; } = 1_000_000;
    public int MaxOutputCharacters { get; init; } = 1_500_000;
    public int MaxParseDepth { get; init; } = 32;
    public int MaxBlocks { get; init; } = 10_000;
    public int MaxDiagnostics { get; init; } = 100;
    public int MaxTemplateExpansionDepth { get; init; } = 16;
    public int MaxExpansionOperations { get; init; } = 10_000;
    public int MaxExpansionOutputCharacters { get; init; } = 1_500_000;
    public TimeSpan MaxExpansionTime { get; init; } = TimeSpan.FromMilliseconds(500);
    public int MaxExpressionTokens { get; init; } = 128;
    public int MaxTableNestingDepth { get; init; } = 8;
    public int MaxTableRows { get; init; } = 1_000;
    public int MaxTableCells { get; init; } = 5_000;
    public int MaxTableCellCharacters { get; init; } = 100_000;
    public int MaxTableAttributes { get; init; } = 16;
    public int MaxGalleryItems { get; init; } = 100;
    public int MaxMediaDimension { get; init; } = 4_096;
    public int MaxFunctionOutputCharacters { get; init; } = 100_000;
    public int MaxMermaidInputCharacters { get; init; } = MediaWikiMermaidCompatibility.DefaultMaxInputCharacters;
    public int MaxMermaidNestingDepth { get; init; } = MediaWikiMermaidCompatibility.DefaultMaxNestingDepth;
    public int MaxInfoboxFields { get; init; } = 512;
    public int MaxInfoboxCharacters { get; init; } = 200_000;
    public int MaxTemplateStylesSourceCharacters { get; init; } = 100_000;
    public int MaxTemplateStylesReferences { get; init; } = 32;
    public int MaxTemplateStylesRules { get; init; } = 256;
    public int MaxTemplateStylesSelectors { get; init; } = 512;
    public int MaxTemplateStylesDeclarations { get; init; } = 2_048;
    public int MaxTemplateStylesSelectorComplexity { get; init; } = 16;
    public int MaxTemplateStylesOutputCharacters { get; init; } = 200_000;
    public int MaxTemplateStylesDependencyDepth { get; init; } = 1;
    public IMediaWikiScribuntoExecutor ScribuntoExecutor { get; init; } = new RejectingMediaWikiScribuntoExecutor();
    /// <summary>Optional typed ContinuumInfobox schema support. No PHP or provider is invoked.</summary>
    public MediaWikiInfoboxRegistry? InfoboxRegistry { get; init; } = MediaWikiInfoboxRegistry.Default;
    public bool EnableLegacyInfoboxAdapter { get; init; } = true;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public enum MediaWikiDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record MediaWikiCompatibilityDiagnostic(
    string Code,
    string Message,
    int Offset,
    int Length,
    int Line,
    int Column,
    MediaWikiDiagnosticSeverity Severity = MediaWikiDiagnosticSeverity.Warning);

public sealed record MediaWikiCategory(string SourceTitle, int Offset);

public sealed record MediaWikiReference(string? Name, string Body, int Index, int Offset);

public sealed record MediaWikiTableRenderModel(
    string? CssClass,
    string? Id,
    string? Caption,
    IReadOnlyList<MediaWikiTableRowRenderModel> Rows,
    int Offset);

public sealed record MediaWikiTableRowRenderModel(
    string? CssClass,
    IReadOnlyList<MediaWikiTableCellRenderModel> Cells,
    int Offset);

public sealed record MediaWikiTableCellRenderModel(
    bool IsHeader,
    int RowSpan,
    int ColumnSpan,
    string? CssClass,
    string? Id,
    string? Scope,
    int Offset);

public sealed record MediaWikiWikitextRenderResult(
    string HtmlResult,
    IReadOnlyList<MediaWikiCompatibilityDiagnostic> Diagnostics,
    IReadOnlyList<MediaWikiCategory> Categories,
    IReadOnlyList<MediaWikiReference> References,
    IReadOnlyList<string> UnsupportedConstructs,
    IReadOnlyList<MediaWikiTemplateDependency> TemplateDependencies,
    IReadOnlyList<MediaWikiTableRenderModel> Tables,
    bool DependsOnCurrentTime = false,
    IReadOnlyList<MediaWikiScribuntoDependency>? ScribuntoDependencies = null,
    IReadOnlyList<MediaWikiStylesheet>? Stylesheets = null);

/// <summary>
/// Bounded, allow-list MediaWiki renderer. Template expansion returns Wikitext
/// and is still followed by this renderer's safe structural and encoding path.
/// </summary>
public sealed class MediaWikiWikitextRenderer(
    ITwMediaWikiImportRepository importRepository,
    TwConfiguration wikiConfiguration,
    MediaWikiWikitextRenderOptions? options = null)
{
    private readonly MediaWikiWikitextRenderOptions _options = options ?? new();

    /// <summary>
    /// The immutable-by-convention render policy shared by MVC re-resolution
    /// of typed assets. Callers must not mutate it after renderer construction.
    /// </summary>
    public MediaWikiWikitextRenderOptions Options => _options;

    public async Task<MediaWikiWikitextRenderResult> RenderAsync(
        string source,
        TwMediaWikiRenderSource provenance,
        CancellationToken cancellationToken = default,
        MediaWikiExpansionContext? expansionContext = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (provenance.Format != TwPageSourceFormat.ImportedMediaWiki)
            throw new ArgumentException("The MediaWiki renderer requires imported MediaWiki provenance.", nameof(provenance));

        expansionContext ??= CreateExpansionContext(provenance);
        var expansion = await new MediaWikiTemplateExpander(importRepository, _options)
            .ExpandAsync(source, provenance, string.Equals(provenance.SourceNamespaceId, "10", StringComparison.Ordinal), cancellationToken, expansionContext);
        var parser = new Parser(expansion.Text, _options, provenance, cancellationToken);
        var document = parser.Parse();
        var context = new RenderContext(_options, [.. expansion.Diagnostics, .. document.Diagnostics]);

        if (provenance.IsRedirect && !string.IsNullOrWhiteSpace(provenance.RedirectTargetNavigation))
        {
            var title = provenance.RedirectTargetSourceTitle ?? provenance.RedirectTargetNavigation;
            context.Append("<p class=\"cw-mediawiki-redirect\">Redirect to ");
            await AppendPageLinkAsync(context, provenance, title, provenance.RedirectTargetNavigation, title, 0, cancellationToken);
            context.Append("</p>");
        }
        else
        {
            foreach (var block in document.Blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RenderBlockAsync(context, document, provenance, block, cancellationToken);
            }
        }

        if (document.Categories.Count > 0)
        {
            context.Append("<div class=\"cw-mediawiki-categories\"><span>Categories:</span> ");
            foreach (var category in document.Categories)
            {
                await AppendPageLinkAsync(context, provenance, category.SourceTitle, null, category.SourceTitle, category.Offset, cancellationToken, categoryPrefix: "Category:");
                context.Append(" ");
            }
            context.Append("</div>");
        }

        if (context.References.Count > 0 && !context.ReferenceListRendered)
        {
            AddDiagnostic(context.Diagnostics, _options, "ReferenceListMissing", "References were recognized and appended because <references /> was not present.", 0, 0, MediaWikiDiagnosticSeverity.Information);
            await RenderReferencesAsync(context, cancellationToken);
        }

        for (var index = 0; index < context.Diagnostics.Count; index++)
        {
            var diagnostic = context.Diagnostics[index];
            var boundedOffset = Math.Min(Math.Max(0, diagnostic.Offset), expansion.Text.Length);
            var line = 1 + expansion.Text.AsSpan(0, boundedOffset).Count('\n');
            var lastNewline = boundedOffset == 0 ? -1 : expansion.Text.LastIndexOf('\n', boundedOffset - 1);
            context.Diagnostics[index] = diagnostic with { Line = line, Column = boundedOffset - lastNewline };
        }

        return new MediaWikiWikitextRenderResult(
            context.Html.ToString(),
            context.Diagnostics,
            document.Categories,
            context.References.Select(reference => new MediaWikiReference(reference.Name, reference.Body, reference.Index, reference.Offset)).ToList(),
            [.. expansion.UnsupportedConstructs, .. document.UnsupportedConstructs.Distinct(StringComparer.Ordinal)],
            expansion.Dependencies,
            document.Tables,
            expansion.DependsOnCurrentTime,
            expansion.ScribuntoDependencies,
            context.Stylesheets);
    }

    public MediaWikiExpansionContext CreateExpansionContext(
        TwMediaWikiRenderSource provenance,
        CultureInfo? culture = null,
        TimeZoneInfo? timeZone = null)
        => MediaWikiExpansionContext.From(provenance, wikiConfiguration, _options.TimeProvider, culture, timeZone);

    public string BuildCacheContextKey(MediaWikiExpansionContext context)
        => string.Join("|", context.CurrentPageTitle, context.CurrentNamespaceId, context.SourceRevisionId,
            context.Culture.Name, context.TimeZone.Id, context.SiteName, context.ContentLanguage,
            context.NowUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    private async Task RenderBlockAsync(RenderContext context, Document document, TwMediaWikiRenderSource provenance, Block block, CancellationToken cancellationToken)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                context.Append("<p>");
                await RenderInlineAsync(context, document, provenance, paragraph.Inlines, cancellationToken);
                context.Append("</p>");
                break;
            case HeadingBlock heading:
                context.Append($"<h{heading.Level}>");
                await RenderInlineAsync(context, document, provenance, heading.Inlines, cancellationToken);
                context.Append($"</h{heading.Level}>");
                break;
            case HorizontalRuleBlock:
                context.Append("<hr />");
                break;
            case ListBlock list:
                await RenderListAsync(context, document, provenance, list, cancellationToken);
                break;
            case RedirectBlock redirect:
                context.Append("<p class=\"cw-mediawiki-redirect\">Redirect to ");
                await AppendPageLinkAsync(context, provenance, redirect.Target, redirect.Target, redirect.Target, redirect.Offset, cancellationToken);
                context.Append("</p>");
                break;
            case TableBlock table:
                await RenderTableAsync(context, document, provenance, table, cancellationToken);
                break;
            case GalleryBlock gallery:
                await RenderGalleryAsync(context, document, provenance, gallery, cancellationToken);
                break;
            case InfoboxBlock infobox:
                await RenderInfoboxAsync(context, document, provenance, infobox.Node, cancellationToken);
                break;
        }
    }

    private async Task RenderTableAsync(RenderContext context, Document document, TwMediaWikiRenderSource provenance, TableBlock table, CancellationToken cancellationToken)
    {
        var classes = new List<string> { "cw-mediawiki-table" };
        classes.AddRange(table.Attributes.Classes);
        if (table.Attributes.Classes.Contains("infobox", StringComparer.OrdinalIgnoreCase)) classes.Add("cw-mediawiki-infobox");
        context.Append("<div class=\"cw-mediawiki-table-wrap\"><table class=\"" + WebUtility.HtmlEncode(string.Join(' ', classes.Distinct(StringComparer.Ordinal))) + "\"");
        AppendTableAttribute(context, "id", table.Attributes.Id);
        AppendTableAttribute(context, "role", table.Attributes.Role);
        AppendTableAttribute(context, "aria-label", table.Attributes.AriaLabel);
        context.Append(">");
        if (table.Caption is not null)
        {
            context.Append("<caption class=\"cw-mediawiki-table-caption\">");
            await RenderInlineAsync(context, document, provenance, table.Caption.Inlines, cancellationToken);
            context.Append("</caption>");
        }

        foreach (var row in table.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Append("<tr");
            AppendTableAttribute(context, "id", row.Attributes.Id);
            AppendTableAttribute(context, "class", row.Attributes.Classes.Count == 0 ? null : string.Join(' ', row.Attributes.Classes));
            context.Append(">");
            foreach (var cell in row.Cells)
            {
                var tag = cell.IsHeader ? "th" : "td";
                context.Append('<' + tag);
                AppendTableAttribute(context, "id", cell.Attributes.Id);
                AppendTableAttribute(context, "class", cell.Attributes.Classes.Count == 0 ? null : string.Join(' ', cell.Attributes.Classes));
                AppendTableAttribute(context, "scope", cell.Attributes.Scope);
                if (cell.Attributes.ColumnSpan > 1) AppendTableAttribute(context, "colspan", cell.Attributes.ColumnSpan.ToString());
                if (cell.Attributes.RowSpan > 1) AppendTableAttribute(context, "rowspan", cell.Attributes.RowSpan.ToString());
                context.Append(">");
                foreach (var content in cell.Contents)
                {
                    switch (content)
                    {
                        case InlineCellContent inline:
                            await RenderInlineAsync(context, document, provenance, inline.Inlines, cancellationToken);
                            break;
                        case NestedTableCellContent nested:
                            await RenderTableAsync(context, document, provenance, nested.Table, cancellationToken);
                            break;
                    }
                }
                context.Append("</" + tag + ">");
            }
            context.Append("</tr>");
        }
        context.Append("</table></div>");
    }

    private async Task RenderGalleryAsync(RenderContext context, Document document, TwMediaWikiRenderSource provenance, GalleryBlock gallery, CancellationToken cancellationToken)
    {
        context.Append("<div class=\"cw-mediawiki-gallery cw-mediawiki-gallery-mode-" + gallery.Options.Mode + "\"");
        if (gallery.Options.PerRow.HasValue) AppendTableAttribute(context, "data-cw-perrow", gallery.Options.PerRow.Value.ToString());
        context.Append(">");
        foreach (var item in gallery.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Append("<figure class=\"cw-mediawiki-gallery-item\">");
            var media = new MediaLinkNode(item.FileName, item.CaptionText, item.Offset, item.Length,
                new MediaDisplayOptions(MediaDisplayStyle.Default, MediaAlignment.None, gallery.Options.Width, gallery.Options.Height, null, null, item.CaptionText, item.CaptionInlines));
            await AppendMediaLinkAsync(context, document, provenance, media, cancellationToken, bareImage: true);
            if (item.CaptionInlines.Count > 0)
            {
                context.Append("<figcaption class=\"cw-mediawiki-caption\">");
                await RenderInlineAsync(context, document, provenance, item.CaptionInlines, cancellationToken);
                context.Append("</figcaption>");
            }
            context.Append("</figure>");
        }
        context.Append("</div>");
    }

    private static void AppendTableAttribute(RenderContext context, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        context.Append(" " + name + "=\"" + WebUtility.HtmlEncode(value) + "\"");
    }

    private async Task RenderListAsync(RenderContext context, Document document, TwMediaWikiRenderSource provenance, ListBlock list, CancellationToken cancellationToken)
    {
        var roots = new List<TreeListItem>();
        var stack = new List<TreeListItem>();
        foreach (var item in list.Items)
        {
            var depth = Math.Min(item.Depth, _options.MaxParseDepth);
            if (depth > stack.Count) depth = stack.Count;
            while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
            var treeItem = new TreeListItem(item);
            if (stack.Count == 0) roots.Add(treeItem);
            else stack[^1].Children.Add(treeItem);
            stack.Add(treeItem);
        }

        await RenderListLevelAsync(context, document, provenance, roots, cancellationToken);
    }

    private async Task RenderListLevelAsync(RenderContext context, Document document, TwMediaWikiRenderSource provenance, IReadOnlyList<TreeListItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        var tag = items[0].Item.Ordered ? "ol" : "ul";
        context.Append("<" + tag + ">");
        foreach (var item in items)
        {
            context.Append("<li>");
            await RenderInlineAsync(context, document, provenance, item.Item.Inlines, cancellationToken);
            await RenderListLevelAsync(context, document, provenance, item.Children, cancellationToken);
            context.Append("</li>");
        }
        context.Append("</" + tag + ">");
    }

    private async Task RenderInlineAsync(RenderContext context, Document document, TwMediaWikiRenderSource provenance, IReadOnlyList<InlineNode> nodes, CancellationToken cancellationToken)
    {
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case TextNode text:
                    context.Append(WebUtility.HtmlEncode(text.Value).Replace("\n", "<br />\n", StringComparison.Ordinal));
                    break;
                case BoldNode bold:
                    context.Append("<strong>");
                    await RenderInlineAsync(context, document, provenance, bold.Children, cancellationToken);
                    context.Append("</strong>");
                    break;
                case ItalicNode italic:
                    context.Append("<em>");
                    await RenderInlineAsync(context, document, provenance, italic.Children, cancellationToken);
                    context.Append("</em>");
                    break;
                case InternalLinkNode link:
                    await AppendInternalLinkAsync(context, provenance, link, cancellationToken);
                    break;
                case MediaLinkNode media:
                    await AppendMediaLinkAsync(context, document, provenance, media, cancellationToken);
                    break;
                case CategoryNode:
                    break;
                case ExternalLinkNode external:
                    AppendExternalLink(context, external);
                    break;
                case ReferenceNode reference:
                    await AppendReferenceAsync(context, document, provenance, reference, cancellationToken);
                    break;
                case ReferencesNode:
                    await RenderReferencesAsync(context, cancellationToken);
                    break;
                case InfoboxNode infobox:
                    await RenderInfoboxAsync(context, document, provenance, infobox, cancellationToken);
                    break;
                case TemplateStylesNode templateStyles:
                    await RenderTemplateStylesAsync(context, provenance, templateStyles, cancellationToken);
                    break;
                case StaticExtensionNode extension:
                    AddDiagnostic(context.Diagnostics, _options, extension.DiagnosticCode, extension.DiagnosticMessage, extension.Offset, extension.Length, extension.Severity);
                    context.AppendExtension(extension.Html, extension.Offset, extension.Length);
                    break;
                case UnsupportedNode unsupported:
                    AddDiagnostic(context.Diagnostics, _options, unsupported.Code, unsupported.Message, unsupported.Offset, unsupported.Length, unsupported.Severity);
                    context.Append($"<span class=\"cw-mediawiki-unsupported\" data-cw-kind=\"{WebUtility.HtmlEncode(unsupported.Kind)}\">Unsupported MediaWiki construct</span>");
                    break;
            }
        }
    }

    private async Task RenderTemplateStylesAsync(
        RenderContext context,
        TwMediaWikiRenderSource provenance,
        TemplateStylesNode templateStyles,
        CancellationToken cancellationToken)
    {
        if (templateStyles.ParseError is not null)
        {
            AddDiagnostic(context.Diagnostics, _options, "TemplateStylesInvalidSource", templateStyles.ParseError, templateStyles.Offset, templateStyles.Length, MediaWikiDiagnosticSeverity.Error);
            return;
        }

        var resolution = await MediaWikiTemplateStylesCompatibility.ResolveAsync(
            importRepository,
            provenance,
            templateStyles.SourceReference,
            _options,
            cancellationToken);
        if (resolution.DiagnosticCode is not null)
            AddDiagnostic(context.Diagnostics, _options, resolution.DiagnosticCode, resolution.Message, templateStyles.Offset, templateStyles.Length, resolution.Severity);
        if (resolution.Stylesheet is null) return;

        if (!context.StylesheetRegistry.TryRegister(resolution.Stylesheet, out var registrationDiagnostic))
        {
            AddDiagnostic(context.Diagnostics, _options, registrationDiagnostic!, "TemplateStyles references exceeded the configured per-render stylesheet budget.", templateStyles.Offset, templateStyles.Length, MediaWikiDiagnosticSeverity.Error);
        }
    }

    private async Task RenderInfoboxAsync(
        RenderContext context,
        Document document,
        TwMediaWikiRenderSource provenance,
        InfoboxNode infobox,
        CancellationToken cancellationToken)
    {
        var model = infobox.Model;
        var type = MediaWikiInfoboxRegistry.NormalizeToken(model.Type);
        var title = string.IsNullOrWhiteSpace(model.Title) ? provenance.SourceTitle ?? model.Label : model.Title;
        context.Append("<aside class=\"cu-infobox cu-infobox--" + WebUtility.HtmlEncode(type) + "\" role=\"complementary\" aria-label=\"" + WebUtility.HtmlEncode(model.Label) + "\">");
        if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(model.Subtitle))
        {
            context.Append("<header class=\"cu-infobox__header\">");
            if (!string.IsNullOrWhiteSpace(title))
            {
                context.Append("<h2 class=\"cu-infobox__title\"><span class=\"cu-infobox__title-main\">");
                await RenderInfoboxInlineAsync(context, document, provenance, title!, cancellationToken);
                context.Append("</span>");
                if (!string.IsNullOrWhiteSpace(model.Subtitle))
                {
                    context.Append(" <span class=\"cu-infobox__subtitle\">");
                    await RenderInfoboxInlineAsync(context, document, provenance, model.Subtitle!, cancellationToken);
                    context.Append("</span>");
                }
                context.Append("</h2>");
            }
            else
            {
                context.Append("<h2 class=\"cu-infobox__title\">");
                await RenderInfoboxInlineAsync(context, document, provenance, model.Subtitle!, cancellationToken);
                context.Append("</h2>");
            }
            context.Append("</header>");
        }
        if (!string.IsNullOrWhiteSpace(model.Image))
        {
            context.Append("<figure class=\"cu-infobox__figure\">");
            var image = model.Image!.StartsWith("File:", StringComparison.OrdinalIgnoreCase) || model.Image.StartsWith("Image:", StringComparison.OrdinalIgnoreCase)
                ? "[[" + model.Image + "]]" : model.Image;
            await RenderInfoboxInlineAsync(context, document, provenance, image, cancellationToken);
            if (!string.IsNullOrWhiteSpace(model.Caption))
            {
                context.Append("<figcaption class=\"cu-infobox__caption\">");
                await RenderInfoboxInlineAsync(context, document, provenance, model.Caption!, cancellationToken);
                context.Append("</figcaption>");
            }
            context.Append("</figure>");
        }

        var groupedFields = model.Fields.GroupBy(field => field.Group ?? "additional", StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var groupOrder = (model.Groups ?? []).Select(group => group.Name)
            .Concat(groupedFields.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var groupName in groupOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!groupedFields.TryGetValue(groupName, out var fields) || fields.Count == 0) continue;
            var groupDefinition = (model.Groups ?? []).FirstOrDefault(group => string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase));
            context.Append("<section class=\"cu-infobox__section");
            if (fields.Any(field => field.Horizontal) || groupDefinition?.Horizontal == true) context.Append(" cu-infobox__section--horizontal");
            context.Append("\">");
            context.Append("<h3 class=\"cu-infobox__section-title\">");
            context.Append(WebUtility.HtmlEncode(groupDefinition?.Label ?? fields.FirstOrDefault()?.GroupLabel ?? HumanizeInfoboxGroup(groupName)));
            context.Append("</h3>");
            if (fields.Any(field => field.Horizontal) || groupDefinition?.Horizontal == true)
            {
                context.Append("<div class=\"cu-infobox__horizontal-group\">");
                foreach (var field in fields)
                {
                    context.Append("<span class=\"cu-infobox__horizontal-label\">");
                    context.Append(WebUtility.HtmlEncode(field.Label));
                    context.Append("</span><span class=\"cu-infobox__horizontal-value\">");
                    await RenderInfoboxInlineAsync(context, document, provenance, field.Value, cancellationToken);
                    context.Append("</span>");
                }
                context.Append("</div>");
            }
            else
            {
                context.Append("<dl class=\"cu-infobox__rows\">");
                foreach (var field in fields)
                {
                    context.Append("<div class=\"cu-infobox__row\"><dt class=\"cu-infobox__label\">");
                    context.Append(WebUtility.HtmlEncode(field.Label));
                    context.Append("</dt><dd class=\"cu-infobox__value\">");
                    await RenderInfoboxInlineAsync(context, document, provenance, field.Value, cancellationToken);
                    context.Append("</dd></div>");
                }
                context.Append("</dl>");
            }
            context.Append("</section>");
        }
        context.Append("</aside>");
    }

    private async Task RenderInfoboxInlineAsync(RenderContext context, Document document, TwMediaWikiRenderSource provenance, string value, CancellationToken cancellationToken)
    {
        var fragment = new Parser(value, _options, provenance, cancellationToken).ParseInlineFragment();
        foreach (var diagnostic in fragment.Diagnostics) context.Diagnostics.Add(diagnostic);
        await RenderInlineAsync(context, document, provenance, fragment.Nodes, cancellationToken);
    }

    private static string HumanizeInfoboxGroup(string value)
        => value.Equals("additional", StringComparison.OrdinalIgnoreCase) ? "Additional" : string.Join(' ', value.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    private async Task AppendInternalLinkAsync(RenderContext context, TwMediaWikiRenderSource provenance, InternalLinkNode link, CancellationToken cancellationToken)
    {
        var targetTitle = link.Target.Split('#', 2)[0].Trim();
        var target = await importRepository.ResolvePageAsync(provenance.ImportBatchId ?? 0, targetTitle, cancellationToken);
        if (target is null)
        {
            AddDiagnostic(context.Diagnostics, _options, "UnresolvedInternalLink", $"Imported page link '{targetTitle}' did not resolve.", link.Offset, link.Length);
            context.Append(WebUtility.HtmlEncode(link.Label ?? DisplayTitle(link.Target)));
            return;
        }

        await AppendPageLinkAsync(context, provenance, target.SourceTitle, target.TargetNavigation, link.Label ?? DisplayTitle(link.Target), link.Offset, cancellationToken);
    }

    private async Task AppendPageLinkAsync(RenderContext context, TwMediaWikiRenderSource provenance, string sourceTitle, string? mappedNavigation, string label, int offset, CancellationToken cancellationToken, string? categoryPrefix = null)
    {
        var navigation = mappedNavigation;
        if (string.IsNullOrWhiteSpace(navigation))
        {
            var target = await importRepository.ResolvePageAsync(provenance.ImportBatchId ?? 0, sourceTitle, cancellationToken);
            navigation = target?.TargetNavigation;
        }

        if (string.IsNullOrWhiteSpace(navigation))
        {
            AddDiagnostic(context.Diagnostics, _options, "UnresolvedInternalLink", $"Imported page link '{sourceTitle}' did not resolve.", offset, sourceTitle.Length);
            context.Append(WebUtility.HtmlEncode(label));
            return;
        }

        var href = BuildPageHref(navigation);
        context.Append("<a href=\"" + WebUtility.HtmlEncode(href) + "\">");
        context.Append(WebUtility.HtmlEncode(categoryPrefix is null ? label : label));
        context.Append("</a>");
    }

    private async Task AppendMediaLinkAsync(RenderContext context, Document document, TwMediaWikiRenderSource provenance, MediaLinkNode media, CancellationToken cancellationToken, bool bareImage = false)
    {
        var target = await importRepository.ResolveMediaAsync(provenance.ImportBatchId ?? 0, provenance.SourcePageId ?? string.Empty, provenance.SourceRevisionId ?? string.Empty, media.FileName, cancellationToken);
        if (target is null || !target.Resolved)
        {
            AddDiagnostic(context.Diagnostics, _options, "UnresolvedMedia", $"Imported media '{media.FileName}' did not resolve.", media.Offset, media.Length);
            context.Append("<span class=\"cw-mediawiki-unresolved-media\">[File: " + WebUtility.HtmlEncode(media.FileName) + "]</span>");
            return;
        }

        var label = string.IsNullOrWhiteSpace(media.Options.CaptionText) ? media.FileName : media.Options.CaptionText;
        var page = Uri.EscapeDataString(target.PageNavigation);
        var file = Uri.EscapeDataString(target.FileNavigation);
        // The existing Binary route preserves the imported bytes and applies
        // its normal page-read permission check. Png/Image performs legacy
        // compression/conversion assumptions that are not valid for staged
        // MediaWiki bytes, so this foundation links images directly.
        const string route = "Binary";
        var href = JoinBasePath($"/File/{route}/{page}/{file}{(target.FileRevision.HasValue ? "/" + target.FileRevision.Value : string.Empty)}");
        if (!target.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            context.Append("<a href=\"" + WebUtility.HtmlEncode(href) + "\">" + WebUtility.HtmlEncode(label) + "</a>");
            return;
        }

        var classes = new List<string> { "cw-mediawiki-media" };
        if (media.Options.Style != MediaDisplayStyle.Default) classes.Add("cw-mediawiki-media-" + media.Options.Style.ToString().ToLowerInvariant());
        if (media.Options.Alignment != MediaAlignment.None) classes.Add("cw-mediawiki-media-align-" + media.Options.Alignment.ToString().ToLowerInvariant());
        var image = new StringBuilder("<img class=\"");
        image.Append(WebUtility.HtmlEncode(string.Join(' ', classes))).Append("\" src=\"").Append(WebUtility.HtmlEncode(href)).Append("\" alt=\"");
        image.Append(WebUtility.HtmlEncode(media.Options.Alt ?? label)).Append('"');
        if (media.Options.Width.HasValue) image.Append(" width=\"").Append(media.Options.Width.Value).Append('"');
        if (media.Options.Height.HasValue) image.Append(" height=\"").Append(media.Options.Height.Value).Append('"');
        image.Append(" />");

        if (bareImage)
        {
            context.Append(image.ToString());
            return;
        }

        var useFigure = media.Options.Style != MediaDisplayStyle.Default || media.Options.CaptionInlines.Count > 0;
        if (useFigure)
        {
            context.Append("<figure class=\"cw-mediawiki-media-figure\">");
        }

        string? linkedHref = null;
        if (media.Options.Link is not null && !media.Options.Link.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (media.Options.Link.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
                || media.Options.Link.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
                || media.Options.Link.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || media.Options.Link.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || media.Options.Link.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                || media.Options.Link.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
            {
                AddDiagnostic(context.Diagnostics, _options, "UnsafeMediaLink", "External media link options are not enabled; the image was left unlinked.", media.Offset, media.Length);
            }
            else
            {
                var linkedPage = await importRepository.ResolvePageAsync(provenance.ImportBatchId ?? 0, media.Options.Link, cancellationToken);
                if (linkedPage is null)
                    AddDiagnostic(context.Diagnostics, _options, "UnresolvedMediaLink", $"Media link '{media.Options.Link}' did not resolve.", media.Offset, media.Length);
                else
                    linkedHref = BuildPageHref(linkedPage.TargetNavigation);
            }
        }

        if (linkedHref is not null) context.Append("<a href=\"" + WebUtility.HtmlEncode(linkedHref) + "\">");
        context.Append(image.ToString());
        if (linkedHref is not null) context.Append("</a>");

        if (useFigure)
        {
            if (media.Options.CaptionInlines.Count > 0)
            {
                context.Append("<figcaption class=\"cw-mediawiki-caption\">");
                await RenderInlineAsync(context, document, provenance, media.Options.CaptionInlines, cancellationToken);
                context.Append("</figcaption>");
            }
            context.Append("</figure>");
        }
    }

    private void AppendExternalLink(RenderContext context, ExternalLinkNode link)
    {
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)
            || uri is null
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0
            || link.Url.Any(char.IsControl))
        {
            AddDiagnostic(context.Diagnostics, _options, "UnsafeExternalUrl", "The external URL was rejected; only http and https links are allowed.", link.Offset, link.Length);
            context.Append(WebUtility.HtmlEncode(link.Label ?? link.Url));
            return;
        }

        var label = link.Label ?? link.Url;
        context.Append("<a href=\"" + WebUtility.HtmlEncode(uri.AbsoluteUri) + "\" rel=\"nofollow noopener noreferrer\">" + WebUtility.HtmlEncode(label) + "</a>");
    }

    private async Task AppendReferenceAsync(RenderContext context, Document document, TwMediaWikiRenderSource provenance, ReferenceNode node, CancellationToken cancellationToken)
    {
        var existing = context.References.FirstOrDefault(reference => node.Name is not null && string.Equals(reference.Name, node.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var body = await RenderInlineToTextAsync(document, provenance, node.Body, cancellationToken);
            existing = new ReferenceState(node.Name, body, context.References.Count + 1, node.Offset);
            context.References.Add(existing);
        }
        context.Append("<sup id=\"cite_ref_" + existing.Index + "\"><a href=\"#cite_note_" + existing.Index + "\">[" + existing.Index + "]</a></sup>");
    }

    private async Task<string> RenderInlineToTextAsync(Document document, TwMediaWikiRenderSource provenance, IReadOnlyList<InlineNode> nodes, CancellationToken cancellationToken)
    {
        var nested = new RenderContext(_options, document.Diagnostics);
        await RenderInlineAsync(nested, document, provenance, nodes, cancellationToken);
        return nested.Html.ToString();
    }

    private async Task RenderReferencesAsync(RenderContext context, CancellationToken cancellationToken)
    {
        if (context.ReferenceListRendered) return;
        context.ReferenceListRendered = true;
        context.Append("<ol class=\"cw-mediawiki-references\">");
        foreach (var reference in context.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Append("<li id=\"cite_note_" + reference.Index + "\">" + reference.Body + "</li>");
        }
        context.Append("</ol>");
    }

    private string BuildPageHref(string navigation) => JoinBasePath("/" + Uri.EscapeDataString(navigation));
    private string JoinBasePath(string path)
    {
        var basePath = wikiConfiguration.BasePath?.TrimEnd('/') ?? string.Empty;
        return basePath + path;
    }

    private static string DisplayTitle(string value)
    {
        var title = value.Split('#', 2)[0].Trim().Replace('_', ' ');
        return title.Contains(':') ? title[(title.IndexOf(':') + 1)..].Trim() : title;
    }

    private static void AddDiagnostic(List<MediaWikiCompatibilityDiagnostic> diagnostics, MediaWikiWikitextRenderOptions? options, string code, string message, int offset, int length, MediaWikiDiagnosticSeverity severity = MediaWikiDiagnosticSeverity.Warning)
    {
        if (options is not null && diagnostics.Count >= options.MaxDiagnostics) return;
        var clamped = Math.Max(0, Math.Min(offset, int.MaxValue));
        diagnostics.Add(new(code, message, clamped, Math.Max(0, length), 0, 0, severity));
    }

    private sealed class RenderContext(MediaWikiWikitextRenderOptions options, List<MediaWikiCompatibilityDiagnostic> diagnostics)
    {
        public StringBuilder Html { get; } = new();
        public List<MediaWikiCompatibilityDiagnostic> Diagnostics { get; } = diagnostics;
        public List<ReferenceState> References { get; } = new();
        public MediaWikiStylesheetRegistry StylesheetRegistry { get; } = new(options.MaxTemplateStylesReferences);
        public IReadOnlyList<MediaWikiStylesheet> Stylesheets => StylesheetRegistry.Stylesheets;
        public bool ReferenceListRendered { get; set; }
        public void Append(string value)
        {
            if (Html.Length >= options.MaxOutputCharacters) return;
            var remaining = options.MaxOutputCharacters - Html.Length;
            if (value.Length <= remaining)
            {
                Html.Append(value);
                return;
            }

            Html.Append(value.AsSpan(0, Math.Max(0, remaining)));
            if (Diagnostics.All(diagnostic => diagnostic.Code != "OutputLimitExceeded"))
                Diagnostics.Add(new("OutputLimitExceeded", "MediaWiki output exceeded the configured size limit.", 0, 0, 0, 0, MediaWikiDiagnosticSeverity.Error));
        }

        public void AppendExtension(string value, int offset, int length)
        {
            if (Html.Length >= options.MaxOutputCharacters || value.Length > options.MaxOutputCharacters - Html.Length)
            {
                if (Diagnostics.All(diagnostic => diagnostic.Code != "ExtensionOutputLimitExceeded"))
                    Diagnostics.Add(new("ExtensionOutputLimitExceeded", "The extension output exceeded the shared MediaWiki output limit.", offset, length, 0, 0, MediaWikiDiagnosticSeverity.Error));
                return;
            }
            Html.Append(value);
        }
    }

    private sealed record ReferenceState(string? Name, string Body, int Index, int Offset);

    private abstract record Block(int Offset);
    private sealed record ParagraphBlock(IReadOnlyList<InlineNode> Inlines, int Offset) : Block(Offset);
    private sealed record HeadingBlock(int Level, IReadOnlyList<InlineNode> Inlines, int Offset) : Block(Offset);
    private sealed record HorizontalRuleBlock(int Offset) : Block(Offset);
    private sealed record RedirectBlock(string Target, int Offset) : Block(Offset);
    private sealed record ListBlock(IReadOnlyList<ListItem> Items, int Offset) : Block(Offset);
    private sealed record TableBlock(TableAttributeSet Attributes, TableCaption? Caption, IReadOnlyList<TableRow> Rows, int Offset) : Block(Offset);
    private sealed record GalleryBlock(GalleryOptions Options, IReadOnlyList<GalleryItem> Items, int Offset) : Block(Offset);
    private sealed record InfoboxBlock(InfoboxNode Node) : Block(Node.Offset);
    private sealed record ListItem(int Depth, bool Ordered, IReadOnlyList<InlineNode> Inlines, int Offset);
    private sealed class TreeListItem(ListItem item)
    {
        public ListItem Item { get; } = item;
        public List<TreeListItem> Children { get; } = new();
    }

    private abstract record InlineNode(int Offset, int Length);
    private sealed record TextNode(string Value, int Offset, int Length) : InlineNode(Offset, Length);
    private sealed record BoldNode(IReadOnlyList<InlineNode> Children, int Offset, int Length) : InlineNode(Offset, Length);
    private sealed record ItalicNode(IReadOnlyList<InlineNode> Children, int Offset, int Length) : InlineNode(Offset, Length);
    private sealed record InternalLinkNode(string Target, string? Label, int Offset, int Length) : InlineNode(Offset, Length);
    private sealed record MediaLinkNode(string FileName, string? Caption, int Offset, int Length, MediaDisplayOptions Options) : InlineNode(Offset, Length);
    private sealed record CategoryNode(string SourceTitle, int Offset, int Length) : InlineNode(Offset, Length);
    private sealed record ExternalLinkNode(string Url, string? Label, int Offset, int Length) : InlineNode(Offset, Length);
    private sealed record ReferenceNode(string? Name, IReadOnlyList<InlineNode> Body, int Offset, int Length) : InlineNode(Offset, Length);
    private sealed record ReferencesNode(int Offset, int Length) : InlineNode(Offset, Length);
    private sealed record InfoboxNode(MediaWikiInfoboxRenderModel Model, int Offset, int Length) : InlineNode(Offset, Length);
    private sealed record TemplateStylesNode(string? SourceReference, string? ParseError, int Offset, int Length) : InlineNode(Offset, Length);
    private sealed record StaticExtensionNode(string Html, string DiagnosticCode, string DiagnosticMessage, int Offset, int Length, MediaWikiDiagnosticSeverity Severity = MediaWikiDiagnosticSeverity.Warning) : InlineNode(Offset, Length);
    private sealed record UnsupportedNode(string Kind, string Code, string Message, int Offset, int Length, MediaWikiDiagnosticSeverity Severity = MediaWikiDiagnosticSeverity.Warning) : InlineNode(Offset, Length);

    private enum MediaDisplayStyle { Default, Thumb, Frame, Frameless, Border }
    private enum MediaAlignment { None, Left, Right, Center }
    private sealed record MediaDisplayOptions(
        MediaDisplayStyle Style,
        MediaAlignment Alignment,
        int? Width,
        int? Height,
        string? Alt,
        string? Link,
        string? CaptionText,
        IReadOnlyList<InlineNode> CaptionInlines);

    private sealed record TableAttributeSet(
        IReadOnlyList<string> Classes,
        string? Id,
        string? Role,
        string? Scope,
        string? AriaLabel,
        int? RowSpan = null,
        int? ColumnSpan = null);
    private sealed record TableCaption(IReadOnlyList<InlineNode> Inlines, string RawText, int Offset);
    private sealed record TableRow(TableAttributeSet Attributes, IReadOnlyList<TableCell> Cells, int Offset);
    private sealed record TableCell(bool IsHeader, TableAttributeSet Attributes, IReadOnlyList<CellContent> Contents, int Offset, int Length);
    private abstract record CellContent;
    private sealed record InlineCellContent(IReadOnlyList<InlineNode> Inlines) : CellContent;
    private sealed record NestedTableCellContent(TableBlock Table) : CellContent;
    private sealed record GalleryOptions(string Mode, int? Width, int? Height, int? PerRow);
    private sealed record GalleryItem(string FileName, string CaptionText, IReadOnlyList<InlineNode> CaptionInlines, int Offset, int Length);

    private sealed class Document
    {
        public List<Block> Blocks { get; } = new();
        public List<MediaWikiCompatibilityDiagnostic> Diagnostics { get; } = new();
        public List<MediaWikiCategory> Categories { get; } = new();
        public List<string> UnsupportedConstructs { get; } = new();
        public List<MediaWikiTableRenderModel> Tables { get; } = new();
    }

    private sealed class Parser(string source, MediaWikiWikitextRenderOptions options, TwMediaWikiRenderSource provenance, CancellationToken cancellationToken)
    {
        private readonly string _source = source.Length > options.MaxInputCharacters ? source[..options.MaxInputCharacters] : source;
        private readonly Document _document = new();
        private int _blockCount;
        private int _tableCellCount;

        public Document Parse()
        {
            if (source.Length > options.MaxInputCharacters)
                AddDiagnostic(_document.Diagnostics, options, "InputLimitExceeded", "MediaWiki source exceeded the configured input limit and was truncated for rendering.", options.MaxInputCharacters, 0, MediaWikiDiagnosticSeverity.Error);

            var paragraph = new StringBuilder();
            var paragraphOffset = 0;
            var listItems = new List<ListItem>();
            bool? listOrdered = null;
            var lines = _source.Split('\n');
            var lineOffsets = new int[lines.Length];
            var runningOffset = 0;
            for (var index = 0; index < lines.Length; index++)
            {
                lineOffsets[index] = runningOffset;
                runningOffset += lines[index].Length + 1;
            }
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var raw = lines[lineIndex];
                var line = raw.TrimEnd('\r');
                var trimmed = line.Trim();
                var lineOffset = lineOffsets[lineIndex];

                if (TryParseRedirect(trimmed, lineOffset, out var redirect))
                {
                    FlushParagraph(paragraph, paragraphOffset);
                    FlushList(listItems, lineOffset);
                    listOrdered = null;
                    _document.Blocks.Add(redirect);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    FlushParagraph(paragraph, paragraphOffset);
                    FlushList(listItems, lineOffset);
                    listOrdered = null;
                    continue;
                }

                if (trimmed.StartsWith("{|", StringComparison.Ordinal) && TryParseTable(lines, lineOffsets, ref lineIndex, 0, out var table))
                {
                    FlushParagraph(paragraph, paragraphOffset);
                    FlushList(listItems, lineOffset);
                    listOrdered = null;
                    AddBlock(table);
                    continue;
                }

                if (trimmed.StartsWith("<gallery", StringComparison.OrdinalIgnoreCase) && TryParseGallery(lines, lineOffsets, ref lineIndex, out var gallery))
                {
                    FlushParagraph(paragraph, paragraphOffset);
                    FlushList(listItems, lineOffset);
                    listOrdered = null;
                    AddBlock(gallery);
                    continue;
                }

                if (MediaWikiInfoboxCompatibility.TryDecode(trimmed, out var infobox) && trimmed.StartsWith(MediaWikiInfoboxCompatibility.TokenPrefix, StringComparison.Ordinal))
                {
                    FlushParagraph(paragraph, paragraphOffset);
                    FlushList(listItems, lineOffset);
                    listOrdered = null;
                    AddBlock(new InfoboxBlock(new InfoboxNode(infobox, lineOffset + line.IndexOf(trimmed, StringComparison.Ordinal), trimmed.Length)));
                    continue;
                }

                if (TryParseHeading(line, lineOffset, out var heading))
                {
                    FlushParagraph(paragraph, paragraphOffset);
                    FlushList(listItems, lineOffset);
                    listOrdered = null;
                    _document.Blocks.Add(heading);
                    continue;
                }

                if (trimmed.Length >= 4 && trimmed.All(c => c == '-'))
                {
                    FlushParagraph(paragraph, paragraphOffset);
                    FlushList(listItems, lineOffset);
                    listOrdered = null;
                    _document.Blocks.Add(new HorizontalRuleBlock(lineOffset));
                    continue;
                }

                if (TryParseListItem(line, lineOffset, out var item))
                {
                    FlushParagraph(paragraph, paragraphOffset);
                    if (listOrdered.HasValue && listOrdered.Value != item.Ordered) FlushList(listItems, lineOffset);
                    listOrdered ??= item.Ordered;
                    listItems.Add(item);
                    continue;
                }

                FlushList(listItems, lineOffset);
                listOrdered = null;
                if (paragraph.Length == 0) paragraphOffset = lineOffset;
                else paragraph.Append('\n');
                paragraph.Append(line);
            }

            FlushParagraph(paragraph, paragraphOffset);
            FlushList(listItems, _source.Length);
            for (var index = 0; index < _document.Diagnostics.Count; index++)
            {
                var diagnostic = _document.Diagnostics[index];
                var boundedOffset = Math.Min(Math.Max(0, diagnostic.Offset), _source.Length);
                var line = 1 + _source.AsSpan(0, boundedOffset).Count('\n');
                var lastNewline = boundedOffset == 0 ? -1 : _source.LastIndexOf('\n', boundedOffset - 1);
                var column = boundedOffset - lastNewline;
                _document.Diagnostics[index] = diagnostic with { Line = line, Column = column };
            }
            return _document;
        }

        private void FlushParagraph(StringBuilder paragraph, int offset)
        {
            if (paragraph.Length == 0) return;
            AddBlock(new ParagraphBlock(ParseInline(paragraph.ToString(), offset, 0), offset));
            paragraph.Clear();
        }

        private void FlushList(List<ListItem> items, int offset)
        {
            if (items.Count == 0) return;
            AddBlock(new ListBlock(items.ToList(), items[0].Offset));
            items.Clear();
        }

        private void AddBlock(Block block)
        {
            if (_blockCount++ >= options.MaxBlocks)
            {
                AddDiagnostic(_document.Diagnostics, options, "BlockLimitExceeded", "MediaWiki block count exceeded the configured limit.", block.Offset, 0, MediaWikiDiagnosticSeverity.Error);
                return;
            }
            _document.Blocks.Add(block);
        }

        private static readonly Regex AttributeToken = new(
            "(?<name>[A-Za-z_:][A-Za-z0-9_:.-]*)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s]+))",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SafeToken = new("^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private bool TryParseTable(string[] lines, int[] lineOffsets, ref int lineIndex, int depth, out TableBlock table)
        {
            var start = lineIndex;
            var startLine = lines[start].Trim();
            if (!startLine.StartsWith("{|", StringComparison.Ordinal))
            {
                table = null!;
                return false;
            }
            if (depth >= options.MaxTableNestingDepth)
            {
                AddDiagnostic(_document.Diagnostics, options, "TableNestingLimitExceeded", "MediaWiki table nesting exceeded the configured limit; the nested table was omitted.", lineOffsets[start], lines[start].Length, MediaWikiDiagnosticSeverity.Error);
                table = new TableBlock(new TableAttributeSet([], null, null, null, null), null, [], lineOffsets[start]);
                return true;
            }

            var attributes = ParseTableAttributes(startLine[2..].Trim(), lineOffsets[start] + 2);
            var rows = new List<TableRow>();
            TableCaption? caption = null;
            MutableTableRow? currentRow = null;
            MutableTableCell? currentCell = null;

            void CommitCell()
            {
                if (currentCell is null || currentRow is null) return;
                currentCell.FlushText(this);
                if (_tableCellCount++ >= options.MaxTableCells)
                {
                    AddDiagnostic(_document.Diagnostics, options, "TableCellLimitExceeded", "MediaWiki table cell count exceeded the configured limit; additional cells were omitted.", currentCell.Offset, currentCell.Length, MediaWikiDiagnosticSeverity.Error);
                }
                else
                {
                    currentRow.Cells.Add(new TableCell(currentCell.IsHeader, currentCell.Attributes, currentCell.Contents.ToList(), currentCell.Offset, currentCell.Length));
                }
                currentCell = null;
            }

            void CommitRow()
            {
                CommitCell();
                if (currentRow is null) return;
                if (rows.Count >= options.MaxTableRows)
                {
                    AddDiagnostic(_document.Diagnostics, options, "TableRowLimitExceeded", "MediaWiki table row count exceeded the configured limit; additional rows were omitted.", currentRow.Offset, 0, MediaWikiDiagnosticSeverity.Error);
                }
                else if (currentRow.Cells.Count > 0)
                {
                    rows.Add(new TableRow(currentRow.Attributes, currentRow.Cells.ToList(), currentRow.Offset));
                }
                currentRow = null;
            }

            for (var index = start + 1; index < lines.Length; index++)
            {
                var line = lines[index].TrimEnd('\r');
                var trimmed = line.Trim();
                if (trimmed.StartsWith("{|", StringComparison.Ordinal) && currentCell is not null)
                {
                    if (TryParseTable(lines, lineOffsets, ref index, depth + 1, out var nested))
                    {
                        currentCell.FlushText(this);
                        currentCell.Contents.Add(new NestedTableCellContent(nested));
                        continue;
                    }
                }
                if (trimmed.StartsWith("|}", StringComparison.Ordinal))
                {
                    CommitRow();
                    lineIndex = index;
                    table = new TableBlock(attributes, caption, rows, lineOffsets[start]);
                    _document.Tables.Add(ToPublicTable(table));
                    return true;
                }
                if (trimmed.StartsWith("|-", StringComparison.Ordinal))
                {
                    CommitRow();
                    currentRow = new MutableTableRow(ParseTableAttributes(trimmed[2..].Trim(), lineOffsets[index] + Math.Max(0, line.IndexOf("|-", StringComparison.Ordinal) + 2)), lineOffsets[index]);
                    continue;
                }
                if (trimmed.StartsWith("|+", StringComparison.Ordinal))
                {
                    CommitCell();
                    var captionText = trimmed[2..].Trim();
                    var captionParts = SplitTopLevel(captionText, '|');
                    if (captionParts.Count > 1 && LooksLikeAttributes(captionParts[0])) captionText = string.Join('|', captionParts.Skip(1));
                    caption = new TableCaption(ParseInline(captionText, lineOffsets[index] + Math.Max(0, line.IndexOf("|+", StringComparison.Ordinal) + 2), depth + 1), captionText, lineOffsets[index]);
                    continue;
                }
                if (trimmed.StartsWith("!", StringComparison.Ordinal) || (trimmed.StartsWith("|", StringComparison.Ordinal) && !trimmed.StartsWith("||", StringComparison.Ordinal)))
                {
                    currentRow ??= new MutableTableRow(new TableAttributeSet([], null, null, null, null), lineOffsets[index]);
                    CommitCell();
                    var marker = trimmed[0];
                    var body = trimmed[1..];
                    var segments = SplitTopLevelToken(body, marker == '!' ? "!!" : "||");
                    for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
                    {
                        var segment = segments[segmentIndex];
                        var cellParts = SplitTopLevel(segment, '|');
                        var cellText = segment.Trim();
                        var cellAttributes = new TableAttributeSet([], null, null, null, null);
                        if (cellParts.Count > 1 && LooksLikeAttributes(cellParts[0]))
                        {
                            cellAttributes = ParseTableAttributes(cellParts[0].Trim(), lineOffsets[index]);
                            cellText = string.Join('|', cellParts.Skip(1)).Trim();
                        }
                        currentCell = new MutableTableCell(marker == '!', cellAttributes, lineOffsets[index], cellText.Length);
                        AppendCellText(currentCell, cellText, lineOffsets[index]);
                        if (segmentIndex < segments.Count - 1) CommitCell();
                    }
                    continue;
                }
                if (currentCell is not null)
                {
                    AppendCellText(currentCell, line, lineOffsets[index]);
                    continue;
                }
                AddDiagnostic(_document.Diagnostics, options, "TableContentOutsideCell", "MediaWiki table content appeared before a cell and was ignored.", lineOffsets[index], line.Length);
            }

            CommitRow();
            AddDiagnostic(_document.Diagnostics, options, "UnclosedTable", "MediaWiki table was not closed with |}; bounded table content was rendered.", lineOffsets[start], _source.Length - lineOffsets[start], MediaWikiDiagnosticSeverity.Error);
            lineIndex = lines.Length - 1;
            table = new TableBlock(attributes, caption, rows, lineOffsets[start]);
            _document.Tables.Add(ToPublicTable(table));
            return true;
        }

        private bool TryParseGallery(string[] lines, int[] lineOffsets, ref int lineIndex, out GalleryBlock gallery)
        {
            var start = lineIndex;
            var opening = lines[start].Trim();
            var close = opening.IndexOf('>');
            if (close < 0)
            {
                gallery = null!;
                return false;
            }
            var galleryOptions = ParseGalleryOptions(opening[8..close], lineOffsets[start] + 8);
            var items = new List<GalleryItem>();
            var foundClose = false;
            for (var index = start + 1; index < lines.Length; index++)
            {
                var trimmed = lines[index].Trim();
                if (trimmed.StartsWith("</gallery", StringComparison.OrdinalIgnoreCase))
                {
                    lineIndex = index;
                    foundClose = true;
                    break;
                }
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (items.Count >= options.MaxGalleryItems)
                {
                    AddDiagnostic(_document.Diagnostics, options, "GalleryItemLimitExceeded", "MediaWiki gallery item count exceeded the configured limit; additional items were omitted.", lineOffsets[index], trimmed.Length, MediaWikiDiagnosticSeverity.Error);
                    continue;
                }
                var parts = SplitTopLevel(trimmed, '|');
                var file = DecodeTitle(parts[0]);
                if (file.StartsWith("File:", StringComparison.OrdinalIgnoreCase) || file.StartsWith("Image:", StringComparison.OrdinalIgnoreCase)) file = file[(file.IndexOf(':') + 1)..].Trim();
                else
                {
                    AddDiagnostic(_document.Diagnostics, options, "InvalidGalleryItem", "Gallery entries must reference an imported File or Image title.", lineOffsets[index], trimmed.Length);
                    continue;
                }
                var caption = string.Join('|', parts.Skip(1)).Trim();
                items.Add(new GalleryItem(file, caption, ParseInline(caption, lineOffsets[index], 0), lineOffsets[index], trimmed.Length));
            }
            if (!foundClose)
            {
                AddDiagnostic(_document.Diagnostics, options, "UnclosedGallery", "MediaWiki gallery was not closed with </gallery>; bounded gallery content was rendered.", lineOffsets[start], _source.Length - lineOffsets[start], MediaWikiDiagnosticSeverity.Error);
                lineIndex = lines.Length - 1;
            }
            gallery = new GalleryBlock(galleryOptions, items, lineOffsets[start]);
            return true;
        }

        private void AppendCellText(MutableTableCell cell, string value, int offset)
        {
            if (cell.Text.Length >= options.MaxTableCellCharacters) return;
            var remaining = options.MaxTableCellCharacters - cell.Text.Length;
            if (value.Length > remaining)
            {
                cell.Text.Append(value.AsSpan(0, remaining));
                AddDiagnostic(_document.Diagnostics, options, "TableCellSizeLimitExceeded", "MediaWiki table cell content exceeded the configured limit and was truncated.", offset, value.Length, MediaWikiDiagnosticSeverity.Error);
                return;
            }
            if (cell.Text.Length > 0) cell.Text.Append('\n');
            cell.Text.Append(value);
        }

        private TableAttributeSet ParseTableAttributes(string text, int offset)
        {
            var classes = new List<string>();
            string? id = null;
            string? role = null;
            string? scope = null;
            string? ariaLabel = null;
            int? rowspan = null;
            int? colspan = null;
            var count = 0;
            foreach (Match match in AttributeToken.Matches(text))
            {
                if (++count > options.MaxTableAttributes)
                {
                    AddDiagnostic(_document.Diagnostics, options, "TableAttributeLimitExceeded", "MediaWiki table attribute count exceeded the configured limit; additional attributes were ignored.", offset, text.Length, MediaWikiDiagnosticSeverity.Error);
                    break;
                }
                var name = match.Groups["name"].Value.ToLowerInvariant();
                var value = match.Groups["value"].Value.Trim();
                switch (name)
                {
                    case "class":
                        foreach (var token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                            if (SafeToken.IsMatch(token)) classes.Add(token);
                            else AddDiagnostic(_document.Diagnostics, options, "UnsafeTableClass", "An unsafe table class was ignored.", offset, value.Length);
                        break;
                    case "id":
                        if (SafeToken.IsMatch(value)) id = value; else AddDiagnostic(_document.Diagnostics, options, "UnsafeTableId", "An unsafe table id was ignored.", offset, value.Length);
                        break;
                    case "role":
                        if (value is "table" or "row" or "presentation") role = value; else AddDiagnostic(_document.Diagnostics, options, "UnsafeTableRole", "An unsupported table role was ignored.", offset, value.Length);
                        break;
                    case "scope":
                        if (value is "row" or "col" or "rowgroup" or "colgroup") scope = value; else AddDiagnostic(_document.Diagnostics, options, "UnsafeTableScope", "An unsupported table scope was ignored.", offset, value.Length);
                        break;
                    case "aria-label":
                        if (value.Length <= 256 && !value.Any(char.IsControl)) ariaLabel = value; else AddDiagnostic(_document.Diagnostics, options, "UnsafeTableAriaLabel", "An unsafe aria-label was ignored.", offset, value.Length);
                        break;
                    case "rowspan": rowspan = ParseSpan(value, offset, "rowspan"); break;
                    case "colspan": colspan = ParseSpan(value, offset, "colspan"); break;
                    case "style":
                        AddDiagnostic(_document.Diagnostics, options, "UnsafeTableStyle", "Inline table styles are not enabled and were ignored.", offset, value.Length);
                        break;
                    default:
                        AddDiagnostic(_document.Diagnostics, options, name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ? "UnsafeTableAttribute" : "UnsupportedTableAttribute", $"Table attribute '{name}' was ignored.", offset, value.Length);
                        break;
                }
            }
            if (text.Contains("javascript:", StringComparison.OrdinalIgnoreCase)) AddDiagnostic(_document.Diagnostics, options, "UnsafeTableUrl", "Unsafe table URL content was ignored.", offset, text.Length);
            return new TableAttributeSet(classes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), id, role, scope, ariaLabel, rowspan, colspan);
        }

        private int? ParseSpan(string value, int offset, string name)
        {
            if (int.TryParse(value, out var parsed) && parsed is >= 1 and <= 64) return parsed;
            AddDiagnostic(_document.Diagnostics, options, "InvalidTableSpan", $"Table {name} must be an integer between 1 and 64; the default was used.", offset, value.Length);
            return null;
        }

        private GalleryOptions ParseGalleryOptions(string text, int offset)
        {
            var mode = "traditional";
            int? width = null;
            int? height = null;
            int? perrow = null;
            foreach (Match match in AttributeToken.Matches(text))
            {
                var name = match.Groups["name"].Value.ToLowerInvariant();
                var value = match.Groups["value"].Value.Trim();
                switch (name)
                {
                    case "mode":
                        if (value is "traditional" or "packed" or "nolines") mode = value;
                        else AddDiagnostic(_document.Diagnostics, options, "UnsupportedGalleryMode", $"Gallery mode '{value}' is not supported; traditional mode was used.", offset, value.Length);
                        break;
                    case "widths": width = ParseMediaDimension(value, offset, "gallery width"); break;
                    case "heights": height = ParseMediaDimension(value, offset, "gallery height"); break;
                    case "perrow":
                        if (int.TryParse(value, out var parsed) && parsed is >= 1 and <= 12) perrow = parsed;
                        else AddDiagnostic(_document.Diagnostics, options, "InvalidGalleryPerRow", "Gallery perrow must be between 1 and 12 and was ignored.", offset, value.Length);
                        break;
                    default: AddDiagnostic(_document.Diagnostics, options, "UnsupportedGalleryAttribute", $"Gallery attribute '{name}' was ignored.", offset, value.Length); break;
                }
            }
            return new GalleryOptions(mode, width, height, perrow);
        }

        private MediaDisplayOptions ParseMediaOptions(IReadOnlyList<string> values, int offset, int length, int depth)
        {
            var style = MediaDisplayStyle.Default;
            var alignment = MediaAlignment.None;
            int? width = null;
            int? height = null;
            string? alt = null;
            string? link = null;
            var captions = new List<string>();
            foreach (var raw in values)
            {
                var value = raw.Trim();
                if (value.Equals("thumb", StringComparison.OrdinalIgnoreCase) || value.Equals("thumbnail", StringComparison.OrdinalIgnoreCase)) style = MediaDisplayStyle.Thumb;
                else if (value.Equals("frame", StringComparison.OrdinalIgnoreCase)) style = MediaDisplayStyle.Frame;
                else if (value.Equals("frameless", StringComparison.OrdinalIgnoreCase)) style = MediaDisplayStyle.Frameless;
                else if (value.Equals("border", StringComparison.OrdinalIgnoreCase)) style = MediaDisplayStyle.Border;
                else if (Enum.TryParse<MediaAlignment>(value, true, out var parsedAlignment)) alignment = parsedAlignment;
                else if (value.StartsWith("alt=", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = value[4..].Trim();
                    if (candidate.Length <= 256 && !candidate.Any(char.IsControl)) alt = candidate;
                    else AddDiagnostic(_document.Diagnostics, options, "UnsafeMediaAlt", "The media alt text was rejected.", offset, length);
                }
                else if (value.StartsWith("link=", StringComparison.OrdinalIgnoreCase)) link = value[5..].Trim();
                else if (value.Equals("upright", StringComparison.OrdinalIgnoreCase) || value.StartsWith("upright=", StringComparison.OrdinalIgnoreCase))
                {
                    var multiplier = 0.75m;
                    var rawMultiplier = value.Contains('=') ? value[(value.IndexOf('=') + 1)..] : null;
                    if (rawMultiplier is not null && (!decimal.TryParse(rawMultiplier, out multiplier) || multiplier <= 0 || multiplier > 4))
                    {
                        AddDiagnostic(_document.Diagnostics, options, "InvalidMediaUpright", "The media upright value was ignored.", offset, length);
                        continue;
                    }
                    width = (int)Math.Clamp(Math.Round(250m * multiplier), 1m, (decimal)options.MaxMediaDimension);
                }
                else if (TryParseDimensionPair(value, out var parsedWidth, out var parsedHeight))
                {
                    width = parsedWidth ?? width;
                    height = parsedHeight ?? height;
                }
                else if (value.StartsWith("class=", StringComparison.OrdinalIgnoreCase))
                    AddDiagnostic(_document.Diagnostics, options, "UnsupportedMediaAttribute", "Arbitrary media classes are not permitted.", offset, length);
                else if (value.Contains('=') && value[..value.IndexOf('=')].Trim().Length > 0)
                    AddDiagnostic(_document.Diagnostics, options, "UnsupportedMediaAttribute", "The media option was not in the supported allow-list and was ignored.", offset, length);
                else captions.Add(value);
            }
            var captionText = string.Join(" | ", captions);
            return new MediaDisplayOptions(style, alignment, width, height, alt, link, captionText, ParseInline(captionText, offset, depth + 1));
        }

        private int? ParseMediaDimension(string value, int offset, string label)
        {
            var normalized = value.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? value[..^2] : value;
            if (int.TryParse(normalized, out var parsed) && parsed is >= 1 && parsed <= options.MaxMediaDimension) return parsed;
            AddDiagnostic(_document.Diagnostics, options, "InvalidMediaDimension", $"The {label} was ignored because it exceeded the configured bounds.", offset, value.Length);
            return null;
        }

        private bool TryParseDimensionPair(string value, out int? width, out int? height)
        {
            width = null;
            height = null;
            if (!value.EndsWith("px", StringComparison.OrdinalIgnoreCase)) return false;
            var normalized = value[..^2];
            var pieces = normalized.Split('x', 2, StringSplitOptions.TrimEntries);
            if (pieces.Length is < 1 or > 2 || !int.TryParse(pieces[0], out var parsedWidth)) return false;
            if (parsedWidth is < 1 || parsedWidth > options.MaxMediaDimension) { AddDiagnostic(_document.Diagnostics, options, "InvalidMediaDimension", "The media width was ignored because it exceeded the configured bounds.", 0, value.Length); return true; }
            width = parsedWidth;
            if (pieces.Length == 2)
            {
                if (!int.TryParse(pieces[1], out var parsedHeight) || parsedHeight < 1 || parsedHeight > options.MaxMediaDimension)
                {
                    AddDiagnostic(_document.Diagnostics, options, "InvalidMediaDimension", "The media height was ignored because it exceeded the configured bounds.", 0, value.Length);
                    return true;
                }
                height = parsedHeight;
            }
            return true;
        }

        private static bool LooksLikeAttributes(string value)
            => AttributeToken.IsMatch(value) || value.TrimStart().StartsWith("style", StringComparison.OrdinalIgnoreCase);

        private static IReadOnlyList<string> SplitTopLevelToken(string value, string separator)
        {
            var result = new List<string>();
            var start = 0;
            var braces = 0;
            var links = 0;
            for (var index = 0; index <= value.Length - separator.Length; index++)
            {
                if (value.AsSpan(index).StartsWith("{{", StringComparison.Ordinal)) { braces++; index++; continue; }
                if (value.AsSpan(index).StartsWith("}}", StringComparison.Ordinal) && braces > 0) { braces--; index++; continue; }
                if (value.AsSpan(index).StartsWith("[[", StringComparison.Ordinal)) { links++; index++; continue; }
                if (value.AsSpan(index).StartsWith("]]", StringComparison.Ordinal) && links > 0) { links--; index++; continue; }
                if (braces == 0 && links == 0 && value.AsSpan(index).StartsWith(separator, StringComparison.Ordinal))
                {
                    result.Add(value[start..index]);
                    start = index + separator.Length;
                    index += separator.Length - 1;
                }
            }
            result.Add(value[start..]);
            return result;
        }

        private MediaWikiTableRenderModel ToPublicTable(TableBlock table)
            => new(table.Attributes.Classes.FirstOrDefault(), table.Attributes.Id, table.Caption?.RawText,
                table.Rows.Select(row => new MediaWikiTableRowRenderModel(row.Attributes.Classes.FirstOrDefault(),
                    row.Cells.Select(cell => new MediaWikiTableCellRenderModel(cell.IsHeader, cell.Attributes.RowSpan ?? 1, cell.Attributes.ColumnSpan ?? 1, cell.Attributes.Classes.FirstOrDefault(), cell.Attributes.Id, cell.Attributes.Scope, cell.Offset)).ToList(), row.Offset)).ToList(), table.Offset);

        private sealed class MutableTableRow(TableAttributeSet attributes, int offset)
        {
            public TableAttributeSet Attributes { get; } = attributes;
            public int Offset { get; } = offset;
            public List<TableCell> Cells { get; } = new();
        }

        private sealed class MutableTableCell(bool isHeader, TableAttributeSet attributes, int offset, int length)
        {
            public bool IsHeader { get; } = isHeader;
            public TableAttributeSet Attributes { get; } = attributes;
            public int Offset { get; } = offset;
            public int Length { get; } = length;
            public StringBuilder Text { get; } = new();
            public List<CellContent> Contents { get; } = new();
            public void FlushText(Parser parser)
            {
                if (Text.Length == 0 && Contents.Count > 0) return;
                Contents.Add(new InlineCellContent(parser.ParseInline(Text.ToString(), Offset, 1)));
                Text.Clear();
            }
        }

        private IReadOnlyList<InlineNode> ParseInline(string text, int sourceOffset, int depth)
        {
            if (depth > options.MaxParseDepth)
            {
                AddDiagnostic(_document.Diagnostics, options, "ParseDepthExceeded", "MediaWiki nesting exceeded the configured depth limit.", sourceOffset, text.Length, MediaWikiDiagnosticSeverity.Error);
                return [new TextNode(text, sourceOffset, text.Length)];
            }

            var nodes = new List<InlineNode>();
            var textStart = 0;
            void FlushText(int end)
            {
                if (end > textStart) nodes.Add(new TextNode(text[textStart..end], sourceOffset + textStart, end - textStart));
            }

            for (var index = 0; index < text.Length; index++)
            {
                if (text.AsSpan(index).StartsWith(MediaWikiInfoboxCompatibility.TokenPrefix, StringComparison.Ordinal))
                {
                    var tokenEnd = text.IndexOf(MediaWikiInfoboxCompatibility.TokenSuffix, index + MediaWikiInfoboxCompatibility.TokenPrefix.Length);
                    if (tokenEnd >= 0 && MediaWikiInfoboxCompatibility.TryDecode(text[index..(tokenEnd + 1)], out var infobox))
                    {
                        FlushText(index);
                        nodes.Add(new InfoboxNode(infobox, sourceOffset + index, tokenEnd + 1 - index));
                        index = tokenEnd;
                        textStart = index + 1;
                        continue;
                    }
                }
                if (text[index] == '[' && index + 1 < text.Length && text[index + 1] == '[' && TryFindBalanced(text, index, "[[", "]]", out var linkEnd))
                {
                    FlushText(index);
                    var inner = text[(index + 2)..linkEnd];
                    var parts = SplitTopLevel(inner, '|');
                    var target = DecodeTitle(parts[0]);
                    var absolute = sourceOffset + index;
                    if (target.StartsWith("Category:", StringComparison.OrdinalIgnoreCase))
                    {
                        var category = target[9..].Trim();
                        _document.Categories.Add(new(category, absolute));
                        nodes.Add(new CategoryNode(category, absolute, linkEnd + 2 - index));
                    }
                    else if (target.StartsWith("File:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("Image:", StringComparison.OrdinalIgnoreCase))
                    {
                        var file = target[(target.IndexOf(':') + 1)..].Trim();
                        var mediaOptions = ParseMediaOptions(parts.Skip(1).Select(DecodeTitle).ToList(), absolute, linkEnd + 2 - index, depth);
                        var caption = mediaOptions.CaptionInlines.Count == 0 ? null : string.Join(" ", mediaOptions.CaptionInlines.OfType<TextNode>().Select(textNode => textNode.Value));
                        nodes.Add(new MediaLinkNode(file, caption, absolute, linkEnd + 2 - index, mediaOptions));
                    }
                    else
                    {
                        nodes.Add(new InternalLinkNode(target, parts.Count > 1 ? DecodeTitle(string.Join('|', parts.Skip(1))) : null, absolute, linkEnd + 2 - index));
                    }
                    index = linkEnd + 1;
                    textStart = index + 1;
                    continue;
                }

                if (text[index] == '[' && index + 1 < text.Length && text[index + 1] == '[')
                {
                    FlushText(index);
                    AddDiagnostic(_document.Diagnostics, options, "UnbalancedLink", "Unbalanced MediaWiki link delimiter was rendered as text.", sourceOffset + index, text.Length - index);
                    nodes.Add(new TextNode(text[index..], sourceOffset + index, text.Length - index));
                    break;
                }

                if (text[index] == '[' && (index + 1 >= text.Length || text[index + 1] != '[') && TryFindSingleBracket(text, index, out var externalEnd))
                {
                    FlushText(index);
                    var inner = text[(index + 1)..externalEnd].Trim();
                    var split = inner.IndexOfAny([' ', '\t']);
                    var url = split < 0 ? inner : inner[..split];
                    var label = split < 0 ? null : inner[(split + 1)..].Trim();
                    nodes.Add(new ExternalLinkNode(url, label, sourceOffset + index, externalEnd + 1 - index));
                    index = externalEnd;
                    textStart = index + 1;
                    continue;
                }

                if (text[index] == '{' && index + 1 < text.Length && text[index + 1] == '{')
                {
                    var parameter = index + 2 < text.Length && text[index + 2] == '{';
                    var open = parameter ? "{{{" : "{{";
                    var close = parameter ? "}}}" : "}}";
                    if (TryFindBalanced(text, index, open, close, out var braceEnd))
                    {
                        FlushText(index);
                        var body = text[(index + open.Length)..braceEnd].Trim();
                        var parserFunction = body.StartsWith('#');
                        var kind = parameter ? "parameter" : parserFunction ? "parser-function" : "template";
                        var code = parameter ? "UnsupportedParameter" : parserFunction ? "UnsupportedParserFunction" : "UnsupportedTemplate";
                        var message = $"MediaWiki {kind} was recognized but is not evaluated.";
                        _document.UnsupportedConstructs.Add(kind);
                        nodes.Add(new UnsupportedNode(kind, code, message, sourceOffset + index, braceEnd + close.Length - index));
                        index = braceEnd + close.Length - 1;
                        textStart = index + 1;
                        continue;
                    }

                    FlushText(index);
                    AddDiagnostic(_document.Diagnostics, options, "UnbalancedDelimiter", "Unbalanced MediaWiki template or parameter delimiter was rendered as text.", sourceOffset + index, text.Length - index);
                    nodes.Add(new TextNode(text[index..], sourceOffset + index, text.Length - index));
                    break;
                }

                if (text[index] == '\'' && TryParseEmphasis(text, index, sourceOffset, depth, out var emphasis, out var emphasisEnd))
                {
                    FlushText(index);
                    nodes.Add(emphasis!);
                    index = emphasisEnd;
                    textStart = index + 1;
                    continue;
                }

                if (text[index] == '<' && TryParseTag(text, index, sourceOffset, depth, out var tag, out var tagEnd))
                {
                    FlushText(index);
                    if (tag is not null) nodes.Add(tag);
                    index = tagEnd;
                    textStart = index + 1;
                    continue;
                }
            }

            FlushText(text.Length);
            return nodes;
        }

        internal (IReadOnlyList<InlineNode> Nodes, IReadOnlyList<MediaWikiCompatibilityDiagnostic> Diagnostics) ParseInlineFragment()
        {
            var nodes = ParseInline(_source, 0, 0);
            return (nodes, _document.Diagnostics);
        }

        private bool TryParseTag(string text, int index, int sourceOffset, int depth, out InlineNode? node, out int end)
        {
            node = null;
            end = index;
            var close = text.IndexOf('>', index + 1);
            if (close < 0) return false;
            var tagText = text[(index + 1)..close].Trim();
            var selfClosing = tagText.EndsWith('/');
            var normalizedTagText = tagText.TrimStart('/').TrimEnd('/');
            var tagNameEnd = normalizedTagText.IndexOfAny([' ', '\t', '\r', '\n']);
            var tagName = tagNameEnd < 0 ? normalizedTagText : normalizedTagText[..tagNameEnd];
            if (tagName.Equals(MediaWikiMermaidCompatibility.ExtensionName, StringComparison.OrdinalIgnoreCase))
                return TryParseMermaidTag(text, index, close, sourceOffset, tagText, selfClosing, out node, out end);
            if (tagName.Equals(MediaWikiTemplateStylesCompatibility.ExtensionName, StringComparison.OrdinalIgnoreCase))
                return TryParseTemplateStylesTag(text, index, close, sourceOffset, tagText, selfClosing, out node, out end);
            if (tagName.Equals("references", StringComparison.OrdinalIgnoreCase) && selfClosing)
            {
                node = new ReferencesNode(sourceOffset + index, close + 1 - index);
                end = close;
                return true;
            }
            if (!tagName.Equals("ref", StringComparison.OrdinalIgnoreCase))
            {
                if (tagText.StartsWith('/') || tagText.StartsWith('!')) return false;
                if (tagName.Equals("nowiki", StringComparison.OrdinalIgnoreCase))
                {
                    var nowikiEnd = text.IndexOf("</nowiki>", close + 1, StringComparison.OrdinalIgnoreCase);
                    if (nowikiEnd >= 0)
                    {
                        node = new TextNode(text[(close + 1)..nowikiEnd], sourceOffset + close + 1, nowikiEnd - close - 1);
                        end = nowikiEnd + 9 - 1;
                        return true;
                    }
                }
                var closing = text.IndexOf("</" + tagName + ">", close + 1, StringComparison.OrdinalIgnoreCase);
                if (closing >= 0)
                {
                    AddUnsupportedTag(tagName, sourceOffset + index, closing + tagName.Length + 3 - index);
                    node = new UnsupportedNode("extension-tag", "UnsupportedExtensionTag", $"Extension tag '{tagName}' is not evaluated.", sourceOffset + index, closing + tagName.Length + 3 - index);
                    end = closing + tagName.Length + 2;
                    return true;
                }
                AddUnsupportedTag(tagName, sourceOffset + index, close + 1 - index);
                node = new UnsupportedNode("extension-tag", "UnsupportedExtensionTag", $"Extension tag '{tagName}' is not evaluated.", sourceOffset + index, close + 1 - index);
                end = close;
                return true;
            }

            var closingRef = text.IndexOf("</ref>", close + 1, StringComparison.OrdinalIgnoreCase);
            if (closingRef < 0)
            {
                AddDiagnostic(_document.Diagnostics, options, "UnbalancedReferenceTag", "Unclosed <ref> tag was rendered as an unsupported construct.", sourceOffset + index, text.Length - index);
                node = new UnsupportedNode("reference", "UnbalancedReferenceTag", "Unclosed <ref> tag.", sourceOffset + index, text.Length - index);
                end = text.Length - 1;
                return true;
            }

            var attributes = tagText[(tagName.Length)..];
            var name = ReadAttribute(attributes, "name");
            var body = text[(close + 1)..closingRef];
            node = new ReferenceNode(name, ParseInline(body, sourceOffset + close + 1, depth + 1), sourceOffset + index, closingRef + 6 - index);
            end = closingRef + 5;
            return true;
        }

        private bool TryParseTemplateStylesTag(
            string text,
            int index,
            int close,
            int sourceOffset,
            string tagText,
            bool selfClosing,
            out InlineNode? node,
            out int end)
        {
            node = null;
            end = close;
            var offset = sourceOffset + index;
            if (tagText.StartsWith("/", StringComparison.Ordinal))
            {
                node = new TemplateStylesNode(null, "A closing TemplateStyles tag did not have a matching opening tag.", offset, close + 1 - index);
                return true;
            }

            if (!MediaWikiTemplateStylesCompatibility.TryParseAttributes(tagText, out var attributes, out var parsedSelfClosing, out var attributeError))
            {
                node = new TemplateStylesNode(null, attributeError ?? "TemplateStyles attributes were invalid.", offset, close + 1 - index);
                return true;
            }

            selfClosing |= parsedSelfClosing;
            if (!selfClosing)
            {
                var closing = text.IndexOf("</templatestyles>", close + 1, StringComparison.OrdinalIgnoreCase);
                if (closing < 0)
                {
                    node = new TemplateStylesNode(null, "The TemplateStyles tag must be self-closing or explicitly closed.", offset, close + 1 - index);
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(text[(close + 1)..closing]))
                {
                    node = new TemplateStylesNode(null, "TemplateStyles does not accept a body; use a self-closing tag.", offset, closing + "</templatestyles>".Length - index);
                }
                else
                {
                    node = new TemplateStylesNode(attributes.GetValueOrDefault("src"), null, offset, closing + "</templatestyles>".Length - index);
                }
                end = closing + "</templatestyles>".Length - 1;
                return true;
            }

            node = new TemplateStylesNode(attributes.GetValueOrDefault("src"), null, offset, close + 1 - index);
            end = close;
            return true;
        }

        private bool TryParseMermaidTag(
            string text,
            int index,
            int close,
            int sourceOffset,
            string tagText,
            bool selfClosing,
            out InlineNode? node,
            out int end)
        {
            node = null;
            end = close;
            var offset = sourceOffset + index;
            if (tagText.StartsWith("/", StringComparison.Ordinal))
            {
                AddDiagnostic(_document.Diagnostics, options, "InvalidExtensionInput", "A closing Mermaid tag did not have a matching opening tag.", offset, close + 1 - index);
                node = new UnsupportedNode("extension-tag", "InvalidExtensionInput", "A closing Mermaid tag did not have a matching opening tag.", offset, close + 1 - index);
                return true;
            }

            if (!MediaWikiMermaidCompatibility.TryParseAttributes(tagText, out var attributes, out var parsedSelfClosing, out var attributeError))
            {
                AddDiagnostic(_document.Diagnostics, options, "InvalidExtensionInput", attributeError ?? "Mermaid tag attributes were invalid.", offset, close + 1 - index);
                node = new UnsupportedNode("extension-tag", "InvalidExtensionInput", attributeError ?? "Mermaid tag attributes were invalid.", offset, close + 1 - index);
                return true;
            }

            selfClosing |= parsedSelfClosing;
            var closing = selfClosing ? -1 : text.IndexOf("</mermaid>", close + 1, StringComparison.OrdinalIgnoreCase);
            if (closing < 0)
            {
                var message = selfClosing
                    ? "A Mermaid extension tag must contain a bounded source body."
                    : "The Mermaid extension tag was not closed.";
                AddDiagnostic(_document.Diagnostics, options, "InvalidExtensionInput", message, offset, close + 1 - index);
                node = new UnsupportedNode("extension-tag", "InvalidExtensionInput", message, offset, close + 1 - index);
                return true;
            }

            var body = text[(close + 1)..closing];
            var length = closing + "</mermaid>".Length - index;
            var result = MediaWikiMermaidCompatibility.CreateStaticResult(new(
                MediaWikiMermaidCompatibility.ExtensionName,
                body,
                attributes,
                provenance,
                options.MaxMermaidInputCharacters,
                options.MaxMermaidNestingDepth,
                1,
                cancellationToken));
            if (result.Html is not null && result.DiagnosticCode is not null)
            {
                _document.UnsupportedConstructs.Add("mermaid-diagram-rendering");
                node = new StaticExtensionNode(result.Html, result.DiagnosticCode, result.Message, offset, length, result.Severity);
            }
            else
            {
                _document.UnsupportedConstructs.Add("mermaid");
                node = new UnsupportedNode("extension-tag", result.DiagnosticCode ?? "UnsupportedExtension", result.Message, offset, length, result.Severity);
            }
            end = closing + "</mermaid>".Length - 1;
            return true;
        }

        private void AddUnsupportedTag(string tagName, int offset, int length)
        {
            _document.UnsupportedConstructs.Add(tagName);
            AddDiagnostic(_document.Diagnostics, options, "UnsupportedExtensionTag", $"Extension tag '{tagName}' is not evaluated.", offset, length);
        }

        private bool TryParseEmphasis(string text, int index, int sourceOffset, int depth, out InlineNode? node, out int end)
        {
            node = null;
            end = index;
            var (open, closeToken, isBold) = text.AsSpan(index).StartsWith("'''''", StringComparison.Ordinal)
                ? (5, "'''''", true)
                : text.AsSpan(index).StartsWith("'''", StringComparison.Ordinal)
                    ? (3, "'''", true)
                    : text.AsSpan(index).StartsWith("''", StringComparison.Ordinal)
                        ? (2, "''", false) : (0, string.Empty, false);
            if (open == 0) return false;
            var close = text.IndexOf(closeToken, index + open, StringComparison.Ordinal);
            if (close < 0)
            {
                AddDiagnostic(_document.Diagnostics, options, "UnbalancedEmphasis", "Unbalanced MediaWiki emphasis delimiter was rendered as text.", sourceOffset + index, text.Length - index);
                return false;
            }
            var body = ParseInline(text[(index + open)..close], sourceOffset + index + open, depth + 1);
            node = open == 5
                ? new BoldNode([new ItalicNode(body, sourceOffset + index, close + open - index)], sourceOffset + index, close + open - index)
                : isBold ? new BoldNode(body, sourceOffset + index, close + open - index) : new ItalicNode(body, sourceOffset + index, close + open - index);
            end = close + open - 1;
            return true;
        }

        private bool TryParseHeading(string line, int offset, out HeadingBlock heading)
        {
            heading = null!;
            var leading = 0;
            while (leading < line.Length && line[leading] == '=') leading++;
            if (leading is < 1 or > 6) return false;
            var trailing = 0;
            while (trailing < line.Length && line[line.Length - trailing - 1] == '=') trailing++;
            if (trailing != leading || line.Length <= leading + trailing) return false;
            var body = line[leading..^trailing].Trim();
            heading = new HeadingBlock(leading, ParseInline(body, offset + leading, 0), offset);
            return true;
        }

        private bool TryParseListItem(string line, int offset, out ListItem item)
        {
            var depth = 0;
            while (depth < line.Length && (line[depth] == '*' || line[depth] == '#')) depth++;
            if (depth == 0)
            {
                item = null!;
                return false;
            }
            var ordered = line[0] == '#';
            var body = line[depth..].TrimStart();
            item = new ListItem(depth - 1, ordered, ParseInline(body, offset + depth, 0), offset);
            return true;
        }

        private static bool TryParseRedirect(string line, int offset, out RedirectBlock block)
        {
            block = null!;
            if (!line.StartsWith("#REDIRECT", StringComparison.OrdinalIgnoreCase)) return false;
            var open = line.IndexOf("[[", StringComparison.Ordinal);
            var close = open < 0 ? -1 : line.IndexOf("]]", open + 2, StringComparison.Ordinal);
            if (open < 0 || close < 0) return false;
            block = new RedirectBlock(line[(open + 2)..close].Trim(), offset);
            return true;
        }

        private static bool TryFindBalanced(string text, int start, string open, string close, out int end)
        {
            var depth = 0;
            for (var index = start; index <= text.Length - open.Length; index++)
            {
                if (text.AsSpan(index).StartsWith(open, StringComparison.Ordinal)) { depth++; index += open.Length - 1; continue; }
                if (index <= text.Length - close.Length && text.AsSpan(index).StartsWith(close, StringComparison.Ordinal))
                {
                    depth--;
                    if (depth == 0) { end = index; return true; }
                    index += close.Length - 1;
                }
            }
            end = -1;
            return false;
        }

        private static bool TryFindSingleBracket(string text, int start, out int end)
        {
            end = text.IndexOf(']', start + 1);
            return end > start;
        }

        private static List<string> SplitTopLevel(string value, char separator)
        {
            var result = new List<string>();
            var start = 0;
            var braces = 0;
            var links = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (value.AsSpan(index).StartsWith("{{", StringComparison.Ordinal)) { braces++; index++; continue; }
                if (value.AsSpan(index).StartsWith("}}", StringComparison.Ordinal) && braces > 0) { braces--; index++; continue; }
                if (value.AsSpan(index).StartsWith("[[", StringComparison.Ordinal)) { links++; index++; continue; }
                if (value.AsSpan(index).StartsWith("]]", StringComparison.Ordinal) && links > 0) { links--; index++; continue; }
                if (value[index] == separator && braces == 0 && links == 0) { result.Add(value[start..index]); start = index + 1; }
            }
            result.Add(value[start..]);
            return result;
        }

        private static string DecodeTitle(string value)
        {
            try { value = Uri.UnescapeDataString(value); } catch (UriFormatException) { }
            return value.Trim().Replace('_', ' ');
        }

        private static bool IsMediaOption(string value)
            => value.Equals("thumb", StringComparison.OrdinalIgnoreCase)
            || value.Equals("thumbnail", StringComparison.OrdinalIgnoreCase)
            || value.Equals("frame", StringComparison.OrdinalIgnoreCase)
            || value.Equals("right", StringComparison.OrdinalIgnoreCase)
            || value.Equals("left", StringComparison.OrdinalIgnoreCase)
            || value.Equals("center", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("alt=", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("class=", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("px", StringComparison.OrdinalIgnoreCase);

        private static string? ReadAttribute(string attributes, string name)
        {
            var marker = name + "=";
            var index = attributes.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;
            var value = attributes[(index + marker.Length)..].TrimStart();
            if (value.StartsWith('"'))
            {
                var end = value.IndexOf('"', 1);
                return end > 0 ? value[1..end] : null;
            }
            return value.Split([' ', '\t'], 2)[0];
        }
    }
}
