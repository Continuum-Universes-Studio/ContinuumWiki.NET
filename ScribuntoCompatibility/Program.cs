using System.Text.Json;
using TightWiki.Engine.MediaWiki;

var arguments = ParseArguments(args);
var corpusPath = Required(arguments, "corpus");
var workerPath = Required(arguments, "worker");
var reportPath = Required(arguments, "report");
var gapPath = arguments.GetValueOrDefault("gap-inventory");

var corpus = MediaWikiScribuntoCompatibilityCorpus.Load(corpusPath);
var report = await new MediaWikiScribuntoCompatibilityRunner().RunAsync(corpus, workerPath);
var options = MediaWikiScribuntoCompatibilityCorpus.JsonOptions(indented: true);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, options));
if (!string.IsNullOrWhiteSpace(gapPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(gapPath))!);
    await File.WriteAllTextAsync(gapPath, JsonSerializer.Serialize(report.ApiGapInventory, options));
}

Console.WriteLine($"Corpus {report.CorpusVersion}: total={report.Summary.TotalCases}, executed={report.Summary.ExecutedCases}, validation-only={report.Summary.ValidationOnlyCases}, passed={report.Summary.PassedCases}, failed={report.Summary.FailedCases}, skipped={report.Summary.SkippedCases}, unsupported={report.Summary.UnsupportedCases}, partial={report.Summary.PartialCases}");
return report.Summary.FailedCases == 0 ? 0 : 1;

static Dictionary<string, string> ParseArguments(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            throw new ArgumentException("Arguments must use --name value form.");
        values[args[index][2..]] = args[++index];
    }
    return values;
}

static string Required(IReadOnlyDictionary<string, string> arguments, string name)
    => arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"--{name} is required.");
