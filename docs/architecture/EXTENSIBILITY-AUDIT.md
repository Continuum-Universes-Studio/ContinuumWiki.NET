# Extensibility audit

`TightWiki.Plugin` defines the public contracts and attributes: `ITwPlugin`, `ITwPluginModule`, `ITwEngineState`, `ITwFunctionDescriptor`, handler descriptors, `TwPluginAttribute`, function attributes, handler attributes, and regular-expression attributes. `TightWiki.Engine/WikiEngine.cs` scans loaded AppDomain assemblies for attributed types, instantiates them through `Activator.CreateInstance`, orders them by plugin/handler precedence, and exposes standard, scope, processing-instruction, completion, exception, and markup pipelines.

`TightWiki.Library/PluginLoader.cs` scans `Plugins/*.dll`, loads assemblies with `Assembly.LoadFrom`, finds the exact `TightWiki.Plugin.TwPluginModule` type, instantiates it, and logs its version. The loader is therefore dynamic but narrow: loading is directory-based and convention-based; it is not a dependency-isolated `AssemblyLoadContext`, signed-plugin policy, version negotiation, or DI module system. A plugin can execute code during reflection/instantiation, so plugin directories must be trusted.

`TightWiki.Plugin.Example/Example.cs` is the concrete contract example: a plugin class with standard and scope functions and regex-driven markup handlers returning `Task<TwPluginResult>`. Repository interfaces (`ITwPageRepository`, `ITwUsersRepository`, etc.) expose persistence contracts, but they currently expose SQLite wrapper types, making provider replacement a public-contract concern.

Recommended audit boundary for CW-M1: add characterization tests around precedence, malformed plugin isolation, recursion/expansion limits, HTML sanitization, and representative handler output before extending the plugin surface.
