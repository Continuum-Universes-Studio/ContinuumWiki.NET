namespace TightWiki.Engine.MediaWiki;

public enum MediaWikiCargoPredicateOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    IsNull,
    IsNotNull
}

public sealed record MediaWikiCargoQueryIdentity(
    string SourceWikiIdentity,
    string SourcePageId,
    string SourceRevisionId,
    string? DatasetIdentity);

public sealed record MediaWikiCargoTableReference(string Name);

public sealed record MediaWikiCargoFieldReference(string Name);

public sealed record MediaWikiCargoPredicate(
    MediaWikiCargoFieldReference Field,
    MediaWikiCargoPredicateOperator Operator,
    string? Value);

public sealed record MediaWikiCargoSort(
    MediaWikiCargoFieldReference Field,
    bool Descending = false);

public sealed record MediaWikiCargoQueryBudget(
    int MaxRows = 256,
    int MaxFields = 32,
    int MaxArgumentCharacters = 8_192,
    int MaxOutputCharacters = 100_000,
    TimeSpan? MaxExecutionTime = null)
{
    public TimeSpan EffectiveExecutionTime => MaxExecutionTime ?? TimeSpan.FromMilliseconds(250);
}

public sealed record MediaWikiCargoQueryRequest(
    MediaWikiCargoQueryIdentity Identity,
    MediaWikiCargoTableReference Table,
    IReadOnlyList<MediaWikiCargoFieldReference> Fields,
    IReadOnlyList<MediaWikiCargoPredicate> Predicates,
    IReadOnlyList<MediaWikiCargoSort> Sort,
    int Offset,
    int Limit,
    MediaWikiCargoQueryBudget Budget);

public sealed record MediaWikiCargoResultColumn(string Name, string Value);

public sealed record MediaWikiCargoQueryResult(
    IReadOnlyList<IReadOnlyList<MediaWikiCargoResultColumn>> Rows,
    MediaWikiCargoQueryIdentity Identity,
    string DatasetProvenance);

public enum MediaWikiCargoProviderFailure
{
    ProviderUnavailable,
    InvalidQuery,
    UnsupportedQuery,
    RelationshipUnverified,
    SchemaMismatch,
    HistoricalProvenanceUnavailable,
    BudgetExceeded,
    Cancelled
}

public sealed record MediaWikiCargoProviderDiagnostic(
    MediaWikiCargoProviderFailure Failure,
    string Message);

public sealed record MediaWikiCargoProviderResult(
    MediaWikiCargoQueryResult? Result,
    MediaWikiCargoProviderDiagnostic? Diagnostic)
{
    public bool Succeeded => Result is not null && Diagnostic is null;
}

public interface IMediaWikiCargoQueryProvider
{
    Task<MediaWikiCargoProviderResult> QueryAsync(
        MediaWikiCargoQueryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class RejectingMediaWikiCargoQueryProvider : IMediaWikiCargoQueryProvider
{
    public Task<MediaWikiCargoProviderResult> QueryAsync(
        MediaWikiCargoQueryRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new MediaWikiCargoProviderResult(
            null,
            new(MediaWikiCargoProviderFailure.ProviderUnavailable,
                "No authorized historical Cargo dataset provider is configured.")));
}

public static class MediaWikiCargoQueryValidator
{
    public static IReadOnlyList<MediaWikiCargoProviderDiagnostic> Validate(
        MediaWikiCargoQueryRequest request,
        IReadOnlyDictionary<string, IReadOnlySet<string>> allowlist)
    {
        var errors = new List<MediaWikiCargoProviderDiagnostic>();
        if (request.Identity is null
            || string.IsNullOrWhiteSpace(request.Identity.SourceWikiIdentity)
            || string.IsNullOrWhiteSpace(request.Identity.SourcePageId)
            || string.IsNullOrWhiteSpace(request.Identity.SourceRevisionId))
            errors.Add(new(MediaWikiCargoProviderFailure.HistoricalProvenanceUnavailable, "A source wiki, page, and revision identity are required."));
        if (string.IsNullOrWhiteSpace(request.Identity?.DatasetIdentity))
            errors.Add(new(MediaWikiCargoProviderFailure.HistoricalProvenanceUnavailable, "A versioned Cargo dataset identity is required."));
        if (request.Table is null || !allowlist.TryGetValue(request.Table.Name, out var allowedFields))
            errors.Add(new(MediaWikiCargoProviderFailure.InvalidQuery, "The Cargo table is not allowlisted."));
        else
        {
            if (request.Fields.Count == 0 || request.Fields.Count > request.Budget.MaxFields)
                errors.Add(new(MediaWikiCargoProviderFailure.InvalidQuery, "The selected Cargo fields exceeded the configured limit."));
            foreach (var field in request.Fields.Concat(request.Sort.Select(value => value.Field)).Concat(request.Predicates.Select(value => value.Field)))
                if (!allowedFields.Contains(field.Name)) errors.Add(new(MediaWikiCargoProviderFailure.InvalidQuery, "A Cargo field is not allowlisted."));
            foreach (var predicate in request.Predicates)
                if (predicate.Operator is not (MediaWikiCargoPredicateOperator.Equals or MediaWikiCargoPredicateOperator.NotEquals or MediaWikiCargoPredicateOperator.Contains or MediaWikiCargoPredicateOperator.StartsWith or MediaWikiCargoPredicateOperator.IsNull or MediaWikiCargoPredicateOperator.IsNotNull))
                    errors.Add(new(MediaWikiCargoProviderFailure.UnsupportedQuery, "The Cargo predicate operator is unsupported."));
        }
        if (request.Offset < 0 || request.Limit < 0 || request.Limit > request.Budget.MaxRows)
            errors.Add(new(MediaWikiCargoProviderFailure.BudgetExceeded, "Cargo pagination exceeded the configured row budget."));
        if (request.Budget.MaxRows <= 0 || request.Budget.MaxFields <= 0 || request.Budget.MaxArgumentCharacters <= 0 || request.Budget.MaxOutputCharacters <= 0)
            errors.Add(new(MediaWikiCargoProviderFailure.BudgetExceeded, "Cargo query budgets must be positive."));
        var argumentCharacters = (request.Table?.Name.Length ?? 0)
            + request.Fields.Sum(value => value.Name.Length)
            + request.Predicates.Sum(value => value.Field.Name.Length + (value.Value?.Length ?? 0));
        if (argumentCharacters > request.Budget.MaxArgumentCharacters)
            errors.Add(new(MediaWikiCargoProviderFailure.BudgetExceeded, "Cargo query arguments exceeded the configured character budget."));
        return errors;
    }
}
