using Microsoft.Extensions.Configuration;

namespace TightWiki.Engine.MediaWiki;

/// <summary>Explicit, fail-closed application settings for the experimental worker.</summary>
public sealed record MediaWikiScribuntoExperimentalConfiguration(
    bool Enabled = false,
    string? Transport = null,
    string? SocketPath = null,
    string? HostCertificationPath = null,
    int ConnectionTimeoutMilliseconds = 250,
    int ExecutionTimeoutMilliseconds = 250,
    int MaxRequestCharacters = 2_000_000,
    int MaxResponseCharacters = 100_000)
{
    public bool UsesApprovedSocketTransport =>
        string.Equals(Transport, "unix-socket", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(SocketPath);

    public static MediaWikiScribuntoExperimentalConfiguration From(IConfiguration configuration)
    {
        var section = configuration.GetSection("ContinuumWiki:ExperimentalLua");
        var value = section.Get<MediaWikiScribuntoExperimentalConfiguration>() ?? new();
        if (!value.Enabled || !value.UsesApprovedSocketTransport || !Path.IsPathFullyQualified(value.SocketPath!)
            || value.ConnectionTimeoutMilliseconds is < 1 or > 10_000
            || value.ExecutionTimeoutMilliseconds is < 1 or > 60_000
            || value.MaxRequestCharacters is < 1 or > 2_000_000
            || value.MaxResponseCharacters is < 1 or > 250_000)
            return new();
        return value;
    }
}
