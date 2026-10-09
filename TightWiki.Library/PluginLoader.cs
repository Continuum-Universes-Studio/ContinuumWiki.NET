using Microsoft.Extensions.Logging;
using System.Reflection;

using TightWiki.Plugin.Interfaces.Persistence;

namespace TightWiki.Library
{
    public static class PluginLoader
    {
        public static void LoadPlugins(ILogger logger, string pluginsDirectory)
            => LoadPlugins(logger, pluginsDirectory, TwPersistenceProvider.SQLite);

        public static void LoadPlugins(ILogger logger, string pluginsDirectory, TwPersistenceProvider provider)
        {
            try
            {
                if (!Directory.Exists(pluginsDirectory))
                {
                    Directory.CreateDirectory(pluginsDirectory);
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"An error occurred while loading plugins: {ex.Message}");
            }

            if (Directory.Exists(pluginsDirectory))
            {
                foreach (var dllPath in Directory.GetFiles(pluginsDirectory, "*.dll"))
                {
                    try
                    {
                        var assembly = Assembly.LoadFrom(dllPath);

                        var pluginType = assembly.GetTypes()
                            .FirstOrDefault(t => t.FullName == "TightWiki.Plugin.TwPluginModule");

                        if (pluginType == null)
                            continue;

                        var instance = Activator.CreateInstance(pluginType);
                        var compatibility = instance as ITwPluginPersistenceCompatibility;
                        var decision = PluginPersistenceCompatibility.Evaluate(compatibility, provider);
                        if (!decision.IsSupported)
                        {
                            logger.LogError("Rejected plugin {Plugin}: {Diagnostic} Configure SQLite or migrate the plugin.", Path.GetFileName(dllPath), decision.Diagnostic);
                            continue;
                        }
                        var method = pluginType.GetMethod("GetVersion");
                        var version = method?.Invoke(instance, null) as string;
                        logger.LogInformation($"Loaded plugin: {Path.GetFileName(dllPath)}, version: {version}");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"Failed to load {Path.GetFileName(dllPath)}: {ex.Message}");
                    }
                }
            }
        }
    }
}
