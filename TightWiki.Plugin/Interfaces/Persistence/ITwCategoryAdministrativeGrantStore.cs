using TightWiki.Plugin.Models;

namespace TightWiki.Plugin.Interfaces.Persistence;

#pragma warning disable CS1591

public interface ITwCategoryAdministrativeGrantStore :
    ITwCategoryAdministrativeGrantEvidenceSource,
    ITwCategoryAdministrativeGrantOutcomeRecorder
{
    Task<TwCategoryAdministrativeGrantCreateResult> CreateAsync(
        TwCategoryAdministrativeGrantCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<TwCategoryAdministrativeGrantLoadResult> LoadAsync(
        Guid grantId,
        CancellationToken cancellationToken = default);

    Task<TwCategoryAdministrativeGrantConsumeResult> TryConsumeAsync(
        TwCategoryAdministrativeGrantConsumeRequest request,
        CancellationToken cancellationToken = default);

    Task<TwCategoryAdministrativeGrantRevokeResult> RevokeAsync(
        TwCategoryAdministrativeGrantRevokeRequest request,
        CancellationToken cancellationToken = default);
}

#pragma warning restore CS1591
