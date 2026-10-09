using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Interfaces.Repository;
using TightWiki.Plugin.Models;
using TightWiki.Plugin;
using ModelPermission = TightWiki.Plugin.Models.TwPermission;
using ModelPermissionDisposition = TightWiki.Plugin.Models.TwPermissionDisposition;

namespace TightWiki.Repository.Helpers;

/// <summary>Provider-neutral identity adapter over the existing SQLite users repository.</summary>
public sealed class SqliteApplicationIdentityRepository(ITwUsersRepository repository) : ITwApplicationIdentityRepository
{
    public Task CreateProfileAsync(Guid userId, string accountName, CancellationToken cancellationToken = default) => Wait(repository.CreateProfile(userId, accountName), cancellationToken);
    public async Task<TwAccountProfile?> GetProfileByUserIdAsync(Guid userId, CancellationToken cancellationToken = default) => await repository.GetAccountProfileByUserId(userId).WaitAsync(cancellationToken);
    public Task UpdateProfileAsync(TwAccountProfile profile, CancellationToken cancellationToken = default) => Wait(repository.UpdateProfile(profile), cancellationToken);
    public Task UpdateProfileAvatarAsync(Guid userId, byte[] imageData, string contentType, CancellationToken cancellationToken = default) => Wait(repository.UpdateProfileAvatar(userId, imageData, contentType), cancellationToken);
    public async Task<int> EnsureRoleAsync(string name, string? description = null, bool isBuiltIn = false, CancellationToken cancellationToken = default)
    {
        if (!await repository.DoesRoleExist(name).WaitAsync(cancellationToken)) await repository.InsertRole(name, description).WaitAsync(cancellationToken);
        return (await repository.GetRoleByName(name).WaitAsync(cancellationToken)).Id;
    }
    public Task<int> EnsurePermissionAsync(string name, string? description = null, CancellationToken cancellationToken = default) => Unsupported<int>();
    public async Task<int> GetPermissionDispositionIdAsync(string name, CancellationToken cancellationToken = default)
        => (await repository.GetAllPermissionDispositions().WaitAsync(cancellationToken)).Single(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Id;
    public Task AddRoleMembershipAsync(Guid userId, int roleId, CancellationToken cancellationToken = default) => Wait(repository.AddRoleMember(userId, roleId), cancellationToken);
    public async Task<int> AddAccountPermissionAsync(Guid userId, int permissionId, string disposition, string? ns = null, string? pageId = null, CancellationToken cancellationToken = default)
        => (await repository.InsertAccountPermission(userId, permissionId, disposition, ns, pageId).WaitAsync(cancellationToken))?.Id ?? throw new InvalidOperationException("The account permission was not created.");
    public async Task<int> AddRolePermissionAsync(int roleId, int permissionId, string disposition, string? ns = null, string? pageId = null, CancellationToken cancellationToken = default)
        => (await repository.InsertRolePermission(roleId, permissionId, disposition, ns, pageId).WaitAsync(cancellationToken))?.Id ?? throw new InvalidOperationException("The role permission was not created.");
    public Task<IReadOnlyList<TwApparentPermission>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken = default) => Read(repository.GetApparentAccountPermissions(userId), cancellationToken);
    public Task<IReadOnlyList<TwApparentPermission>> GetEffectiveRolePermissionsAsync(string roleName, CancellationToken cancellationToken = default) => Read(repository.GetApparentRolePermissions(roleName), cancellationToken);
    public Task<TwAccountProfile?> GetBasicProfileByUserIdAsync(Guid userId, CancellationToken cancellationToken = default) => Wait(repository.GetBasicProfileByUserId(userId), cancellationToken);
    public Task<TwAccountProfile?> GetProfileByNavigationAsync(string? navigation, CancellationToken cancellationToken = default) => Wait(repository.GetAccountProfileByNavigation(navigation), cancellationToken);
    public Task<TwProfileAvatar?> GetProfileAvatarByNavigationAsync(string navigation, CancellationToken cancellationToken = default) => Wait(repository.GetProfileAvatarByNavigation(navigation), cancellationToken);
    public Task<bool> ProfileAccountExistsAsync(string navigation, CancellationToken cancellationToken = default) => Wait(repository.DoesProfileAccountExist(navigation), cancellationToken);
    public Task<bool> EmailAddressExistsAsync(string? emailAddress, CancellationToken cancellationToken = default) => Wait(repository.DoesEmailAddressExist(emailAddress), cancellationToken);
    public Task<IReadOnlyList<TwRole>> GetRolesAsync(CancellationToken cancellationToken = default) => Read(repository.GetAllRoles(), cancellationToken);
    public async Task<TwRole?> GetRoleByNameAsync(string name, CancellationToken cancellationToken = default) => await repository.GetRoleByName(name).WaitAsync(cancellationToken);
    public Task<bool> RoleExistsAsync(string name, CancellationToken cancellationToken = default) => Wait(repository.DoesRoleExist(name), cancellationToken);
    public Task<bool> IsUserAdministratorAsync(Guid userId, CancellationToken cancellationToken = default) => Wait(repository.IsUserMemberOfAdministrators(userId), cancellationToken);
    public Task<TwAdminPasswordChangeState> GetAdminPasswordStatusAsync(CancellationToken cancellationToken = default) => Wait(repository.AdminPasswordStatus(), cancellationToken);
    public Task<IReadOnlyList<ModelPermission>> GetPermissionsAsync(CancellationToken cancellationToken = default) => Read(repository.GetAllPermissions(), cancellationToken);
    public Task<IReadOnlyList<ModelPermissionDisposition>> GetPermissionDispositionsAsync(CancellationToken cancellationToken = default) => Read(repository.GetAllPermissionDispositions(), cancellationToken);
    public Task<IReadOnlyList<TwAccountProfile>> GetUsersPagedAsync(int pageNumber, int pageSize, string? searchToken = null, CancellationToken cancellationToken = default) => Read(repository.GetAllUsersPaged(pageNumber, searchToken: searchToken), cancellationToken);
    public Task<IReadOnlyList<TwAccountProfile>> GetRoleMembersPagedAsync(int roleId, int pageNumber, int pageSize, CancellationToken cancellationToken = default) => Read(repository.GetRoleMembersPaged(roleId, pageNumber, orderByDirection: null, pageSize: pageSize), cancellationToken);
    public Task<IReadOnlyList<TwAccountPermission>> GetAccountPermissionsPagedAsync(Guid userId, int pageNumber, int pageSize, CancellationToken cancellationToken = default) => Read(repository.GetAccountPermissionsPaged(userId, pageNumber, pageSize: pageSize), cancellationToken);
    public Task<IReadOnlyList<TwAccountRoleMembership>> GetAccountRoleMembershipPagedAsync(Guid userId, int pageNumber, int pageSize, CancellationToken cancellationToken = default) => Read(repository.GetAccountRoleMembershipPaged(userId, pageNumber, pageSize: pageSize), cancellationToken);
    public Task<IReadOnlyList<TwRolePermission>> GetRolePermissionsPagedAsync(int roleId, int pageNumber, int pageSize, CancellationToken cancellationToken = default) => Read(repository.GetRolePermissionsPaged(roleId, pageNumber, pageSize: pageSize), cancellationToken);
    public Task<IReadOnlyList<TwRole>> AutoCompleteRolesAsync(string? searchText, CancellationToken cancellationToken = default) => Read(repository.AutoCompleteRole(searchText), cancellationToken);
    public Task<IReadOnlyList<TwAccountProfile>> AutoCompleteAccountsAsync(string? searchText, CancellationToken cancellationToken = default) => Read(repository.AutoCompleteAccount(searchText), cancellationToken);
    public Task<TwAddRoleMemberResult?> AddRoleMemberAsync(Guid userId, int roleId, CancellationToken cancellationToken = default) => Wait(repository.AddRoleMember(userId, roleId), cancellationToken);
    public Task<TwAddAccountMembershipResult?> AddAccountMembershipAsync(Guid userId, int roleId, CancellationToken cancellationToken = default) => Wait(repository.AddAccountMembership(userId, roleId), cancellationToken);
    public Task<TwAddRoleMemberResult?> AddRoleMemberByNameAsync(Guid userId, string roleName, CancellationToken cancellationToken = default) => Wait(repository.AddRoleMemberByname(userId, roleName), cancellationToken);
    public Task RemoveRoleMemberAsync(int roleId, Guid userId, CancellationToken cancellationToken = default) => Wait(repository.RemoveRoleMember(roleId, userId), cancellationToken);
    public Task RemoveRolePermissionAsync(int id, CancellationToken cancellationToken = default) => Wait(repository.RemoveRolePermission(id), cancellationToken);
    public Task RemoveAccountPermissionAsync(int id, CancellationToken cancellationToken = default) => Wait(repository.RemoveAccountPermission(id), cancellationToken);
    public Task<TwInsertRolePermissionResult?> InsertRolePermissionAsync(int roleId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default) => Wait(repository.InsertRolePermission(roleId, permissionId, disposition, ns, pageId), cancellationToken);
    public Task<TwInsertAccountPermissionResult?> InsertAccountPermissionAsync(Guid userId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default) => Wait(repository.InsertAccountPermission(userId, permissionId, disposition, ns, pageId), cancellationToken);
    public Task<bool> IsAccountMemberOfRoleAsync(Guid userId, int roleId, CancellationToken cancellationToken = default) => Wait(repository.IsAccountAMemberOfRole(userId, roleId), cancellationToken);
    public Task<bool> IsAccountPermissionDefinedAsync(Guid userId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default) => Wait(repository.IsAccountPermissionDefined(userId, permissionId, disposition, ns, pageId), cancellationToken);
    public Task<bool> IsRolePermissionDefinedAsync(int roleId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default) => Wait(repository.IsRolePermissionDefined(roleId, permissionId, disposition, ns, pageId), cancellationToken);
    public Task AnonymizeProfileAsync(Guid userId, CancellationToken cancellationToken = default) => Wait(repository.AnonymizeProfile(userId), cancellationToken);
    public Task DeleteRoleAsync(int roleId, CancellationToken cancellationToken = default) => Wait(repository.DeleteRole(roleId), cancellationToken);
    public Task<bool> InsertRoleAsync(string name, string? description, CancellationToken cancellationToken = default) => Wait(repository.InsertRole(name, description), cancellationToken);
    public Task SetAdminPasswordChangedAsync(CancellationToken cancellationToken = default) => Wait(repository.SetAdminPasswordIsChanged(), cancellationToken);

    private static async Task<T> Wait<T>(Task<T> task, CancellationToken cancellationToken) => await task.WaitAsync(cancellationToken);
    private static async Task Wait(Task task, CancellationToken cancellationToken) => await task.WaitAsync(cancellationToken);
    private static async Task<IReadOnlyList<T>> Read<T>(Task<List<T>> task, CancellationToken cancellationToken) => await task.WaitAsync(cancellationToken);
    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("This SQLite identity adapter does not expose the requested operation."));
}
