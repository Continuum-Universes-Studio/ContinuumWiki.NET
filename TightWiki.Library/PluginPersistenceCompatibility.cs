using TightWiki.Plugin.Interfaces.Persistence;

namespace TightWiki.Library;

public static class PluginPersistenceCompatibility
{
    public static (bool IsSupported, string? Diagnostic) Evaluate(
        ITwPluginPersistenceCompatibility? declaration,
        TwPersistenceProvider provider)
    {
        if (declaration is null)
        {
            return provider == TwPersistenceProvider.SQLite
                ? (true, null)
                : (false, "The plugin has no provider-neutral persistence declaration and is treated as a legacy SQLite-only plugin.");
        }

        if (!declaration.SupportedProviders.Contains(provider))
            return (false, $"The plugin does not support persistence provider {provider}.");

        if (provider == TwPersistenceProvider.MySQL && declaration.RequiresLegacySqliteRepositories)
            return (false, "The plugin requires legacy SQLite repositories and has no MySQL adapter.");

        return (true, null);
    }
}
