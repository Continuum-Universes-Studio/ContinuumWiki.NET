using System.Reflection;
using TightWiki.Plugin.Interfaces;

namespace TightWiki.Plugin
{
    /// <summary>
    /// Used by the engine to verify the plugin version.
    /// The engine will call GetVersion() to check if the plugin is compatible with the engine version.
    /// The plugin version should be in the format of "Major.Minor.Patch".
    /// </summary>
    public class TwPluginModule
        : ITwPluginModule, Interfaces.Persistence.ITwPluginPersistenceCompatibility
    {
        /// <summary>Gets the providers supported by the built-in plugin module.</summary>
        public IReadOnlyCollection<Interfaces.Persistence.TwPersistenceProvider> SupportedProviders { get; } =
            new[] { Interfaces.Persistence.TwPersistenceProvider.SQLite, Interfaces.Persistence.TwPersistenceProvider.MySQL };

        /// <summary>Gets whether this module requires legacy SQLite repository services.</summary>
        public bool RequiresLegacySqliteRepositories => false;

        /// <summary>
        /// Gets the current assembly version in Major.Minor.Patch format.
        /// </summary>
        public string GetVersion()
        {
            return string.Join('.', (Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? "0.0.0.0").Split('.').Take(3)); //Major.Minor.Patch
        }
    }
}
