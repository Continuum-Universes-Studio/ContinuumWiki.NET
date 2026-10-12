using TightWiki.Plugin.Models;

namespace TightWiki.Engine.MediaWiki;

/// <summary>
/// Verifies grants obtained from an independently controlled evidence source.
/// It does not accept grants from request JSON and does not pretend that an
/// in-memory lookup provides atomic single-use or revocation semantics.
/// </summary>
public sealed class TwCategoryAdministrativeGrantProvider(
    ITwCategoryAdministrativeGrantEvidenceSource evidenceSource)
    : ITwCategoryAdministrativeGrantProvider
{
    public async Task<TwCategoryAdministrativeAuthorizationDecision> EvaluateAsync(
        TwCategoryAdministrativeAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(false, TwCategoryAdministrativeAuthorizationReason.CancellationRequested);

        try
        {
            var evidence = await evidenceSource.GetAsync(request, cancellationToken);
            return TwCategoryAdministrativeGrantVerifier.Evaluate(
                evidence.Grant,
                request,
                TimeProvider.System.GetUtcNow().UtcDateTime,
                evidence.ReplayAlreadyConsumed,
                evidence.DurableReplayProtectionAvailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, TwCategoryAdministrativeAuthorizationReason.CancellationRequested);
        }
        catch
        {
            return new(false, TwCategoryAdministrativeAuthorizationReason.ProviderUnavailable);
        }
    }
}
