using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaWikiInventory;

namespace TightWiki.Tests.Unit;

public sealed class MediaWikiMediaTests
{
    [Fact]
    public void ManifestPlannerValidatesUnicodeFilesAndHistoricalBinaries()
    {
        using var fixture = new MediaFixture();
        var plan = MediaWikiImportPlanner.Create(fixture.XmlPath, NamespaceMappingConfiguration.Defaults, fixture.ManifestPath, fixture.RootPath);

        Assert.NotNull(plan.Media);
        Assert.Equal(3, plan.Media!.PlannedFileCount);
        Assert.Equal(4, plan.Media.PlannedRevisionCount);
        Assert.Equal(44, plan.Media.EstimatedBinaryBytes);
        Assert.DoesNotContain(plan.Media.Findings, finding => finding.Severity == PlanFindingSeverity.Blocking);
        Assert.Contains(plan.Media.Entries, entry => entry.OriginalFileName == "ファイル.png");
    }

    [Fact]
    public async Task ResolverRejectsTraversalMissingAndChecksumMismatch()
    {
        using var fixture = new MediaFixture();
        var resolver = new MediaWikiMediaBinaryResolver(fixture.RootPath);
        Assert.Throws<InvalidDataException>(() => resolver.ResolvePath("../outside.bin"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => resolver.ReadVerifiedAsync("missing.bin", 1, new string('0', 64)));
        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ReadVerifiedAsync("media/example-v1.bin", 2, new string('0', 64)));
        if (!OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(Path.GetTempPath(), "cw-media-outside-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(outside, [1, 2, 3]);
                File.CreateSymbolicLink(Path.Combine(fixture.RootPath, "media", "escape.bin"), outside);
                await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ReadVerifiedAsync("media/escape.bin", 3, Convert.ToHexString(SHA256.HashData([1, 2, 3])).ToLowerInvariant()));
            }
            finally { File.Delete(outside); }
        }
    }

    [Fact]
    public void ManifestRejectsCaseCollisionsAndUnsafePaths()
    {
        var contributor = new MediaWikiContributor(MediaWikiContributorKind.NamedUser, "1", "tester", null, false);
        var entry = new MediaWikiMediaEntry("Example.png", "1", "image/png", 1, new string('a', 64), null, "../escape", null, contributor, null,
            [new("r1", 1, DateTimeOffset.UnixEpoch, contributor, "image/png", 1, new string('a', 64), null, "../escape", null)]);
        var collision = entry with { OriginalFileName = "example.png", SourceFilePageId = "2" };
        var findings = MediaWikiMediaManifestReader.Validate(new("1", "wiki", [entry, collision]));
        Assert.Contains(findings, finding => finding.Code == "DuplicateMediaFileName");
        Assert.Contains(findings, finding => finding.Code == "UnsafeMediaPath");
    }

    private sealed class MediaFixture : IDisposable
    {
        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), "cw-media-" + Guid.NewGuid().ToString("N"));
        public string XmlPath { get; }
        public string ManifestPath { get; }

        public MediaFixture()
        {
            Directory.CreateDirectory(Path.Combine(RootPath, "media"));
            XmlPath = FixturePath("media-fixture.xml");
            ManifestPath = Path.Combine(RootPath, "manifest.json");
            var contributor = new MediaWikiContributor(MediaWikiContributorKind.NamedUser, "21", "Media author", null, false);
            var ipContributor = new MediaWikiContributor(MediaWikiContributorKind.AnonymousIp, null, null, "192.0.2.51", false);
            var mapContributor = new MediaWikiContributor(MediaWikiContributorKind.NamedUser, "22", "Map author", null, false);
            var unicodeContributor = new MediaWikiContributor(MediaWikiContributorKind.AnonymousIp, null, null, "192.0.2.52", false);
            var exampleV1 = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };
            var exampleV2 = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 5, 6, 7, 8 };
            var jpeg = new byte[] { 255, 216, 255, 224, 0, 16, 74, 70, 73, 70 };
            var unicode = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 9, 10 };
            Write("media/example-v1.bin", exampleV1);
            Write("media/example-v2.bin", exampleV2);
            Write("media/map.bin", jpeg);
            Write("media/unicode.bin", unicode);
            var manifest = new MediaWikiMediaManifest("cw-m3-003b-fixture-1", "Continuum Synthetic Media Wiki|https://media.synthetic.invalid/wiki|MediaWiki 1.42", [
                Entry("Example.png", "600", "image/png", exampleV2, contributor, [Revision("6001", 1, "media/example-v1.bin", exampleV1, contributor), Revision("6002", 2, "media/example-v2.bin", exampleV2, ipContributor)]),
                Entry("地图.jpg", "601", "image/jpeg", jpeg, mapContributor, [Revision("6011", 1, "media/map.bin", jpeg, mapContributor)]),
                Entry("ファイル.png", "602", "image/png", unicode, unicodeContributor, [Revision("6021", 1, "media/unicode.bin", unicode, unicodeContributor)])
            ]);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        private void Write(string relative, byte[] bytes) => File.WriteAllBytes(Path.Combine(RootPath, relative), bytes);
        private static MediaWikiMediaEntry Entry(string name, string pageId, string mime, byte[] current, MediaWikiContributor uploader, IReadOnlyList<MediaWikiMediaRevision> revisions)
            => new(name, pageId, mime, current.LongLength, Hash(current), null, revisions[^1].RelativeBinaryPath, revisions[0].Timestamp, uploader, "synthetic", revisions);
        private static MediaWikiMediaRevision Revision(string id, int ordinal, string path, byte[] bytes, MediaWikiContributor uploader)
            => new(id, ordinal, DateTimeOffset.Parse($"2025-03-0{ordinal}T01:00:00Z"), uploader, path.EndsWith("map.bin", StringComparison.Ordinal) ? "image/jpeg" : "image/png", bytes.LongLength, Hash(bytes), null, path, "synthetic");
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        private static string FixturePath(string name)
        {
            var copied = Path.Combine(AppContext.BaseDirectory, "MediaWikiInventory", "Fixtures", name);
            return File.Exists(copied) ? copied : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MediaWikiInventory/Fixtures", name));
        }
        public void Dispose() { try { Directory.Delete(RootPath, true); } catch { } }
    }
}
