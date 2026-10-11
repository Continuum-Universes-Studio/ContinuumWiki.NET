using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaWikiInventory;

public sealed record MediaWikiMediaManifest(
    string ManifestVersion,
    string SourceWikiIdentity,
    IReadOnlyList<MediaWikiMediaEntry> Entries);

public sealed record MediaWikiMediaEntry(
    string OriginalFileName,
    string SourceFilePageId,
    string? MimeType,
    long Size,
    string Sha256,
    string? Sha1,
    string RelativeBinaryPath,
    DateTimeOffset? OriginalTimestamp,
    MediaWikiContributor Uploader,
    string? ArchiveMetadata,
    IReadOnlyList<MediaWikiMediaRevision> Revisions);

public sealed record MediaWikiMediaRevision(
    string SourceMediaRevisionId,
    int RevisionOrdinal,
    DateTimeOffset? Timestamp,
    MediaWikiContributor Uploader,
    string? MimeType,
    long Size,
    string Sha256,
    string? Sha1,
    string RelativeBinaryPath,
    string? ArchiveMetadata);

public sealed record PlannedMedia(
    string OriginalFileName,
    string SourceFilePageId,
    string? TargetFilePageNavigation,
    string TargetFileNavigation,
    string? MimeType,
    long Size,
    string Sha256,
    string? Sha1,
    string RelativeBinaryPath,
    DateTimeOffset? OriginalTimestamp,
    MediaWikiContributor Uploader,
    string? ArchiveMetadata,
    IReadOnlyList<MediaWikiMediaRevision> Revisions,
    IReadOnlyList<PlanFinding> Findings);

public sealed record MediaWikiMediaPlan(
    string ManifestSha256,
    long ManifestByteLength,
    string SourceWikiIdentity,
    long EstimatedBinaryBytes,
    IReadOnlyList<PlannedMedia> Entries,
    IReadOnlyList<PlanFinding> Findings)
{
    public int PlannedFileCount => Entries.Count;
    public int PlannedRevisionCount => Entries.Sum(entry => entry.Revisions.Count);
}

public sealed record MediaWikiMediaValidationResult(
    MediaWikiMediaManifest Manifest,
    string ManifestSha256,
    long ManifestByteLength,
    IReadOnlyList<PlanFinding> Findings);

public static class MediaWikiMediaManifestReader
{
    public static MediaWikiMediaValidationResult ReadAndValidate(string manifestPath)
    {
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("The MediaWiki media manifest was not found.", manifestPath);
        var bytes = File.ReadAllBytes(manifestPath);
        var manifest = JsonSerializer.Deserialize<MediaWikiMediaManifest>(bytes, JsonOptions())
            ?? throw new InvalidDataException("The MediaWiki media manifest is empty.");
        var findings = Validate(manifest);
        return new(manifest, Hash(bytes), bytes.LongLength, findings);
    }

    public static IReadOnlyList<PlanFinding> Validate(MediaWikiMediaManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var findings = new List<PlanFinding>();
        if (string.IsNullOrWhiteSpace(manifest.ManifestVersion)) findings.Add(new(PlanFindingSeverity.Blocking, "MediaManifestVersionMissing", "The media manifest version is required."));
        if (string.IsNullOrWhiteSpace(manifest.SourceWikiIdentity)) findings.Add(new(PlanFindingSeverity.Blocking, "MediaSourceWikiIdentityMissing", "The media manifest source wiki identity is required."));
        var names = new Dictionary<string, MediaWikiMediaEntry>(StringComparer.OrdinalIgnoreCase);
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.OriginalFileName)) findings.Add(new(PlanFindingSeverity.Blocking, "MediaFileNameMissing", "A media manifest entry has no original filename."));
            var normalized = NormalizeFileName(entry.OriginalFileName);
            if (!names.TryAdd(normalized, entry)) findings.Add(new(PlanFindingSeverity.Blocking, "DuplicateMediaFileName", $"Media filename '{entry.OriginalFileName}' collides under MediaWiki filename matching rules."));
            if (string.IsNullOrWhiteSpace(entry.SourceFilePageId)) findings.Add(new(PlanFindingSeverity.Blocking, "MediaFilePageIdMissing", $"Media entry '{entry.OriginalFileName}' has no source file-page id."));
            else if (!pageIds.Add(entry.SourceFilePageId)) findings.Add(new(PlanFindingSeverity.Blocking, "DuplicateMediaFilePageId", $"Source file-page id '{entry.SourceFilePageId}' is used more than once."));
            ValidateBinaryMetadata(entry.OriginalFileName, entry.Size, entry.Sha256, entry.RelativeBinaryPath, findings);
            var revisionIds = new HashSet<string>(StringComparer.Ordinal);
            var revisions = entry.Revisions ?? [];
            for (var index = 0; index < revisions.Count; index++)
            {
                var revision = revisions[index];
                if (revision.RevisionOrdinal != index + 1) findings.Add(new(PlanFindingSeverity.Blocking, "MediaRevisionOrderMismatch", $"Media file '{entry.OriginalFileName}' has non-contiguous revision ordinals."));
                if (string.IsNullOrWhiteSpace(revision.SourceMediaRevisionId) || !revisionIds.Add(revision.SourceMediaRevisionId)) findings.Add(new(PlanFindingSeverity.Blocking, "InvalidMediaRevisionIdentity", $"Media file '{entry.OriginalFileName}' has a missing or duplicate source revision identity."));
                ValidateBinaryMetadata(entry.OriginalFileName, revision.Size, revision.Sha256, revision.RelativeBinaryPath, findings);
            }
            if (revisions.Count == 0)
                findings.Add(new(PlanFindingSeverity.Blocking, "MediaRevisionsMissing", $"Media file '{entry.OriginalFileName}' has no source media revisions."));
            else if (entry.Size != revisions[^1].Size || !string.Equals(entry.Sha256, revisions[^1].Sha256, StringComparison.OrdinalIgnoreCase))
                findings.Add(new(PlanFindingSeverity.Blocking, "MediaCurrentRevisionMismatch", $"Media file '{entry.OriginalFileName}' original size/checksum does not match its latest declared revision."));
        }
        return findings;
    }

    public static string NormalizeFileName(string value)
    {
        var name = value.Trim();
        if (name.StartsWith("File:", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Image:", StringComparison.OrdinalIgnoreCase)) name = name[(name.IndexOf(':') + 1)..];
        return name.Replace('_', ' ').Normalize(NormalizationForm.FormC).Trim();
    }

    private static void ValidateBinaryMetadata(string fileName, long size, string sha256, string relativePath, List<PlanFinding> findings)
    {
        if (size < 0) findings.Add(new(PlanFindingSeverity.Blocking, "NegativeMediaSize", $"Media file '{fileName}' declares a negative size."));
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) findings.Add(new(PlanFindingSeverity.Blocking, "InvalidMediaSha256", $"Media file '{fileName}' does not have a valid SHA-256 checksum."));
        var normalized = string.IsNullOrWhiteSpace(relativePath) ? string.Empty : relativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\0') || normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal) || normalized.Contains("/../", StringComparison.Ordinal)) findings.Add(new(PlanFindingSeverity.Blocking, "UnsafeMediaPath", $"Media file '{fileName}' does not have a safe relative binary path."));
    }

    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public static class MediaWikiMediaPlanner
{
    public static MediaWikiMediaPlan Create(string manifestPath, string offlineRoot, IReadOnlyList<PlannedPage> pages)
    {
        var validated = MediaWikiMediaManifestReader.ReadAndValidate(manifestPath);
        var findings = validated.Findings.ToList();
        var pageById = pages.ToDictionary(page => page.SourcePageId, StringComparer.Ordinal);
        var entries = new List<PlannedMedia>();
        MediaWikiMediaBinaryResolver? resolver = null;
        try { resolver = new MediaWikiMediaBinaryResolver(offlineRoot); }
        catch (Exception exception) { findings.Add(new(PlanFindingSeverity.Blocking, "MediaRootInvalid", exception.Message)); }

        foreach (var entry in validated.Manifest.Entries.OrderBy(entry => MediaWikiMediaManifestReader.NormalizeFileName(entry.OriginalFileName), StringComparer.OrdinalIgnoreCase))
        {
            var entryFindings = new List<PlanFinding>();
            if (!pageById.TryGetValue(entry.SourceFilePageId, out var filePage))
                entryFindings.Add(new(PlanFindingSeverity.Blocking, "MissingMediaFilePage", $"Media file-page '{entry.SourceFilePageId}' for '{entry.OriginalFileName}' is not present in the XML export.", entry.SourceFilePageId));
            if (string.IsNullOrWhiteSpace(entry.MimeType) || !SupportedMimeTypes.Contains(entry.MimeType, StringComparer.OrdinalIgnoreCase))
                entryFindings.Add(new(PlanFindingSeverity.Warning, "UnsupportedMediaMimeType", $"Media file '{entry.OriginalFileName}' declares unsupported MIME type '{entry.MimeType ?? ""}'.", entry.SourceFilePageId));
            foreach (var revision in entry.Revisions)
            {
                if (resolver is not null)
                {
                    try { resolver.ReadVerifiedAsync(revision.RelativeBinaryPath, revision.Size, revision.Sha256, declaredMimeType: revision.MimeType ?? entry.MimeType).GetAwaiter().GetResult(); }
                    catch (Exception exception) { entryFindings.Add(new(PlanFindingSeverity.Blocking, "MediaBinaryInvalid", $"Media file '{entry.OriginalFileName}' revision '{revision.SourceMediaRevisionId}' failed offline binary validation: {exception.Message}", entry.SourceFilePageId, revision.SourceMediaRevisionId)); }
                }
            }
            if (filePage is not null)
            {
                var expected = MediaWikiMediaManifestReader.NormalizeFileName(entry.OriginalFileName);
                var title = MediaWikiMediaManifestReader.NormalizeFileName(filePage.OriginalTitle);
                if (!string.Equals(expected, title, StringComparison.OrdinalIgnoreCase))
                    entryFindings.Add(new(PlanFindingSeverity.Warning, "MediaFilePageNameMismatch", $"Media manifest filename '{entry.OriginalFileName}' does not match file-page title '{filePage.OriginalTitle}'.", entry.SourceFilePageId));
            }
            entries.Add(new(entry.OriginalFileName, entry.SourceFilePageId, filePage?.ProposedTargetNavigation,
                CleanFileNavigation(entry.OriginalFileName), entry.MimeType, entry.Size, entry.Sha256, entry.Sha1,
                entry.RelativeBinaryPath, entry.OriginalTimestamp, entry.Uploader, entry.ArchiveMetadata, entry.Revisions, entryFindings));
        }

        var knownFiles = entries.ToDictionary(entry => MediaWikiMediaManifestReader.NormalizeFileName(entry.OriginalFileName), StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
            foreach (var revision in page.Revisions)
                foreach (var fileReference in revision.Dependencies.Files)
                {
                    var normalized = MediaWikiMediaManifestReader.NormalizeFileName(fileReference);
                    if (!knownFiles.ContainsKey(normalized))
                        findings.Add(new(PlanFindingSeverity.Blocking, "UnresolvedMediaReference", $"File reference '{fileReference}' has no manifest entry.", page.SourcePageId, revision.SourceRevisionId));
                }

        foreach (var entry in entries) findings.AddRange(entry.Findings);
        return new(validated.ManifestSha256, validated.ManifestByteLength, validated.Manifest.SourceWikiIdentity,
            entries.Sum(entry => entry.Revisions.Sum(revision => revision.Size)), entries,
            findings.OrderBy(finding => finding.Severity).ThenBy(finding => finding.Code, StringComparer.Ordinal).ThenBy(finding => finding.SourcePageId, StringComparer.Ordinal).ToArray());
    }

    private static readonly string[] SupportedMimeTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf"];

    private static string CleanFileNavigation(string fileName)
    {
        var value = MediaWikiMediaManifestReader.NormalizeFileName(fileName).Replace(' ', '_').Replace('.', '_');
        var chars = value.Where(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '/').ToArray();
        return new string(chars).ToLowerInvariant();
    }
}

public sealed class MediaWikiMediaBinaryResolver
{
    public const long DefaultMaximumBinaryBytes = 512L * 1024 * 1024;
    private readonly string _root;
    private readonly long _maximumBinaryBytes;

    public MediaWikiMediaBinaryResolver(string root, long maximumBinaryBytes = DefaultMaximumBinaryBytes)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("An explicit offline media root is required.", nameof(root));
        _root = Path.GetFullPath(root);
        _maximumBinaryBytes = maximumBinaryBytes > 0 ? maximumBinaryBytes : throw new ArgumentOutOfRangeException(nameof(maximumBinaryBytes));
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException($"The offline media root '{_root}' does not exist.");
        RejectReparsePoint(_root, "The offline media root may not be a symbolic link or reparse point.");
    }

    public async Task<byte[]> ReadVerifiedAsync(string relativePath, long declaredSize, string declaredSha256, CancellationToken cancellationToken = default, string? declaredMimeType = null)
    {
        var path = ResolvePath(relativePath);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The declared offline media binary is missing.", path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Offline media binaries may not be symbolic links or reparse points.");
        if (info.Length != declaredSize) throw new InvalidDataException($"Offline media binary '{relativePath}' size does not match its manifest.");
        if (declaredSize < 0 || declaredSize > _maximumBinaryBytes) throw new InvalidDataException($"Offline media binary '{relativePath}' exceeds the configured bounded read limit.");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var output = new MemoryStream(checked((int)Math.Min(declaredSize, int.MaxValue)));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > declaredSize || total > _maximumBinaryBytes) throw new InvalidDataException("Offline media binary exceeded its declared or configured size limit.");
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        if (total != declaredSize) throw new InvalidDataException("Offline media binary ended before its declared size.");
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, declaredSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Offline media binary '{relativePath}' checksum does not match its manifest.");
        var bytes = output.ToArray();
        ValidateContentSignature(bytes, declaredMimeType, relativePath);
        return bytes;
    }

    public string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) throw new InvalidDataException("Offline media paths must be non-empty and relative.");
        var combined = Path.GetFullPath(Path.Combine(_root, relativePath));
        var relative = Path.GetRelativePath(_root, combined);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)) throw new InvalidDataException("Offline media path traversal is not allowed.");
        var current = _root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current)) RejectReparsePoint(current, "Offline media directory traversal through symbolic links is not allowed.");
        }
        return combined;
    }

    private static void RejectReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(message);
    }

    private static void ValidateContentSignature(byte[] bytes, string? mimeType, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return;
        var valid = mimeType.ToLowerInvariant() switch
        {
            "image/png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8,
            "image/gif" => bytes.Length >= 6 && (Encoding.ASCII.GetString(bytes, 0, 6) is "GIF87a" or "GIF89a"),
            "image/webp" => bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",
            "application/pdf" => bytes.Length >= 5 && Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-",
            _ => true
        };
        if (!valid) throw new InvalidDataException($"Offline media binary '{relativePath}' does not match its declared MIME type '{mimeType}'.");
    }
}
