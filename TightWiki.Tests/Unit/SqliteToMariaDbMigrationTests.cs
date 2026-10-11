using ContinuumWiki.SqliteToMariaDb;

public sealed class SqliteToMariaDbMigrationTests
{
    [Fact]
    public void ParseRequiresExplicitOperationAndConfiguration()
    {
        Assert.Throws<ArgumentException>(() => MigrationOptions.Parse([]));
        Assert.Throws<ArgumentException>(() => MigrationOptions.Parse(["execute"]));
    }

    [Fact]
    public void ParseRejectsUnknownOperation()
    {
        Assert.Throws<ArgumentException>(() => MigrationOptions.Parse(["migrate", "--source-root", "/tmp/source"]));
    }

    [Fact]
    public void ParseAcceptsIntegrityOperation()
    {
        var options = MigrationOptions.Parse([
            "integrity",
            "--source-root", "/tmp/source",
            "--translations", "/tmp/Translations.db",
            "--defaults", "/tmp/defaults.db",
            "--destination", "Server=127.0.0.1;Database=continuumwiki;",
            "--report", "/tmp/integrity.json"]);

        Assert.Equal("integrity", options.Operation);
    }
}
