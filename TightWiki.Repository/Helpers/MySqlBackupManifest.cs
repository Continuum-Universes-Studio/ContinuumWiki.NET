using System.Security.Cryptography;
using System.Text.Json;

namespace TightWiki.Repository.Helpers;

/// <summary>Non-sensitive metadata accompanying an operator-created MySQL dump.</summary>
public sealed record MySqlBackupManifest(
    DateTimeOffset CreatedUtc,
    string Provider,
    string ServerVersion,
    string ApplicationVersion,
    string Format,
    string DatabaseIdentifier,
    IReadOnlyList<string> IncludedStorageDomains,
    IReadOnlyList<string> ExcludedPersistentAssets,
    long ArtifactSizeBytes,
    string Sha256)
{
    public static async Task<MySqlBackupManifest> CreateAsync(
        string artifactPath, string serverVersion, string applicationVersion,
        string databaseIdentifier, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(artifactPath);
        if (!info.Exists) throw new FileNotFoundException("The backup artifact does not exist.", artifactPath);
        await using var stream = info.OpenRead();
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new(DateTimeOffset.UtcNow, "mysql", serverVersion, applicationVersion,
            "mysqldump-sql", databaseIdentifier,
            ["schema", "identity", "pages", "revisions", "attachments", "comments", "configuration", "translations", "themes", "menus", "operations"],
            ["external filesystem assets (none used by the MySQL persistence adapter)"],
            info.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    public async Task WriteAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }

    public static async Task<MySqlBackupManifest> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MySqlBackupManifest>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("The backup manifest is empty.");
    }

    public async Task VerifyArtifactAsync(string artifactPath, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(artifactPath);
        if (!info.Exists || info.Length != ArtifactSizeBytes) throw new InvalidDataException("Backup artifact size does not match its manifest.");
        await using var stream = info.OpenRead();
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(Sha256)))
            throw new InvalidDataException("Backup checksum does not match its manifest.");
    }
}
