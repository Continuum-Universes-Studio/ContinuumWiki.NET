using System.Data;
using System.Data.Common;
using TightWiki.Library.Database;

namespace TightWiki.Engine.MediaWiki;

/// <summary>
/// Executes the schema-verified scalar subset of Cargo queries on the host.
/// Helper-backed fields are deliberately rejected until a typed join contract
/// is supplied; Lua never receives a database connection.
/// </summary>
public sealed class SchemaVerifiedMediaWikiCargoQueryProvider : IMediaWikiCargoQueryProvider
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ScalarFields =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Variants"] = Fields("Name", "Species", "Universe", "Origin", "Background", "Height", "Weight", "Lifespan", "Gestation", "CaloricRequirements", "CoreTemperature"),
            ["Subvariants"] = Fields("Name", "Species", "GeneticAncestor", "Universe", "Origin", "Background"),
            ["Divines"] = Fields("Name", "Father", "Mother", "OED", "IPA", "Image", "Epithet", "HomeUniverse", "Gender", "Role", "Domain", "MortalName", "MortalPage"),
            ["Characters"] = Fields("Name", "Father", "Mother", "Species", "Race", "Universe", "Status", "Gender", "Height", "Weight", "Twin", "DivineName"),
            ["Species"] = Fields("Name", "OED", "IPA", "Audio", "Taxonomy", "SpeciesFamily", "BirthType", "ReproductionType", "AverageHeight", "AverageLifespan", "Homeworld", "Universe", "Creator", "Status", "Stance")
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> HelperFields =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Variants"] = Fields("GeneticAncestor", "EyeColors", "HairColors", "SkinColors", "FeatherColors", "ScaleColors", "FurColors"),
            ["Divines"] = Fields("Spouses", "Consorts", "Companions", "NotableDivineSiblings", "NotableDivineOffspring", "Servants", "UniversesCreated"),
            ["Characters"] = Fields("Affiliations", "Partners", "Lovers", "Hair", "Eyes", "Feathers", "Scales", "Fur", "NotableRelatives", "StepSiblings", "SocialRelatives", "FullSiblings", "HalfSiblings", "Children", "DivineSpouse")
        };

    private readonly IDatabaseConnectionFactory _connectionFactory;
    private readonly string _datasetIdentity;
    private readonly string _sourceWikiIdentity;
    private readonly string _tablePrefix;

    public SchemaVerifiedMediaWikiCargoQueryProvider(
        IDatabaseConnectionFactory connectionFactory,
        string datasetIdentity,
        string sourceWikiIdentity,
        string tablePrefix = "cargo__")
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _datasetIdentity = RequireIdentity(datasetIdentity, nameof(datasetIdentity));
        _sourceWikiIdentity = RequireIdentity(sourceWikiIdentity, nameof(sourceWikiIdentity));
        if (tablePrefix.Any(value => !char.IsLetterOrDigit(value) && value != '_'))
            throw new ArgumentException("The Cargo table prefix contains an invalid identifier.", nameof(tablePrefix));
        _tablePrefix = tablePrefix;
    }

    public async Task<MediaWikiCargoProviderResult> QueryAsync(MediaWikiCargoQueryRequest request, CancellationToken cancellationToken = default)
    {
        var validation = MediaWikiCargoQueryValidator.Validate(request, QueryFieldAllowlist);
        if (!string.Equals(request.Identity.SourceWikiIdentity, _sourceWikiIdentity, StringComparison.Ordinal))
            validation = validation.Append(new(MediaWikiCargoProviderFailure.HistoricalProvenanceUnavailable, "The request source wiki does not match the authorized dataset.")).ToArray();
        if (!string.Equals(request.Identity.DatasetIdentity, _datasetIdentity, StringComparison.Ordinal))
            validation = validation.Append(new(MediaWikiCargoProviderFailure.HistoricalProvenanceUnavailable, "The request dataset does not match the authorized dataset.")).ToArray();
        if (request.Table is not null && HelperFields.TryGetValue(request.Table.Name, out var helpers)
            && request.Fields.Concat(request.Predicates.Select(value => value.Field)).Concat(request.Sort.Select(value => value.Field)).Any(value => helpers.Contains(value.Name)))
            validation = validation.Append(new(MediaWikiCargoProviderFailure.UnsupportedQuery, "The requested field is Cargo helper-table backed and has no approved typed join contract.")).ToArray();
        if (validation.Count > 0) return Failure(validation[0]);

        var started = DateTime.UtcNow;
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var command = session.Connection.CreateCommand();
        command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(request.Budget.EffectiveExecutionTime.TotalSeconds));
        var table = Identifier(_tablePrefix + request.Table!.Name);
        var fields = request.Fields.Select(value => Identifier(value.Name)).ToArray();
        var sql = $"SELECT {string.Join(", ", fields)} FROM `{table}`";
        var where = new List<string>();
        var parameterIndex = 0;
        foreach (var predicate in request.Predicates)
        {
            var field = $"`{Identifier(predicate.Field.Name)}`";
            if (predicate.Operator is MediaWikiCargoPredicateOperator.IsNull or MediaWikiCargoPredicateOperator.IsNotNull)
                where.Add($"{field} IS {(predicate.Operator == MediaWikiCargoPredicateOperator.IsNull ? "NULL" : "NOT NULL")}");
            else
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"@cargo{parameterIndex++}";
                parameter.Value = predicate.Operator switch
                {
                    MediaWikiCargoPredicateOperator.Contains => $"%{predicate.Value ?? string.Empty}%",
                    MediaWikiCargoPredicateOperator.StartsWith => $"{predicate.Value ?? string.Empty}%",
                    _ => predicate.Value ?? string.Empty
                };
                command.Parameters.Add(parameter);
                var op = predicate.Operator == MediaWikiCargoPredicateOperator.NotEquals ? "<>" : "=";
                if (predicate.Operator is MediaWikiCargoPredicateOperator.Contains or MediaWikiCargoPredicateOperator.StartsWith) op = "LIKE";
                where.Add($"{field} {op} {parameter.ParameterName}");
            }
        }
        if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
        if (request.Sort.Count > 0) sql += " ORDER BY " + string.Join(", ", request.Sort.Select(value => $"`{Identifier(value.Field.Name)}` {(value.Descending ? "DESC" : "ASC")}"));
        sql += " LIMIT @cargoLimit OFFSET @cargoOffset";
        AddParameter(command, "@cargoLimit", request.Limit);
        AddParameter(command, "@cargoOffset", request.Offset);
        command.CommandText = sql;

        try
        {
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            var rows = new List<IReadOnlyList<MediaWikiCargoResultColumn>>();
            var outputCharacters = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new List<MediaWikiCargoResultColumn>(fields.Length);
                for (var i = 0; i < fields.Length; i++)
                {
                    var value = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    outputCharacters += fields[i].Length + value.Length;
                    if (outputCharacters > request.Budget.MaxOutputCharacters)
                        return Failure(new(MediaWikiCargoProviderFailure.BudgetExceeded, "Cargo output exceeded the configured character budget."));
                    row.Add(new(request.Fields[i].Name, value));
                }
                rows.Add(row);
                if (rows.Count > request.Budget.MaxRows) return Failure(new(MediaWikiCargoProviderFailure.BudgetExceeded, "Cargo rows exceeded the configured limit."));
            }
            if (DateTime.UtcNow - started > request.Budget.EffectiveExecutionTime)
                return Failure(new(MediaWikiCargoProviderFailure.BudgetExceeded, "Cargo query exceeded the configured execution budget."));
            return new(new(rows, request.Identity, $"current:{_datasetIdentity}"), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(new(MediaWikiCargoProviderFailure.Cancelled, "Cargo query was cancelled."));
        }
    }

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> VerifiedScalarAllowlist => ScalarFields;

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> QueryFieldAllowlist
    {
        get
        {
            var result = ScalarFields.ToDictionary(value => value.Key, value => (IReadOnlySet<string>)new HashSet<string>(value.Value, StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var pair in HelperFields)
            {
                if (!result.TryGetValue(pair.Key, out var allowedFields))
                    result[pair.Key] = new HashSet<string>(pair.Value, StringComparer.Ordinal);
                else
                    foreach (var fieldName in pair.Value) ((HashSet<string>)allowedFields).Add(fieldName);
            }
            return result;
        }
    }

    private static IReadOnlySet<string> Fields(params string[] fields) => new HashSet<string>(fields, StringComparer.Ordinal);
    private static string RequireIdentity(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A dataset identity is required.", name) : value;
    private static string Identifier(string value) => value;
    private static void AddParameter(DbCommand command, string name, object value) { var p = command.CreateParameter(); p.ParameterName = name; p.Value = value; command.Parameters.Add(p); }
    private static MediaWikiCargoProviderResult Failure(MediaWikiCargoProviderDiagnostic diagnostic) => new(null, diagnostic);
}
