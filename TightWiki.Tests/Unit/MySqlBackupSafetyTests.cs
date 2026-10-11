using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Unit;

public sealed class MySqlBackupSafetyTests
{
    [Fact]
    public void RestoreRejectsSourceDatabase() => Assert.Throws<InvalidOperationException>(() => MySqlBackupSafety.ValidateRestoreTarget("wiki", "WIKI"));

    [Fact]
    public void DestinationRejectsDirectory() {
        var directory = Directory.CreateTempSubdirectory();
        try { Assert.Throws<ArgumentException>(() => MySqlBackupSafety.ValidateArtifactDestination(directory.FullName)); }
        finally { directory.Delete(); }
    }

    [Fact]
    public async Task ManifestDetectsTampering() {
        var directory = Directory.CreateTempSubdirectory();
        try {
            var artifact = Path.Combine(directory.FullName, "backup.sql");
            await File.WriteAllTextAsync(artifact, "original");
            var manifest = await MySqlBackupManifest.CreateAsync(artifact, "8.4", "2.40.0", "wiki");
            await File.WriteAllTextAsync(artifact, "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() => manifest.VerifyArtifactAsync(artifact));
        } finally { directory.Delete(true); }
    }
}
