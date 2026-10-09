namespace TightWiki.Plugin.Models;

/// <summary>Supported administrative maintenance actions.</summary>
public enum TwMaintenanceAction
{
    /// <summary>Optimizes provider storage structures when supported.</summary>
    Optimize,
    /// <summary>Reclaims provider storage when supported.</summary>
    Vacuum,
    /// <summary>Runs a read-only integrity verification.</summary>
    Verify
}

/// <summary>Outcome of a provider-aware maintenance operation.</summary>
public sealed class TwMaintenanceResult
{
    /// <summary>Indicates whether the provider supports the requested operation.</summary>
    public bool Supported { get; init; }
    /// <summary>Indicates whether the supported operation completed successfully.</summary>
    public bool Succeeded { get; init; }
    /// <summary>Human-readable operation details.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Creates a successful result.</summary>
    public static TwMaintenanceResult Success(string message) => new() { Supported = true, Succeeded = true, Message = message };
    /// <summary>Creates an explicitly unsupported result.</summary>
    public static TwMaintenanceResult Unsupported(string message) => new() { Supported = false, Succeeded = false, Message = message };
}

/// <summary>Read-only provider health and schema status.</summary>
public sealed class TwDatabaseMaintenanceStatus
{
    /// <summary>Selected persistence provider.</summary>
    public string Provider { get; init; } = string.Empty;
    /// <summary>Provider database name.</summary>
    public string DatabaseName { get; init; } = string.Empty;
    /// <summary>Indicates whether the health check succeeded.</summary>
    public bool IsHealthy { get; init; }
    /// <summary>Indicates whether required schema objects were found.</summary>
    public bool IsInitialized { get; init; }
    /// <summary>Provider schema version summary.</summary>
    public string SchemaVersion { get; init; } = string.Empty;
    /// <summary>Seed capability and state summary.</summary>
    public string SeedStatus { get; init; } = string.Empty;
    /// <summary>Human-readable health details.</summary>
    public string Message { get; init; } = string.Empty;
}
