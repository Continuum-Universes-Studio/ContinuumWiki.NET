using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace MediaWikiInventory;

public static class PlannerCli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }
        if (args[0].Equals("plan", StringComparison.OrdinalIgnoreCase)) return Plan(args[1..]);
        if (args[0].Equals("validate", StringComparison.OrdinalIgnoreCase)) return Validate(args[1..]);
        if (args[0].Equals("preview", StringComparison.OrdinalIgnoreCase) && args.Length == 2)
            return WriteJson(MediaWikiImportPreview.Create(args[1]), null);
        if (args.Length == 1)
            return WriteJson(InventoryReport.Read(args[0]), null);
        Usage();
        return 2;
    }

    private static int Plan(string[] args)
    {
        var options = Options(args);
        if (!options.TryGetValue("input", out var input) || !options.TryGetValue("output", out var output))
        {
            Usage();
            return 2;
        }
        var mapping = NamespaceMappingConfiguration.Load(options.GetValueOrDefault("mapping"));
        var plan = MediaWikiImportPlanner.Create(input, mapping, options.GetValueOrDefault("media-manifest"), options.GetValueOrDefault("media-root"));
        var json = JsonSerializer.Serialize(plan, JsonOptions());
        File.WriteAllText(output, json + Environment.NewLine);
        Console.WriteLine($"Plan written to {output}; pages={plan.PlannedPageCount}, revisions={plan.PlannedRevisionCount}, disposition={plan.OverallDisposition}.");
        return plan.OverallDisposition == ImportDisposition.Blocked ? 3 : 0;
    }

    private static int Validate(string[] args)
    {
        var options = Options(args);
        if (!options.TryGetValue("plan", out var planPath) || !options.TryGetValue("input", out var input))
        {
            Usage();
            return 2;
        }
        var mapping = NamespaceMappingConfiguration.Load(options.GetValueOrDefault("mapping"));
        var plan = JsonSerializer.Deserialize<MediaWikiImportPlan>(File.ReadAllText(planPath), JsonOptions())
            ?? throw new InvalidDataException("Plan JSON is empty.");
        var findings = ImportPlanValidator.Validate(plan, input, mapping, options.GetValueOrDefault("media-manifest"), options.GetValueOrDefault("media-root"));
        Console.WriteLine(JsonSerializer.Serialize(findings, JsonOptions()));
        return findings.Any(f => f.Severity == PlanFindingSeverity.Blocking) ? 3 : 0;
    }

    private static int WriteJson<T>(T value, string? output)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions());
        if (output is null) Console.WriteLine(json);
        else File.WriteAllText(output, json + Environment.NewLine);
        return 0;
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException($"Expected an option followed by a value: {args[i]}");
            result[args[i][2..]] = args[++i];
        }
        return result;
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = null,
        Converters = { new JsonStringEnumConverter() }
    };

    private static void Usage() => Console.Error.WriteLine(
        "Usage: MediaWikiInventory <export.xml> | preview <export.xml> | plan --input <export.xml> --output <plan.json> [--mapping <mapping.json>] [--media-manifest <manifest.json> --media-root <directory>] | validate --plan <plan.json> --input <export.xml> [--mapping <mapping.json>] [--media-manifest <manifest.json> --media-root <directory>]");
}
