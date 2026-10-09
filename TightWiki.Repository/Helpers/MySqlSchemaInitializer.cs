using TightWiki.Library.Database;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Coordinates the currently implemented MySQL schema domains in dependency order.
/// This is an explicit provisioning operation and is never invoked implicitly by the
/// SQLite application startup path.
/// </summary>
public sealed class MySqlSchemaInitializer
{
    private readonly MySqlConfigurationSchemaInitializer _configuration = new();
    private readonly MySqlIdentitySchemaInitializer _identity = new();
    private readonly MySqlPageSchemaInitializer _pages = new();
    private readonly MySqlOperationsSchemaInitializer _operations = new();
    private readonly MySqlTranslationSchemaInitializer _translations = new();
    private readonly MySqlDefaultsSchemaInitializer _defaults = new();

    public async Task InitializeAsync(IDatabaseConnectionFactory connectionFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        await MySqlSchemaCompatibility.ValidateSupportedVersionsAsync(connectionFactory, cancellationToken);
        await _configuration.InitializeAsync(connectionFactory, cancellationToken);
        await _identity.InitializeAsync(connectionFactory, cancellationToken);
        await _pages.InitializeAsync(connectionFactory, cancellationToken);
        await _operations.InitializeAsync(connectionFactory, cancellationToken);
        await _translations.InitializeAsync(connectionFactory, cancellationToken);
        await _defaults.InitializeAsync(connectionFactory, cancellationToken);
    }
}
