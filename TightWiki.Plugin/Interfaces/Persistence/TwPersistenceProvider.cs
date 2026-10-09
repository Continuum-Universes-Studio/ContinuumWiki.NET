namespace TightWiki.Plugin.Interfaces.Persistence;

/// <summary>Identifies a supported application persistence provider.</summary>
public enum TwPersistenceProvider
{
    /// <summary>The existing SQLite application and plugin provider.</summary>
    SQLite,
    /// <summary>The provider-neutral Oracle MySQL application provider.</summary>
    MySQL
}

/// <summary>Optional plugin declaration for persistence compatibility.</summary>
public interface ITwPluginPersistenceCompatibility
{
    /// <summary>Gets the providers for which the plugin declares compatibility.</summary>
    IReadOnlyCollection<TwPersistenceProvider> SupportedProviders { get; }
    /// <summary>Gets whether the plugin still requires legacy SQLite repository services.</summary>
    bool RequiresLegacySqliteRepositories { get; }
}
