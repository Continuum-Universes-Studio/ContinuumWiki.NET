using Dapper;
using TightWiki.Library;
using TightWiki.Library.Database;
using TightWiki.Plugin;
using TightWiki.Plugin.Models;
using TightWiki.Plugin.Interfaces.Persistence;
using ModelPermission = TightWiki.Plugin.Models.TwPermission;
using ModelPermissionDisposition = TightWiki.Plugin.Models.TwPermissionDisposition;

namespace TightWiki.Repository.Helpers;

/// <summary>
/// Provider-neutral MySQL slice for TightWiki profiles and custom permission data.
/// ASP.NET Identity users, password hashes, roles, claims, logins, and tokens are
/// owned by <see cref="MySqlApplicationDbContext"/> and UserManager.
/// </summary>
public sealed class MySqlUsersRepository : ITwApplicationIdentityRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public MySqlUsersRepository(IDatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task CreateProfileAsync(Guid userId, string accountName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        var navigation = TwNavigation.Clean(accountName);
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO Profile (UserId, AccountName, Navigation, CreatedDate, ModifiedDate) VALUES (@UserId, @AccountName, @Navigation, @Now, @Now);",
            new { UserId = userId.ToString("D"), AccountName = accountName, Navigation = navigation, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken));
    }

    public async Task<TwAccountProfile?> GetProfileByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<TwAccountProfile>(new CommandDefinition("""
            SELECT U.Id AS UserId, U.Email AS EmailAddress, P.AccountName, P.Navigation,
                   P.Biography, P.Avatar, P.CreatedDate, P.ModifiedDate,
                   U.EmailConfirmed, U.PhoneNumber, U.TwoFactorEnabled
            FROM Profile P INNER JOIN AspNetUsers U ON U.Id = P.UserId
            WHERE P.UserId = @UserId;
            """, new { UserId = userId.ToString("D") }, cancellationToken: cancellationToken));
    }

    public async Task UpdateProfileAsync(TwAccountProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken);
        try
        {
            await session.Connection.ExecuteAsync(new CommandDefinition("""
                UPDATE Profile
                SET AccountName = @AccountName, Navigation = @Navigation,
                    Biography = @Biography, ModifiedDate = @ModifiedDate
                WHERE UserId = @UserId;
                """, new
            {
                UserId = profile.UserId.ToString("D"), profile.AccountName, profile.Navigation,
                profile.Biography, profile.ModifiedDate
            }, transaction.Transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task UpdateProfileAvatarAsync(Guid userId, byte[] imageData, string contentType, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Profile SET Avatar = @Avatar, AvatarContentType = @ContentType, ModifiedDate = @ModifiedDate WHERE UserId = @UserId;",
            new { UserId = userId.ToString("D"), Avatar = imageData, ContentType = contentType, ModifiedDate = DateTime.UtcNow },
            cancellationToken: cancellationToken));
    }

    public async Task<int> EnsureRoleAsync(string name, string? description = null, bool isBuiltIn = false, CancellationToken cancellationToken = default)
        => await EnsureLookupIdAsync("Role", "Name", name, "Description", description, "IsBuiltIn", isBuiltIn, cancellationToken);

    public async Task<int> EnsurePermissionAsync(string name, string? description = null, CancellationToken cancellationToken = default)
        => await EnsureLookupIdAsync("Permission", "Name", name, "Description", description, null, null, cancellationToken);

    public async Task<int> GetPermissionDispositionIdAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT Id FROM PermissionDisposition WHERE Name = @Name;", new { Name = name }, cancellationToken: cancellationToken));
    }

    public async Task AddRoleMembershipAsync(Guid userId, int roleId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO AccountRole (UserId, RoleId) VALUES (@UserId, @RoleId) ON DUPLICATE KEY UPDATE RoleId = VALUES(RoleId);",
            new { UserId = userId.ToString("D"), RoleId = roleId }, cancellationToken: cancellationToken));
    }

    public async Task<int> AddAccountPermissionAsync(Guid userId, int permissionId, string disposition, string? ns = null, string? pageId = null, CancellationToken cancellationToken = default)
    {
        var dispositionId = await GetPermissionDispositionIdAsync(disposition, cancellationToken);
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO AccountPermission (UserId, PermissionId, Namespace, PageId, PermissionDispositionId)
            VALUES (@UserId, @PermissionId, @Namespace, @PageId, @DispositionId);
            SELECT LAST_INSERT_ID();
            """, new { UserId = userId.ToString("D"), PermissionId = permissionId, Namespace = ns, PageId = pageId, DispositionId = dispositionId }, cancellationToken: cancellationToken));
    }

    public async Task<int> AddRolePermissionAsync(int roleId, int permissionId, string disposition, string? ns = null, string? pageId = null, CancellationToken cancellationToken = default)
    {
        var dispositionId = await GetPermissionDispositionIdAsync(disposition, cancellationToken);
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT INTO RolePermission (RoleId, PermissionId, Namespace, PageId, PermissionDispositionId)
            VALUES (@RoleId, @PermissionId, @Namespace, @PageId, @DispositionId);
            SELECT LAST_INSERT_ID();
            """, new { RoleId = roleId, PermissionId = permissionId, Namespace = ns, PageId = pageId, DispositionId = dispositionId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TwApparentPermission>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var rows = await session.Connection.QueryAsync<TwApparentPermission>(new CommandDefinition("""
            SELECT P.Name AS Permission, PD.Name AS PermissionDisposition, RP.Namespace, RP.PageId
            FROM RolePermission RP
            INNER JOIN AccountRole AR ON AR.RoleId = RP.RoleId AND AR.UserId = @UserId
            INNER JOIN Permission P ON P.Id = RP.PermissionId
            INNER JOIN PermissionDisposition PD ON PD.Id = RP.PermissionDispositionId
            UNION
            SELECT P.Name, PD.Name, AP.Namespace, AP.PageId
            FROM AccountPermission AP
            INNER JOIN Permission P ON P.Id = AP.PermissionId
            INNER JOIN PermissionDisposition PD ON PD.Id = AP.PermissionDispositionId
            WHERE AP.UserId = @UserId;
            """, new { UserId = userId.ToString("D") }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TwApparentPermission>> GetEffectiveRolePermissionsAsync(string roleName, CancellationToken cancellationToken = default)
        => await QueryAsync<TwApparentPermission>("SELECT DISTINCT P.Name AS Permission, PD.Name AS PermissionDisposition, RP.Namespace, RP.PageId FROM RolePermission RP INNER JOIN Role R ON R.Id = RP.RoleId INNER JOIN Permission P ON P.Id = RP.PermissionId INNER JOIN PermissionDisposition PD ON PD.Id = RP.PermissionDispositionId WHERE R.Name = @RoleName;", new { RoleName = roleName }, cancellationToken);

    public async Task<TwAccountProfile?> GetBasicProfileByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
        => await QueryProfileAsync("WHERE P.UserId = @UserId", new { UserId = userId.ToString("D") }, cancellationToken, basic: true);

    public async Task<TwAccountProfile?> GetProfileByNavigationAsync(string? navigation, CancellationToken cancellationToken = default)
        => await QueryProfileAsync("WHERE P.Navigation = @Navigation", new { Navigation = navigation }, cancellationToken);

    public async Task<TwProfileAvatar?> GetProfileAvatarByNavigationAsync(string navigation, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<TwProfileAvatar>(new CommandDefinition("SELECT Avatar AS Bytes, AvatarContentType AS ContentType FROM Profile WHERE Navigation = @Navigation;", new { Navigation = navigation }, cancellationToken: cancellationToken));
    }

    public async Task<bool> ProfileAccountExistsAsync(string navigation, CancellationToken cancellationToken = default)
        => await ScalarAsync<int>("SELECT COUNT(*) FROM Profile WHERE Navigation = @Navigation;", new { Navigation = navigation }, cancellationToken) > 0;

    public async Task<bool> EmailAddressExistsAsync(string? emailAddress, CancellationToken cancellationToken = default)
        => await ScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers WHERE NormalizedEmail = @Email OR Email = @Email;", new { Email = emailAddress?.ToUpperInvariant() }, cancellationToken) > 0;

    public async Task<IReadOnlyList<TwRole>> GetRolesAsync(CancellationToken cancellationToken = default)
        => await QueryAsync<TwRole>("SELECT Id, Name, Description, IsBuiltIn FROM Role ORDER BY Name;", null, cancellationToken);

    public async Task<TwRole?> GetRoleByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<TwRole>(new CommandDefinition("SELECT Id, Name, Description, IsBuiltIn FROM Role WHERE Name = @Name;", new { Name = name }, cancellationToken: cancellationToken));
    }

    public async Task<bool> RoleExistsAsync(string name, CancellationToken cancellationToken = default)
        => await ScalarAsync<int>("SELECT COUNT(*) FROM Role WHERE Name = @Name;", new { Name = name }, cancellationToken) > 0;

    public async Task<bool> IsUserAdministratorAsync(Guid userId, CancellationToken cancellationToken = default)
        => await ScalarAsync<int>("SELECT COUNT(*) FROM AccountRole AR INNER JOIN Role R ON R.Id = AR.RoleId WHERE AR.UserId = @UserId AND R.Name = 'Administrator';", new { UserId = userId.ToString("D") }, cancellationToken) > 0;

    public async Task<IReadOnlyList<ModelPermission>> GetPermissionsAsync(CancellationToken cancellationToken = default)
        => await QueryAsync<ModelPermission>("SELECT Id, Name FROM Permission ORDER BY Name;", null, cancellationToken);

    public async Task<IReadOnlyList<ModelPermissionDisposition>> GetPermissionDispositionsAsync(CancellationToken cancellationToken = default)
        => await QueryAsync<ModelPermissionDisposition>("SELECT Id, Name FROM PermissionDisposition ORDER BY Name;", null, cancellationToken);

    public async Task<IReadOnlyList<TwAccountProfile>> GetUsersPagedAsync(int pageNumber, int pageSize, string? searchToken = null, CancellationToken cancellationToken = default)
        => await QueryProfilesAsync("WHERE (@Search IS NULL OR P.AccountName LIKE CONCAT('%', @Search, '%') OR U.Email LIKE CONCAT('%', @Search, '%')) ORDER BY P.AccountName LIMIT @PageSize OFFSET @Offset", new { Search = searchToken, PageSize = pageSize, Offset = (pageNumber - 1) * pageSize }, cancellationToken, pageSize);

    public async Task<IReadOnlyList<TwAccountProfile>> GetRoleMembersPagedAsync(int roleId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        => await QueryProfilesAsync("INNER JOIN AccountRole AR ON AR.UserId = P.UserId WHERE AR.RoleId = @RoleId ORDER BY P.AccountName LIMIT @PageSize OFFSET @Offset", new { RoleId = roleId, PageSize = pageSize, Offset = (pageNumber - 1) * pageSize }, cancellationToken, pageSize);

    public async Task<IReadOnlyList<TwAccountPermission>> GetAccountPermissionsPagedAsync(Guid userId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        => await QueryAsync<TwAccountPermission>("""
            SELECT AP.Id, P.Name AS Permission, PD.Name AS PermissionDisposition, AP.Namespace, AP.PageId,
                   COALESCE(AP.Namespace, AP.PageId) AS ResourceName, @PageSize AS PaginationPageSize,
                   CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount
            FROM AccountPermission AP INNER JOIN Permission P ON P.Id = AP.PermissionId
            INNER JOIN PermissionDisposition PD ON PD.Id = AP.PermissionDispositionId
            WHERE AP.UserId = @UserId ORDER BY P.Name, PD.Name LIMIT @PageSize OFFSET @Offset;
            """, new { UserId = userId.ToString("D"), PageSize = pageSize, Offset = (pageNumber - 1) * pageSize }, cancellationToken);

    public async Task<IReadOnlyList<TwAccountRoleMembership>> GetAccountRoleMembershipPagedAsync(Guid userId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        => await QueryAsync<TwAccountRoleMembership>("SELECT AR.Id, R.Name, AR.RoleId, @PageSize AS PaginationPageSize, CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount FROM AccountRole AR INNER JOIN Role R ON R.Id = AR.RoleId WHERE AR.UserId = @UserId ORDER BY R.Name LIMIT @PageSize OFFSET @Offset;", new { UserId = userId.ToString("D"), PageSize = pageSize, Offset = (pageNumber - 1) * pageSize }, cancellationToken);

    public async Task<IReadOnlyList<TwRolePermission>> GetRolePermissionsPagedAsync(int roleId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        => await QueryAsync<TwRolePermission>("SELECT RP.Id, P.Name AS Permission, PD.Name AS PermissionDisposition, RP.Namespace, RP.PageId, COALESCE(RP.Namespace, RP.PageId) AS ResourceName, @PageSize AS PaginationPageSize, CEILING(COUNT(*) OVER () / @PageSize) AS PaginationPageCount FROM RolePermission RP INNER JOIN Permission P ON P.Id = RP.PermissionId INNER JOIN PermissionDisposition PD ON PD.Id = RP.PermissionDispositionId WHERE RP.RoleId = @RoleId ORDER BY P.Name, PD.Name LIMIT @PageSize OFFSET @Offset;", new { RoleId = roleId, PageSize = pageSize, Offset = (pageNumber - 1) * pageSize }, cancellationToken);

    public async Task<IReadOnlyList<TwRole>> AutoCompleteRolesAsync(string? searchText, CancellationToken cancellationToken = default)
        => await QueryAsync<TwRole>("SELECT Id, Name, Description, IsBuiltIn FROM Role WHERE Name LIKE CONCAT('%', @Search, '%') ORDER BY Name LIMIT 50;", new { Search = searchText ?? string.Empty }, cancellationToken);

    public async Task<IReadOnlyList<TwAccountProfile>> AutoCompleteAccountsAsync(string? searchText, CancellationToken cancellationToken = default)
        => await QueryProfilesAsync("WHERE P.AccountName LIKE CONCAT('%', @Search, '%') OR P.Navigation LIKE CONCAT('%', @Search, '%') ORDER BY P.AccountName LIMIT 50", new { Search = searchText ?? string.Empty }, cancellationToken, 50);

    public async Task<TwAddRoleMemberResult?> AddRoleMemberAsync(Guid userId, int roleId, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition("INSERT INTO AccountRole (UserId, RoleId) VALUES (@UserId, @RoleId) ON DUPLICATE KEY UPDATE RoleId = VALUES(RoleId);", new { UserId = userId.ToString("D"), RoleId = roleId }, cancellationToken: cancellationToken));
        return await session.Connection.QuerySingleOrDefaultAsync<TwAddRoleMemberResult>(new CommandDefinition("SELECT AR.Id, P.UserId, P.Navigation, P.AccountName, U.Email AS EmailAddress, '' AS FirstName, '' AS LastName FROM AccountRole AR INNER JOIN Profile P ON P.UserId = AR.UserId INNER JOIN AspNetUsers U ON U.Id = P.UserId WHERE AR.UserId = @UserId AND AR.RoleId = @RoleId;", new { UserId = userId.ToString("D"), RoleId = roleId }, cancellationToken: cancellationToken));
    }

    public async Task<TwAdminPasswordChangeState> GetAdminPasswordStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var passwordHash = await session.Connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT U.PasswordHash FROM AspNetUsers U INNER JOIN AccountRole AR ON AR.UserId = U.Id INNER JOIN Role R ON R.Id = AR.RoleId WHERE R.Name = 'Administrator' ORDER BY U.Id LIMIT 1;", cancellationToken: cancellationToken));
        return passwordHash == null ? TwAdminPasswordChangeState.NeedsToBeSet : TwAdminPasswordChangeState.HasBeenChanged;
    }

    public async Task<TwAddAccountMembershipResult?> AddAccountMembershipAsync(Guid userId, int roleId, CancellationToken cancellationToken = default)
    {
        await AddRoleMembershipAsync(userId, roleId, cancellationToken);
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<TwAddAccountMembershipResult>(new CommandDefinition("SELECT AR.Id, R.Name FROM AccountRole AR INNER JOIN Role R ON R.Id = AR.RoleId WHERE AR.UserId = @UserId AND AR.RoleId = @RoleId;", new { UserId = userId.ToString("D"), RoleId = roleId }, cancellationToken: cancellationToken));
    }

    public async Task<TwAddRoleMemberResult?> AddRoleMemberByNameAsync(Guid userId, string roleName, CancellationToken cancellationToken = default)
    {
        var role = await GetRoleByNameAsync(roleName, cancellationToken);
        return role is null ? null : await AddRoleMemberAsync(userId, role.Id, cancellationToken);
    }

    public Task RemoveRoleMemberAsync(int roleId, Guid userId, CancellationToken cancellationToken = default) => ExecuteAsync("DELETE FROM AccountRole WHERE RoleId = @RoleId AND UserId = @UserId;", new { RoleId = roleId, UserId = userId.ToString("D") }, cancellationToken);
    public Task RemoveRolePermissionAsync(int id, CancellationToken cancellationToken = default) => ExecuteAsync("DELETE FROM RolePermission WHERE Id = @Id;", new { Id = id }, cancellationToken);
    public Task RemoveAccountPermissionAsync(int id, CancellationToken cancellationToken = default) => ExecuteAsync("DELETE FROM AccountPermission WHERE Id = @Id;", new { Id = id }, cancellationToken);

    public async Task<TwInsertRolePermissionResult?> InsertRolePermissionAsync(int roleId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default)
    {
        var dispositionId = await GetPermissionDispositionIdAsync(disposition, cancellationToken);
        await ExecuteAsync("INSERT INTO RolePermission (RoleId, PermissionId, Namespace, PageId, PermissionDispositionId) VALUES (@RoleId, @PermissionId, @Namespace, @PageId, @DispositionId);", new { RoleId = roleId, PermissionId = permissionId, Namespace = ns, PageId = pageId, DispositionId = dispositionId }, cancellationToken);
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<TwInsertRolePermissionResult>(new CommandDefinition("SELECT RP.Id, P.Name AS Permission, PD.Name AS PermissionDisposition, RP.Namespace, RP.PageId, COALESCE(RP.Namespace, RP.PageId) AS ResourceName FROM RolePermission RP INNER JOIN Permission P ON P.Id = RP.PermissionId INNER JOIN PermissionDisposition PD ON PD.Id = RP.PermissionDispositionId WHERE RP.RoleId = @RoleId AND RP.PermissionId = @PermissionId AND RP.PermissionDispositionId = @DispositionId AND RP.Namespace <=> @Namespace AND RP.PageId <=> @PageId ORDER BY RP.Id DESC LIMIT 1;", new { RoleId = roleId, PermissionId = permissionId, DispositionId = dispositionId, Namespace = ns, PageId = pageId }, cancellationToken: cancellationToken));
    }

    public async Task<TwInsertAccountPermissionResult?> InsertAccountPermissionAsync(Guid userId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default)
    {
        var dispositionId = await GetPermissionDispositionIdAsync(disposition, cancellationToken);
        await ExecuteAsync("INSERT INTO AccountPermission (UserId, PermissionId, Namespace, PageId, PermissionDispositionId) VALUES (@UserId, @PermissionId, @Namespace, @PageId, @DispositionId);", new { UserId = userId.ToString("D"), PermissionId = permissionId, Namespace = ns, PageId = pageId, DispositionId = dispositionId }, cancellationToken);
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<TwInsertAccountPermissionResult>(new CommandDefinition("SELECT AP.Id, P.Name AS Permission, PD.Name AS PermissionDisposition, AP.Namespace, AP.PageId, COALESCE(AP.Namespace, AP.PageId) AS ResourceName FROM AccountPermission AP INNER JOIN Permission P ON P.Id = AP.PermissionId INNER JOIN PermissionDisposition PD ON PD.Id = AP.PermissionDispositionId WHERE AP.UserId = @UserId AND AP.PermissionId = @PermissionId AND AP.PermissionDispositionId = @DispositionId AND AP.Namespace <=> @Namespace AND AP.PageId <=> @PageId ORDER BY AP.Id DESC LIMIT 1;", new { UserId = userId.ToString("D"), PermissionId = permissionId, DispositionId = dispositionId, Namespace = ns, PageId = pageId }, cancellationToken: cancellationToken));
    }

    public async Task<bool> IsAccountMemberOfRoleAsync(Guid userId, int roleId, CancellationToken cancellationToken = default) => await ScalarAsync<int>("SELECT COUNT(*) FROM AccountRole WHERE UserId = @UserId AND RoleId = @RoleId;", new { UserId = userId.ToString("D"), RoleId = roleId }, cancellationToken) > 0;
    public async Task<bool> IsAccountPermissionDefinedAsync(Guid userId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default) => await PermissionDefinedAsync("AccountPermission", "UserId", userId.ToString("D"), permissionId, disposition, ns, pageId, cancellationToken);
    public async Task<bool> IsRolePermissionDefinedAsync(int roleId, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken = default) => await PermissionDefinedAsync("RolePermission", "RoleId", roleId, permissionId, disposition, ns, pageId, cancellationToken);

    public async Task AnonymizeProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var name = $"DeletedUser_{Guid.NewGuid():N}";
        await ExecuteAsync("UPDATE Profile SET AccountName = @Name, Navigation = @Navigation, ModifiedDate = UTC_TIMESTAMP(6) WHERE UserId = @UserId;", new { UserId = userId.ToString("D"), Name = name, Navigation = TwNavigation.Clean(name) }, cancellationToken);
    }

    public Task DeleteRoleAsync(int roleId, CancellationToken cancellationToken = default)
        => ExecuteAsync("DELETE FROM Role WHERE Id = @RoleId AND IsBuiltIn = FALSE;", new { RoleId = roleId }, cancellationToken);

    public async Task<bool> InsertRoleAsync(string name, string? description, CancellationToken cancellationToken = default)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var affected = await session.Connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO Role (Name, Description, IsBuiltIn) VALUES (@Name, @Description, FALSE);",
            new { Name = name, Description = description ?? string.Empty }, cancellationToken: cancellationToken));
        return affected == 1;
    }

    public Task SetAdminPasswordChangedAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("INSERT INTO AdminPwCheck (Id, IsChanged) VALUES (1, TRUE) ON DUPLICATE KEY UPDATE IsChanged = TRUE;", new { }, cancellationToken);

    private async Task<T?> QueryProfileAsync<T>(string where, object parameters, CancellationToken cancellationToken, bool basic = false) where T : class
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var select = basic
            ? "U.Id AS UserId, U.Email AS EmailAddress, P.AccountName, P.Navigation, P.Biography, (SELECT ClaimValue FROM AspNetUserClaims WHERE UserId = P.UserId AND ClaimType = 'theme' LIMIT 1) AS Theme, (SELECT ClaimValue FROM AspNetUserClaims WHERE UserId = P.UserId AND ClaimType = 'language' LIMIT 1) AS Language"
            : "U.Id AS UserId, U.Email AS EmailAddress, P.AccountName, P.Navigation, P.Biography, P.Avatar, P.CreatedDate, P.ModifiedDate, U.EmailConfirmed, (SELECT ClaimValue FROM AspNetUserClaims WHERE UserId = P.UserId AND ClaimType = 'firstname' LIMIT 1) AS FirstName, (SELECT ClaimValue FROM AspNetUserClaims WHERE UserId = P.UserId AND ClaimType = 'lastname' LIMIT 1) AS LastName, (SELECT ClaimValue FROM AspNetUserClaims WHERE UserId = P.UserId AND ClaimType = 'timezone' LIMIT 1) AS TimeZone, (SELECT ClaimValue FROM AspNetUserClaims WHERE UserId = P.UserId AND ClaimType = 'language' LIMIT 1) AS Language, (SELECT ClaimValue FROM AspNetUserClaims WHERE UserId = P.UserId AND ClaimType LIKE '%/country' LIMIT 1) AS Country, (SELECT ClaimValue FROM AspNetUserClaims WHERE UserId = P.UserId AND ClaimType = 'theme' LIMIT 1) AS Theme";
        return await session.Connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition($"SELECT {select} FROM Profile P INNER JOIN AspNetUsers U ON U.Id = P.UserId {where};", parameters, cancellationToken: cancellationToken));
    }

    private Task<TwAccountProfile?> QueryProfileAsync(string where, object parameters, CancellationToken cancellationToken, bool basic = false) => QueryProfileAsync<TwAccountProfile>(where, parameters, cancellationToken, basic);

    private async Task<IReadOnlyList<TwAccountProfile>> QueryProfilesAsync(string suffix, object parameters, CancellationToken cancellationToken, int pageSize)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        var sql = $"SELECT U.Id AS UserId, U.Email AS EmailAddress, P.AccountName, P.Navigation, P.Biography, P.CreatedDate, P.ModifiedDate, U.EmailConfirmed, @PageSize AS PaginationPageSize FROM Profile P INNER JOIN AspNetUsers U ON U.Id = P.UserId {suffix};";
        return (await session.Connection.QueryAsync<TwAccountProfile>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).AsList();
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters, CancellationToken cancellationToken)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return (await session.Connection.QueryAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).AsList();
    }
    private async Task<T> ScalarAsync<T>(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return (await session.Connection.ExecuteScalarAsync<T>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)))!;
    }
    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        await session.Connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }
    private async Task<bool> PermissionDefinedAsync(string table, string ownerColumn, object owner, int permissionId, string disposition, string? ns, string? pageId, CancellationToken cancellationToken)
    {
        var dispositionId = await GetPermissionDispositionIdAsync(disposition, cancellationToken);
        return await ScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE {ownerColumn} = @Owner AND PermissionId = @PermissionId AND PermissionDispositionId = @DispositionId AND Namespace <=> @Namespace AND PageId <=> @PageId;", new { Owner = owner, PermissionId = permissionId, DispositionId = dispositionId, Namespace = ns, PageId = pageId }, cancellationToken) > 0;
    }

    private async Task<int> EnsureLookupIdAsync(string table, string keyColumn, string key, string? valueColumn, string? value, string? flagColumn, bool? flag, CancellationToken cancellationToken)
    {
        // Table and column names are fixed internal constants selected only by this class.
        var update = valueColumn is null ? string.Empty : $", {valueColumn} = @Value";
        if (flagColumn is not null) update += $", {flagColumn} = @Flag";
        var sql = $"INSERT INTO {table} ({keyColumn}{(valueColumn is null ? string.Empty : $", {valueColumn}")}{(flagColumn is null ? string.Empty : $", {flagColumn}")}) VALUES (@Key{(valueColumn is null ? string.Empty : ", @Value")}{(flagColumn is null ? string.Empty : ", @Flag")}) ON DUPLICATE KEY UPDATE {keyColumn} = VALUES({keyColumn}){update}; SELECT Id FROM {table} WHERE {keyColumn} = @Key;";
        await using var session = await _connectionFactory.OpenSessionAsync(cancellationToken);
        return await session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { Key = key, Value = value, Flag = flag }, cancellationToken: cancellationToken));
    }
}
