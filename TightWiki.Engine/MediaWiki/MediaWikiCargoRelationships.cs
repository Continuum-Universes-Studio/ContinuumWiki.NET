using System.Collections.Frozen;
using System.Data;
using System.Data.Common;

namespace TightWiki.Engine.MediaWiki;

public enum MediaWikiCargoRelationshipReadiness
{
    SchemaVerified,
    SemanticsVerified,
    SchemaOnly,
    Unverified,
    Unsupported
}

public enum MediaWikiCargoRelationshipProjection
{
    Value,
    Position
}

public enum MediaWikiCargoRelationshipDatasetClassification
{
    Synthetic,
    AuthenticSourceCurrentState,
    Historical
}

public sealed record MediaWikiCargoSchemaIdentity(string ColumnsSha256, string IndexesSha256)
{
    public bool IsWellFormed => IsSha256(ColumnsSha256) && IsSha256(IndexesSha256);

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(value => Uri.IsHexDigit(value));
}

public sealed record MediaWikiCargoRelationshipDatasetProvenance(
    string DatasetIdentity,
    MediaWikiCargoRelationshipDatasetClassification Classification,
    string SourceWikiIdentity,
    MediaWikiCargoSchemaIdentity SchemaIdentity,
    bool HistoricalCorrelationVerified);

public sealed class MediaWikiCargoRelationshipDefinition
{
    public MediaWikiCargoRelationshipDefinition(
        string logicalName,
        string parentTable,
        string helperTable,
        string parentLookupColumn,
        string parentStorageKeyColumn,
        string parentKeyColumn,
        string valueColumn,
        string orderColumn,
        IEnumerable<MediaWikiCargoRelationshipProjection> projections,
        int maxFanOut,
        MediaWikiCargoRelationshipReadiness readiness,
        MediaWikiCargoSchemaIdentity schemaIdentity,
        string evidence)
    {
        LogicalName = RequireLogicalName(logicalName, nameof(logicalName));
        var separator = LogicalName.IndexOf('.');
        if (separator <= 0 || separator == LogicalName.Length - 1)
            throw new ArgumentException("A relationship name must have the form Table.Field.", nameof(logicalName));
        ParentTable = RequireIdentifier(parentTable, nameof(parentTable));
        HelperTable = RequireIdentifier(helperTable, nameof(helperTable));
        ParentLookupColumn = RequireIdentifier(parentLookupColumn, nameof(parentLookupColumn));
        ParentStorageKeyColumn = RequireIdentifier(parentStorageKeyColumn, nameof(parentStorageKeyColumn));
        ParentKeyColumn = RequireIdentifier(parentKeyColumn, nameof(parentKeyColumn));
        ValueColumn = RequireIdentifier(valueColumn, nameof(valueColumn));
        OrderColumn = RequireIdentifier(orderColumn, nameof(orderColumn));
        AllowedProjections = projections.Distinct().ToFrozenSet();
        if (AllowedProjections.Count == 0) throw new ArgumentException("At least one projection is required.", nameof(projections));
        if (maxFanOut <= 0) throw new ArgumentOutOfRangeException(nameof(maxFanOut));
        if (!schemaIdentity.IsWellFormed) throw new ArgumentException("A verified schema identity is required.", nameof(schemaIdentity));
        if (string.IsNullOrWhiteSpace(evidence)) throw new ArgumentException("Evidence is required.", nameof(evidence));
        MaxFanOut = maxFanOut;
        Readiness = readiness;
        SchemaIdentity = schemaIdentity;
        Evidence = evidence;
    }

    public string LogicalName { get; }
    public string ParentTable { get; }
    public string HelperTable { get; }
    public string ParentLookupColumn { get; }
    public string ParentStorageKeyColumn { get; }
    public string ParentKeyColumn { get; }
    public string ValueColumn { get; }
    public string OrderColumn { get; }
    public IReadOnlySet<MediaWikiCargoRelationshipProjection> AllowedProjections { get; }
    public int MaxFanOut { get; }
    public MediaWikiCargoRelationshipReadiness Readiness { get; }
    public MediaWikiCargoSchemaIdentity SchemaIdentity { get; }
    public string Evidence { get; }

    public string LogicalField => LogicalName[(LogicalName.IndexOf('.') + 1)..];

    private static string RequireIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("The relationship identifier is invalid.", name);
        return value;
    }

    private static string RequireLogicalName(string value, string name)
    {
        var parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part.Any(character => !char.IsLetterOrDigit(character) && character != '_')))
            throw new ArgumentException("A relationship name must have the form Table.Field.", name);
        return value;
    }
}

public sealed class MediaWikiCargoRelationshipCatalog
{
    public static readonly MediaWikiCargoSchemaIdentity AuthorizedCurrentSchema = new(
        "ade3431abf2cc321e333eac35c858d25f8c97a52e0379436aa52917cda1ebc4f",
        "952e50120fa88ed06bb163209afadfed99ec867a8d6dd69de5ea113c166beaeb");

    private readonly IReadOnlyDictionary<string, MediaWikiCargoRelationshipDefinition> _definitions;

    public MediaWikiCargoRelationshipCatalog(IEnumerable<MediaWikiCargoRelationshipDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions.ToDictionary(value => value.LogicalName, StringComparer.Ordinal);
    }

    public IReadOnlyList<MediaWikiCargoRelationshipDefinition> Definitions
        => _definitions.Values.OrderBy(value => value.LogicalName, StringComparer.Ordinal).ToArray();

    public bool TryGet(string logicalName, out MediaWikiCargoRelationshipDefinition definition)
        => _definitions.TryGetValue(logicalName, out definition!);

    public static MediaWikiCargoRelationshipCatalog Default { get; } = CreateDefault();

    private static MediaWikiCargoRelationshipCatalog CreateDefault()
    {
        var definitions = new List<MediaWikiCargoRelationshipDefinition>();
        Add(definitions, "Variants.GeneticAncestor", "Variants", "cargo__Variants__GeneticAncestor");
        foreach (var field in new[] { "Servants", "UniversesCreated", "Spouses", "Consorts", "Companions", "NotableDivineSiblings", "NotableDivineOffspring" })
            Add(definitions, $"Divines.{field}", "Divines", $"cargo__Divines__{field}");
        foreach (var field in new[]
        {
            "Affiliations", "Partners", "Lovers", "Hair", "Eyes", "Feathers", "Scales", "Fur",
            "NotableRelatives", "StepSiblings", "SocialRelatives", "FullSiblings", "HalfSiblings",
            "Children", "DivineSpouse"
        })
            Add(definitions, $"Characters.{field}", "Characters", $"cargo__Characters__{field}");
        foreach (var field in new[]
        {
            "Races", "SpeciesRelatives", "AncestralSpecies", "EyeColors", "HairColors", "SkinColors",
            "FeatherColors", "ScaleColors", "FurColors", "Diet"
        })
            Add(definitions, $"Species.{field}", "Species", $"cargo__Species__{field}");

        return new(definitions);

        static void Add(
            ICollection<MediaWikiCargoRelationshipDefinition> target,
            string logicalName,
            string parentTable,
            string helperTable)
            => target.Add(new(
                logicalName,
                parentTable,
                helperTable,
                "Name",
                "_ID",
                "_rowID",
                "_value",
                "_position",
                [MediaWikiCargoRelationshipProjection.Value, MediaWikiCargoRelationshipProjection.Position],
                256,
                MediaWikiCargoRelationshipReadiness.SchemaOnly,
                AuthorizedCurrentSchema,
                "Authenticated index metadata identifies the helper table and indexed _rowID, _value, and _position columns. The column inventory has no helper-table column rows, no helper rows were captured, and relationship semantics are not independently verified."));
    }
}

public sealed record MediaWikiCargoRelationshipQuery(
    MediaWikiCargoQueryIdentity Identity,
    string Relationship,
    string ParentValue,
    IReadOnlyList<MediaWikiCargoRelationshipProjection> Projections,
    string? ValueEquals,
    int Offset,
    int Limit,
    MediaWikiCargoQueryBudget Budget,
    MediaWikiCargoRelationshipDatasetClassification DatasetClassification,
    MediaWikiCargoSchemaIdentity SchemaIdentity);

public sealed record MediaWikiCargoRelationshipSourceRow(string ParentValue, int Position, string? Value);

public sealed record MediaWikiCargoRelationshipResult(
    string Relationship,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows,
    MediaWikiCargoQueryIdentity Identity,
    MediaWikiCargoRelationshipDatasetProvenance DatasetProvenance);

public sealed record MediaWikiCargoRelationshipProviderResult(
    MediaWikiCargoRelationshipResult? Result,
    MediaWikiCargoProviderDiagnostic? Diagnostic)
{
    public bool Succeeded => Result is not null && Diagnostic is null;
}

public static class MediaWikiCargoRelationshipQueryValidator
{
    public static IReadOnlyList<MediaWikiCargoProviderDiagnostic> Validate(
        MediaWikiCargoRelationshipQuery request,
        MediaWikiCargoRelationshipCatalog catalog,
        MediaWikiCargoSchemaIdentity? expectedSchemaIdentity,
        bool allowSyntheticDataset)
    {
        var errors = new List<MediaWikiCargoProviderDiagnostic>();
        if (request.Identity is null
            || string.IsNullOrWhiteSpace(request.Identity.SourceWikiIdentity)
            || string.IsNullOrWhiteSpace(request.Identity.SourcePageId)
            || string.IsNullOrWhiteSpace(request.Identity.SourceRevisionId)
            || string.IsNullOrWhiteSpace(request.Identity.DatasetIdentity))
            errors.Add(new(MediaWikiCargoProviderFailure.HistoricalProvenanceUnavailable, "A complete Cargo relationship provenance identity is required."));
        if (!catalog.TryGet(request.Relationship, out var definition))
        {
            var message = string.Equals(request.Relationship, "Divines.Children", StringComparison.Ordinal)
                ? "Divines.Children has no verified base or helper-table storage and remains unsupported."
                : "The Cargo relationship is not allowlisted.";
            errors.Add(new(MediaWikiCargoProviderFailure.UnsupportedQuery, message));
            return errors;
        }
        if (definition.Readiness != MediaWikiCargoRelationshipReadiness.SemanticsVerified)
            errors.Add(new(MediaWikiCargoProviderFailure.RelationshipUnverified, "The relationship has schema evidence but no independently verified semantics or authorized relationship rows."));
        if (expectedSchemaIdentity is null || request.SchemaIdentity != expectedSchemaIdentity || request.SchemaIdentity != definition.SchemaIdentity)
            errors.Add(new(MediaWikiCargoProviderFailure.SchemaMismatch, "The Cargo relationship schema identity did not match the authorized metadata."));
        if (request.DatasetClassification == MediaWikiCargoRelationshipDatasetClassification.Historical)
            errors.Add(new(MediaWikiCargoProviderFailure.HistoricalProvenanceUnavailable, "Historical relationship data is not authorized by the current provider."));
        if (request.DatasetClassification == MediaWikiCargoRelationshipDatasetClassification.Synthetic && !allowSyntheticDataset)
            errors.Add(new(MediaWikiCargoProviderFailure.HistoricalProvenanceUnavailable, "Synthetic relationship data is not accepted by the database provider."));
        if (string.IsNullOrWhiteSpace(request.ParentValue))
            errors.Add(new(MediaWikiCargoProviderFailure.InvalidQuery, "A parent lookup value is required."));
        if (request.Projections.Count == 0 || request.Projections.Count > request.Budget.MaxFields || request.Projections.Any(value => !definition.AllowedProjections.Contains(value)))
            errors.Add(new(MediaWikiCargoProviderFailure.InvalidQuery, "A relationship projection is not allowlisted."));
        if (request.Projections.Count != request.Projections.Distinct().Count())
            errors.Add(new(MediaWikiCargoProviderFailure.InvalidQuery, "Relationship projections must be unique."));
        if (request.Offset < 0 || request.Limit < 0 || request.Limit > request.Budget.MaxRows || request.Limit > definition.MaxFanOut)
            errors.Add(new(MediaWikiCargoProviderFailure.BudgetExceeded, "Relationship pagination exceeded the configured fan-out budget."));
        if (request.Budget.MaxRows <= 0 || request.Budget.MaxFields <= 0 || request.Budget.MaxArgumentCharacters <= 0 || request.Budget.MaxOutputCharacters <= 0)
            errors.Add(new(MediaWikiCargoProviderFailure.BudgetExceeded, "Cargo relationship budgets must be positive."));
        var argumentCharacters = request.Relationship.Length + request.ParentValue.Length + (request.ValueEquals?.Length ?? 0);
        if (argumentCharacters > request.Budget.MaxArgumentCharacters)
            errors.Add(new(MediaWikiCargoProviderFailure.BudgetExceeded, "Cargo relationship arguments exceeded the configured character budget."));
        return errors;
    }
}

/// <summary>
/// Projects explicitly labelled synthetic helper rows into the same bounded
/// result shape used by the host-to-worker cargoResults bundle. It is not a
/// provider and cannot authorize or represent authentic Cargo data.
/// </summary>
public static class MediaWikiCargoRelationshipProjector
{
    public static MediaWikiCargoRelationshipResult ProjectSynthetic(
        MediaWikiCargoRelationshipQuery request,
        MediaWikiCargoRelationshipDefinition definition,
        IReadOnlyList<MediaWikiCargoRelationshipSourceRow> sourceRows,
        MediaWikiCargoRelationshipDatasetProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(sourceRows);
        ArgumentNullException.ThrowIfNull(provenance);
        if (request.DatasetClassification != MediaWikiCargoRelationshipDatasetClassification.Synthetic
            || provenance.Classification != MediaWikiCargoRelationshipDatasetClassification.Synthetic)
            throw new InvalidOperationException("The relationship projector accepts synthetic data only.");
        if (!string.Equals(request.Relationship, definition.LogicalName, StringComparison.Ordinal))
            throw new InvalidDataException("The relationship definition did not match the request.");
        if (!string.Equals(request.Identity.DatasetIdentity, provenance.DatasetIdentity, StringComparison.Ordinal)
            || !string.Equals(request.Identity.SourceWikiIdentity, provenance.SourceWikiIdentity, StringComparison.Ordinal))
            throw new InvalidDataException("The synthetic relationship dataset identity did not match the request.");
        if (string.IsNullOrWhiteSpace(request.ParentValue)
            || request.Projections.Count == 0
            || request.Projections.Count != request.Projections.Distinct().Count()
            || request.Projections.Any(value => !definition.AllowedProjections.Contains(value))
            || request.Offset < 0
            || request.Limit < 0
            || request.Limit > request.Budget.MaxRows
            || request.Limit > definition.MaxFanOut
            || request.Budget.MaxOutputCharacters <= 0)
            throw new InvalidDataException("The synthetic relationship query exceeded its validated contract.");
        if (sourceRows.Count > definition.MaxFanOut)
            throw new InvalidDataException("Synthetic relationship rows exceeded the authorized fan-out.");

        var rows = sourceRows
            .Where(value => string.Equals(value.ParentValue, request.ParentValue, StringComparison.Ordinal))
            .Where(value => request.ValueEquals is null || string.Equals(value.Value, request.ValueEquals, StringComparison.Ordinal))
            .OrderBy(value => value.Position)
            .ThenBy(value => value.Value ?? string.Empty, StringComparer.Ordinal)
            .Skip(request.Offset)
            .Take(request.Limit)
            .Select(value =>
            {
                var result = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var projection in request.Projections)
                {
                    var key = projection == MediaWikiCargoRelationshipProjection.Value ? "value" : "position";
                    result[key] = projection == MediaWikiCargoRelationshipProjection.Value
                        ? value.Value
                        : value.Position.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                return (IReadOnlyDictionary<string, string?>)result;
            })
            .ToArray();
        var outputCharacters = rows.Sum(row => row.Sum(cell => cell.Key.Length + (cell.Value?.Length ?? 0)));
        if (outputCharacters > request.Budget.MaxOutputCharacters)
            throw new InvalidDataException("Synthetic relationship output exceeded the configured character budget.");
        return new(request.Relationship, rows, request.Identity, provenance);
    }
}
