namespace TightWiki.Repository.Helpers;

/// <summary>
/// Applies the small, explicit SQL dialect correction required by MariaDB.
/// MySQL's 0900 collation family is not available on MariaDB; the Unicode
/// collation below preserves the case-insensitive utf8mb4 contract used by the
/// existing schema without introducing a second provider or ORM.
/// </summary>
internal static class MariaDbSqlCompatibility
{
    public static string Normalize(string sql)
        => sql.Replace("utf8mb4_0900_ai_ci", "utf8mb4_unicode_ci", StringComparison.OrdinalIgnoreCase);
}
