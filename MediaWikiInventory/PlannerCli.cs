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
        if (args[0].Equals("modules", StringComparison.OrdinalIgnoreCase))
        {
            var options = Options(args[1..]);
            if (!options.TryGetValue("input", out var input)) { Usage(); return 2; }
            var report = MediaWikiModuleInventory.Read(input);
            if (options.TryGetValue("output", out var output))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, JsonSerializer.Serialize(report, MediaWikiModuleInventory.JsonOptions()) + Environment.NewLine);
            }
            else Console.WriteLine(JsonSerializer.Serialize(report, MediaWikiModuleInventory.JsonOptions()));
            return 0;
        }
        if (args[0].Equals("invocations", StringComparison.OrdinalIgnoreCase))
        {
            var options = Options(args[1..]);
            if (!options.TryGetValue("input", out var input)) { Usage(); return 2; }
            var report = MediaWikiInvocationInventory.Read(input);
            var json = MediaWikiInvocationInventory.Serialize(report) + Environment.NewLine;
            if (options.TryGetValue("output", out var output))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, json);
            }
            else Console.Write(json);
            return 0;
        }
        if (args[0].Equals("compatibility", StringComparison.OrdinalIgnoreCase))
        {
            var options = Options(args[1..]);
            if (!options.TryGetValue("input", out var input)) { Usage(); return 2; }
            var storageStatus = "NOT_RUN";
            if (options.TryGetValue("import-report", out var importReport) && File.Exists(importReport))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(importReport));
                storageStatus = document.RootElement.TryGetProperty("Verdict", out var verdict) ? verdict.GetString() ?? "UNKNOWN" : "UNKNOWN";
            }
            var report = MediaWikiModuleCompatibilityReportBuilder.Create(MediaWikiModuleInventory.Read(input), storageStatus);
            var json = MediaWikiModuleCompatibilityReportBuilder.Serialize(report) + Environment.NewLine;
            if (options.TryGetValue("output", out var output))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, json);
            }
            else Console.Write(json);
            return 0;
        }
        if (args[0].Equals("select-modules", StringComparison.OrdinalIgnoreCase))
        {
            var options = Options(args[1..]);
            if (!options.TryGetValue("input", out var input) || !options.TryGetValue("output", out var output) || !options.TryGetValue("titles", out var titles)) { Usage(); return 2; }
            var selected = MediaWikiModuleSubset.Write(input, output, titles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase));
            Console.WriteLine($"Derived offline module export written to {output}; modules={selected.Count}.");
            return 0;
        }
        if (args[0].Equals("select-compatibility", StringComparison.OrdinalIgnoreCase))
        {
            var options = Options(args[1..]);
            if (!options.TryGetValue("input", out var input) || !options.TryGetValue("corpus", out var corpus) || !options.TryGetValue("output", out var output)) { Usage(); return 2; }
            var selected = MediaWikiCompatibilitySubset.Write(input, corpus, output);
            Console.WriteLine($"Derived compatibility export written to {output}; pages={selected.Pages}, revisions={selected.Revisions}.");
            return 0;
        }
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
        "Usage: MediaWikiInventory <export.xml> | modules --input <export.xml> [--output <report.json>] | invocations --input <export.xml> [--output <report.json>] | compatibility --input <export.xml> [--output <report.json>] [--import-report <reconciliation.json>] | select-modules --input <export.xml> --output <subset.xml> --titles <comma-separated-titles> | select-compatibility --input <export.xml> --corpus <corpus.json> --output <subset.xml> | preview <export.xml> | plan --input <export.xml> --output <plan.json> [--mapping <mapping.json>] [--media-manifest <manifest.json> --media-root <directory>] | validate --plan <plan.json> --input <export.xml> [--mapping <mapping.json>] [--media-manifest <manifest.json> --media-root <directory>]");
}
