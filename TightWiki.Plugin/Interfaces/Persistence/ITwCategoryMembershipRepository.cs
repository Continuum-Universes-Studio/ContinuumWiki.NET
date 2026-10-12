using TightWiki.Plugin.Models;

namespace TightWiki.Plugin.Interfaces.Persistence;

#pragma warning disable CS1591

/// <summary>
/// Preparation-only category persistence boundary. Application DI registers
/// only an explicit rejecting adapter until an authorized durable provider
/// exists; rendering never calls it.
/// Implementations must enforce authorization and an atomic page-revision
/// replacement transaction.
/// </summary>
public interface ITwCategoryMembershipRepository
{
    Task<TwCategoryIndexConsistencyResult> GetIndexConsistencyAsync(
        int pageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads category-wide index readiness without returning membership rows.
    /// Implementations must not use this as a visibility or count shortcut.
    /// </summary>
    Task<TwCategoryIndexConsistencyResult> GetCategoryIndexConsistencyAsync(
        TwCategoryIdentity category,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TwCategoryMembershipRecord>> GetMembershipsByPageIdAsync(
        int pageId,
        CancellationToken cancellationToken = default);

    Task<TwCategoryMembershipReplacementResult> ReplaceMembershipsAsync(
        TwCategoryMembershipReplacementRequest request,
        CancellationToken cancellationToken = default);

    Task<TwCategoryMemberPage> GetMembersAsync(
        TwCategoryMemberQuery query,
        CancellationToken cancellationToken = default);

    Task<TwCategoryMembershipReplacementResult> InvalidateMembershipsAsync(
        int pageId,
        int expectedPageRevision,
        CancellationToken cancellationToken = default);
}

#pragma warning restore CS1591
