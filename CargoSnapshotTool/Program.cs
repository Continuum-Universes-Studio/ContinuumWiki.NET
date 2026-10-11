using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using TightWiki.Engine.MediaWiki;
using TightWiki.Repository.Helpers;

return await CargoSnapshotCli.RunAsync(args);

internal static class CargoSnapshotCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            return args[0] switch
            {
                "capture" => await CaptureAsync(args[1..]),
                "verify" => await VerifyAsync(args[1..]),
                _ => Usage()
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or MySqlException or TimeoutException)
        {
            Console.Error.WriteLine($"Cargo snapshot tool failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> CaptureAsync(string[] args)
    {
        var options = Options.Parse(args);
        var credentialPath = options.Required("config");
        var columnsPath = options.Required("columns-tsv");
        var output = options.Required("output");
        var database = options.Required("database");
        var columns = ReadColumns(columnsPath);
        var indexesHash = options.Required("indexes-sha256");
        var sourceIdentity = options.Required("source-identity");
        var wikiIdentity = options.Required("source-wiki");
        var snapshotId = options.Required("snapshot-id");
        var builder = ReadClientOptions(credentialPath);
        builder.Database = database;
        builder.Pooling = false;
        var factory = new MySqlDatabaseConnectionFactory(builder.ConnectionString);
        var tables = columns.Select(pair => new MediaWikiCargoSnapshotAcquisitionTable(
            pair.Key["cargo__".Length..], pair.Key, pair.Value, [])).ToArray();
        var request = new MediaWikiCargoSnapshotAcquisitionRequest(
            output, snapshotId, sourceIdentity, wikiIdentity, "cargo__",
            "ade3431abf2cc321e333eac35c858d25f8c97a52e0379436aa52917cda1ebc4f",
            indexesHash, tables, "AUTHENTIC_SOURCE_CURRENT_STATE", MaxRowsPerTable: 100_000,
            MaxOutputBytes: 256_000_000, MaxExecutionTime: TimeSpan.FromMinutes(5));
        await MediaWikiCargoSnapshotStore.CaptureAsync(factory, request, "cw-m3-006f4b.1");
        Console.WriteLine($"Snapshot finalized: {output}");
        return 0;
    }

    private static async Task<int> VerifyAsync(string[] args)
    {
        var options = Options.Parse(args);
        var result = await MediaWikiCargoSnapshotStore.VerifyAsync(
            options.Required("snapshot"), options.Required("source-identity"),
            "ade3431abf2cc321e333eac35c858d25f8c97a52e0379436aa52917cda1ebc4f",
            options.Required("indexes-sha256"));
        foreach (var failure in result.ValidationFailures) Console.Error.WriteLine(failure);
        Console.WriteLine(result.IsValid ? "Snapshot verification: VERIFIED" : "Snapshot verification: FAILED");
        return result.IsValid ? 0 : 1;
    }

    private static Dictionary<string, string[]> ReadColumns(string path)
    {
        var rows = File.ReadLines(path).Skip(1).Select(line => line.Split('\t'));
        var result = new Dictionary<string, List<(int Position, string Name)>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Length < 3 || !int.TryParse(row[1], NumberStyles.None, CultureInfo.InvariantCulture, out var position))
                throw new InvalidDataException("The Cargo columns TSV is malformed.");
            if (!row[0].StartsWith("cargo__", StringComparison.Ordinal) || row[0]["cargo__".Length..].Contains("__", StringComparison.Ordinal))
                continue;
            if (!IsIdentifier(row[0]) || !IsIdentifier(row[2])) throw new InvalidDataException("The Cargo schema contains an invalid identifier.");
            if (!result.TryGetValue(row[0], out var list)) result[row[0]] = list = [];
            list.Add((position, row[2]));
        }
        var expected = new[] { "cargo__Characters", "cargo__Divines", "cargo__Species", "cargo__Subvariants", "cargo__Variants" };
        if (result.Keys.OrderBy(value => value).SequenceEqual(expected.OrderBy(value => value)) == false)
            throw new InvalidDataException("The Cargo columns TSV does not contain exactly the five verified base tables.");
        return result.ToDictionary(value => value.Key, value => value.Value.OrderBy(item => item.Position).Select(item => item.Name).ToArray(), StringComparer.Ordinal);
    }

    private static MySqlConnectionStringBuilder ReadClientOptions(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The private Cargo credential file was not found.", path);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('[')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        var builder = new MySqlConnectionStringBuilder();
        if (values.TryGetValue("host", out var host)) builder.Server = host;
        if (values.TryGetValue("port", out var port) && uint.TryParse(port, out var portValue)) builder.Port = portValue;
        if (values.TryGetValue("user", out var user)) builder.UserID = user;
        if (values.TryGetValue("password", out var password)) builder.Password = password;
        return builder;
    }

    private static bool IsIdentifier(string value) => value.Length > 0 && value.All(value => char.IsLetterOrDigit(value) || value == '_');
    private static int Usage() { Console.Error.WriteLine("Usage: CargoSnapshotTool capture --config FILE --database NAME --columns-tsv FILE --indexes-sha256 HASH --output DIR --source-identity ID --source-wiki ID --snapshot-id ID | verify --snapshot DIR --source-identity ID --indexes-sha256 HASH"); return 2; }

    private sealed class Options(Dictionary<string, string> values)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) throw new InvalidDataException("Invalid command-line arguments.");
                values[args[i][2..]] = args[++i];
            }
            return new(values);
        }
        public string Required(string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : throw new InvalidDataException($"Missing required option --{key}.");
    }
}
