using System.Reflection;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Keeps legacy SQLite-shaped plugin contracts resolvable during MySQL
/// development startup without constructing SQLite factories. Any legacy
/// operation that has not yet been migrated fails explicitly at invocation.
/// </summary>
public static class MySqlLegacyDependencyGate
{
    public static T Create<T>(Func<MethodInfo, object?>? resolver = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, GateProxy>();
        ((GateProxy)(object)proxy).ContractName = typeof(T).FullName ?? typeof(T).Name;
        ((GateProxy)(object)proxy).Resolver = resolver;
        return proxy;
    }

    private class GateProxy : DispatchProxy
    {
        public string ContractName { get; set; } = string.Empty;
        public Func<MethodInfo, object?>? Resolver { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod != null && Resolver != null)
            {
                var resolved = Resolver(targetMethod);
                if (resolved != null) return resolved;
            }
            if (targetMethod?.Name == nameof(ToString)) return $"MySQL legacy contract gate: {ContractName}";
            throw new NotSupportedException($"The legacy SQLite contract '{ContractName}' is unavailable in MySQL mode. Use the provider-neutral application contract or an explicitly supported MySQL operation.");
        }
    }
}
