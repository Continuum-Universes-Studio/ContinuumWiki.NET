using DuoVia.FuzzyStrings;
using NTDLS.Helpers;
using TightWiki.Library;
using TightWiki.Library.Caching;
using TightWiki.Library.Security;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Models;
using static TightWiki.Plugin.TwConstants;

namespace TightWiki.Repository.Helpers;

/// <summary>Runs the existing wiki parser and atomically persists its derived page metadata.</summary>
public sealed class PageMetadataRebuilder(
    ITwApplicationConfigurationRepository configurationRepository,
    ITwApplicationPageSupportRepository pageSupportRepository)
{
    public sealed record Snapshot(
        IReadOnlyList<string> Tags,
        IReadOnlyList<TwPageReference> References,
        IReadOnlyList<string> Instructions,
        IReadOnlyList<TwPageToken> Tokens);

    public async Task<Snapshot> BuildSnapshotAsync(ITwEngine engine, ITwSharedLocalizationText localizer, TwPage page, ITwSessionState? sessionState = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var state = await engine.Transform(localizer, sessionState, page, null, [TwMatchType.StandardFunction]);
        var tokens = await BuildTokensAsync(state, cancellationToken);
        return new Snapshot(state.Tags, state.OutgoingLinks, state.ProcessingInstructions, tokens);
    }

    public async Task RebuildAsync(ITwEngine engine, ITwSharedLocalizationText localizer, TwPage page, ITwSessionState? sessionState = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await BuildSnapshotAsync(engine, localizer, page, sessionState, cancellationToken);
        await pageSupportRepository.ReplaceDerivedMetadataAsync(page.Id, snapshot.Tags, snapshot.References, snapshot.Instructions, snapshot.Tokens, cancellationToken);
        MemCache.ClearCategory(MemCacheKey.Build(MemCache.Category.Page, [page.Id]));
        MemCache.ClearCategory(MemCacheKey.Build(MemCache.Category.Page, [page.Navigation]));
    }

    private async Task<List<TwPageToken>> BuildTokensAsync(ITwEngineState state, CancellationToken cancellationToken)
    {
        var searchEntries = await configurationRepository.GetEntriesByGroupNameAsync(TwConfigGroup.Search, cancellationToken);
        var exclusions = searchEntries.FirstOrDefault(value => value.Name == "Word Exclusions")?.Value
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToHashSet(StringComparer.Ordinal) ?? [];
        var splitCamelCase = bool.TryParse(searchEntries.FirstOrDefault(value => value.Name == "Split Camel Case")?.Value, out var split) && split;
        var weighted = new List<(string Token, double Weight)>();
        weighted.AddRange(await ParseAsync(state.HtmlResult, 1, exclusions, splitCamelCase, cancellationToken));
        weighted.AddRange(await ParseAsync(state.Page.Description, 1.2, exclusions, splitCamelCase, cancellationToken));
        weighted.AddRange(await ParseAsync(string.Join(" ", state.Tags), 1.4, exclusions, splitCamelCase, cancellationToken));
        weighted.AddRange(await ParseAsync(state.Page.Name, 1.6, exclusions, splitCamelCase, cancellationToken));
        return weighted.GroupBy(value => value.Token).Select(group => new TwPageToken
        {
            PageId = state.Page.Id,
            Token = group.Key,
            DoubleMetaphone = group.Key.ToDoubleMetaphone(),
            Weight = group.Sum(value => value.Weight)
        }).ToList();
    }

    private static Task<List<(string Token, double Weight)>> ParseAsync(string content, double multiplier, HashSet<string> exclusions, bool splitCamelCase, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tokens = Html.StripHtml(content).Split(new[] { ' ', '\n', '\t', '-', '_' }).ToList();
        if (splitCamelCase)
        {
            foreach (var token in tokens.ToList())
            {
                var parts = Text.SplitCamelCase(token);
                if (parts.Count > 1) tokens.AddRange(parts);
            }
        }
        var result = tokens.Select(value => value.ToLowerInvariant()).Where(value => !exclusions.Contains(value) && !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value).Select(group => (group.Key, group.Count() * multiplier)).ToList();
        return Task.FromResult(result);
    }
}
