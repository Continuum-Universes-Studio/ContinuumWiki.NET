using System.Text.Json;
using System.Text.Json.Serialization;
using MediaWikiInventory;
using TightWiki.Repository.Helpers;

namespace MediaWikiImport;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (MediaWikiImportPreflightException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetBaseException().Message);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        var command = args[0].ToLowerInvariant();
        var options = ParseOptions(args[1..]);
        return command switch
        {
            "preflight" => await PreflightAsync(options),
            "provision" => await ProvisionAsync(options),
            "import" => await ImportAsync(options),
            "reconcile" => await ReconcileAsync(options),
            "status" => await StatusAsync(options),
            _ => UsageAndFailure()
        };
    }

    private static async Task<int> PreflightAsync(IReadOnlyDictionary<string, string> options)
    {
        var (service, plan, input, mapping, mediaManifest, mediaRoot) = await CreateContextAsync(options);
        var result = await service.PreflightAsync(plan, input, mapping, mediaManifest, mediaRoot);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions()));
        return result.IsReady ? 0 : 3;
    }

    private static async Task<int> ProvisionAsync(IReadOnlyDictionary<string, string> options)
    {
        var target = LoadTarget(options);
        var factory = new MySqlDatabaseConnectionFactory(target.ConnectionString);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        await new MediaWikiImportExecutor(factory, target).InitializeImportSchemaAsync();
        Console.WriteLine($"Provisioned MariaDB target '{target.DatabaseName}' for explicit import use.");
        return 0;
    }

    private static async Task<int> ImportAsync(IReadOnlyDictionary<string, string> options)
    {
        var (service, plan, input, mapping, mediaManifest, mediaRoot) = await CreateContextAsync(options);
        var result = await service.ImportAsync(plan, input, mapping, mediaManifest, mediaRoot);
        var output = options.GetValueOrDefault("reconciliation-output");
        var json = JsonSerializer.Serialize(result.Reconciliation, JsonOptions());
        if (string.IsNullOrWhiteSpace(output)) Console.WriteLine(json);
        else File.WriteAllText(output, json + Environment.NewLine);
        Console.WriteLine($"Import batch {result.ImportBatchId} completed with {result.Reconciliation.Verdict}.");
        return 0;
    }

    private static async Task<int> ReconcileAsync(IReadOnlyDictionary<string, string> options)
    {
        var (service, plan, _, _, _, _) = await CreateContextAsync(options);
        if (!options.TryGetValue("batch", out var batchText) || !long.TryParse(batchText, out var batchId))
            throw new ArgumentException("reconcile requires --batch <id>.");
        var report = await service.ReconcileAsync(plan, batchId);
        var output = options.GetValueOrDefault("reconciliation-output");
        var json = JsonSerializer.Serialize(report, JsonOptions());
        if (string.IsNullOrWhiteSpace(output)) Console.WriteLine(json);
        else File.WriteAllText(output, json + Environment.NewLine);
        return report.IsSuccessful ? 0 : 3;
    }

    private static async Task<int> StatusAsync(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("batch", out var batchText) || !long.TryParse(batchText, out var batchId))
            throw new ArgumentException("status requires --batch <id>.");
        var target = LoadTarget(options);
        var service = new MediaWikiImportExecutor(new MySqlDatabaseConnectionFactory(target.ConnectionString), target);
        var status = await service.GetStatusAsync(batchId);
        Console.WriteLine(JsonSerializer.Serialize(status, JsonOptions()));
        return 0;
    }

    private static async Task<(MediaWikiImportExecutor Service, MediaWikiImportPlan Plan, string Input, NamespaceMappingConfiguration Mapping, string? MediaManifest, string? MediaRoot)> CreateContextAsync(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("input", out var input) || !options.TryGetValue("plan", out var planPath))
            throw new ArgumentException("The command requires --input <export.xml> and --plan <plan.json>.");
        var plan = JsonSerializer.Deserialize<MediaWikiImportPlan>(File.ReadAllText(planPath), JsonOptions())
            ?? throw new InvalidDataException("The import plan is empty.");
        var mapping = options.TryGetValue("mapping", out var mappingPath)
            ? NamespaceMappingConfiguration.Load(mappingPath)
            : plan.MappingConfiguration;
        var target = LoadTarget(options);
        var service = new MediaWikiImportExecutor(new MySqlDatabaseConnectionFactory(target.ConnectionString), target);
        return (service, plan, input, mapping, options.GetValueOrDefault("media-manifest"), options.GetValueOrDefault("media-root"));
    }

    private static MediaWikiImportTargetConfiguration LoadTarget(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("target-config", out var path))
            throw new ArgumentException("The command requires --target-config <disposable-target.json>; passwords must not be passed as command-line arguments.");
        return MediaWikiImportTargetConfiguration.Load(path);
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"Expected an option followed by a value: {args[index]}");
            result[args[index][2..]] = args[++index];
        }
        return result;
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private static int UsageAndFailure()
    {
        Usage();
        return 2;
    }

    private static void Usage() => Console.Error.WriteLine(
        "Usage: MediaWikiImport provision|preflight|import|reconcile|status --input <export.xml> --plan <plan.json> --target-config <target.json> [--mapping <mapping.json>] [--media-manifest <manifest.json> --media-root <directory>] [--batch <id>] [--reconciliation-output <report.json>]");
}
