using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TightWiki.Library;
using TightWiki.Repository.Helpers;

namespace TightWiki.Tests.Integration;

public sealed class MySqlIdentityIntegrationTests
{
    [MySqlFact]
    public async Task IdentityProfilesRolesClaimsLoginsTokensAndPermissionsPersist()
    {
        var connectionString = RequireConnectionString();
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlIdentitySchemaInitializer().InitializeAsync(factory);
        SqlMapper.AddTypeHandler(new GuidTypeHandler());

        await using var provider = BuildIdentityProvider(connectionString);
        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = new MySqlUsersRepository(factory);
        var username = $"user{Guid.NewGuid():N}";
        var password = "C0ntinuum!安全-123";
        var user = new IdentityUser { UserName = username, Email = $"{Guid.NewGuid():N}@example.invalid" };

        var unicodeUsername = new IdentityUser { UserName = $"世界{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@example.invalid" };
        Assert.False((await userManager.CreateAsync(unicodeUsername, password)).Succeeded);

        var creation = await userManager.CreateAsync(user, password);
        Assert.True(creation.Succeeded, string.Join("; ", creation.Errors.Select(o => o.Description)));
        Assert.NotEqual(password, user.PasswordHash);
        Assert.True(await userManager.CheckPasswordAsync(user, password));
        Assert.False(await userManager.CheckPasswordAsync(user, password + "-wrong"));

        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        Assert.True(await userManager.VerifyUserTokenAsync(user, userManager.Options.Tokens.PasswordResetTokenProvider, "ResetPassword", resetToken));
        var oldStamp = user.SecurityStamp;
        Assert.True((await userManager.UpdateSecurityStampAsync(user)).Succeeded);
        Assert.NotEqual(oldStamp, user.SecurityStamp);

        var role = new IdentityRole($"M2-004-{Guid.NewGuid():N}");
        Assert.True((await roleManager.CreateAsync(role)).Succeeded);
        Assert.True((await userManager.AddToRoleAsync(user, role.Name!)).Succeeded);
        Assert.Contains(role.Name, await userManager.GetRolesAsync(user));
        Assert.True((await userManager.AddClaimAsync(user, new System.Security.Claims.Claim("timezone", "世界/Denver"))).Succeeded);
        Assert.Contains((await userManager.GetClaimsAsync(user)), claim => claim.Value == "世界/Denver");

        var login = new UserLoginInfo("TestProvider", $"provider-{Guid.NewGuid():N}", "Test provider");
        Assert.True((await userManager.AddLoginAsync(user, login)).Succeeded);
        Assert.Contains((await userManager.GetLoginsAsync(user)), item => item.LoginProvider == login.LoginProvider && item.ProviderKey == login.ProviderKey);
        Assert.True((await userManager.SetAuthenticationTokenAsync(user, "TestProvider", "integration", "token-value-世界")).Succeeded);
        Assert.Equal("token-value-世界", await userManager.GetAuthenticationTokenAsync(user, "TestProvider", "integration"));

        user.LockoutEnabled = true;
        Assert.True((await userManager.UpdateAsync(user)).Succeeded);
        Assert.True((await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(10))).Succeeded);
        Assert.True(await userManager.IsLockedOutAsync(user));

        var userId = Guid.Parse(user.Id);
        await users.CreateProfileAsync(userId, $"世界 Profile {username}");
        var profile = await users.GetProfileByUserIdAsync(userId);
        Assert.NotNull(profile);
        Assert.Equal(userId, profile.UserId);
        Assert.Equal(profile.UserId, (await users.GetBasicProfileByUserIdAsync(userId))!.UserId);
        Assert.Equal(profile.UserId, (await users.GetProfileByNavigationAsync(profile.Navigation))!.UserId);
        profile!.Biography = "Unicode biography 世界";
        profile.ModifiedDate = DateTime.UtcNow;
        await users.UpdateProfileAsync(profile);
        Assert.Equal("Unicode biography 世界", (await users.GetProfileByUserIdAsync(userId))!.Biography);

        var secondUser = new IdentityUser { UserName = $"second{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@example.invalid" };
        Assert.True((await userManager.CreateAsync(secondUser, password)).Succeeded);
        var secondUserId = Guid.Parse(secondUser.Id);
        await users.CreateProfileAsync(secondUserId, $"Second {Guid.NewGuid():N}");
        var secondProfile = (await users.GetProfileByUserIdAsync(secondUserId))!;
        var originalSecondNavigation = secondProfile.Navigation;
        secondProfile.Navigation = profile.Navigation;
        await Assert.ThrowsAnyAsync<Exception>(() => users.UpdateProfileAsync(secondProfile));
        Assert.Equal(originalSecondNavigation, (await users.GetProfileByUserIdAsync(secondUserId))!.Navigation);

        var customRoleName = $"Custom-{Guid.NewGuid():N}";
        var customRoleId = await users.EnsureRoleAsync(customRoleName);
        var permissionId = await users.EnsurePermissionAsync($"Permission-{Guid.NewGuid():N}");
        await users.AddRoleMembershipAsync(userId, customRoleId);
        await users.AddRolePermissionAsync(customRoleId, permissionId, "Allow", "世界");
        var effective = await users.GetEffectivePermissionsAsync(userId);
        Assert.Contains(effective, item => item.PermissionDisposition == "Allow" && item.Namespace == "世界");
        Assert.Contains(await users.GetRolesAsync(), item => item.Id == customRoleId);
        Assert.Contains(await users.GetPermissionsAsync(), item => item.Id == permissionId);
        Assert.Contains(await users.GetPermissionDispositionsAsync(), item => item.Name == "Allow");
        Assert.Contains(await users.GetEffectiveRolePermissionsAsync(customRoleName), item => item.PermissionDisposition == "Allow" && item.Namespace == "世界");

        await Assert.ThrowsAnyAsync<Exception>(() => users.CreateProfileAsync(userId, profile.AccountName));
        await using var verificationSession = await factory.OpenSessionAsync();
        var profileCount = await verificationSession.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Profile WHERE UserId = @UserId", new { UserId = user.Id });
        Assert.Equal(1, profileCount);
    }

    [MySqlFact]
    public async Task IdentityConcurrencyAndUniqueUsernamesAreEnforced()
    {
        var connectionString = RequireConnectionString();
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        await new MySqlIdentitySchemaInitializer().InitializeAsync(factory);
        await using var provider = BuildIdentityProvider(connectionString);
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var firstManager = firstScope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var secondManager = secondScope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var username = $"concurrent-{Guid.NewGuid():N}";
        var first = new IdentityUser { UserName = username, Email = $"{Guid.NewGuid():N}@example.invalid" };
        Assert.True((await firstManager.CreateAsync(first, "C0ntinuum!123")).Succeeded);
        var duplicate = new IdentityUser { UserName = username.ToUpperInvariant(), Email = $"{Guid.NewGuid():N}@example.invalid" };
        Assert.False((await firstManager.CreateAsync(duplicate, "C0ntinuum!123")).Succeeded);

        var stale = await secondManager.FindByIdAsync(first.Id);
        Assert.NotNull(stale);
        first.Email = $"updated-{Guid.NewGuid():N}@example.invalid";
        Assert.True((await firstManager.UpdateAsync(first)).Succeeded);
        stale!.Email = $"stale-{Guid.NewGuid():N}@example.invalid";
        var staleResult = await secondManager.UpdateAsync(stale);
        Assert.False(staleResult.Succeeded);
        Assert.Contains(staleResult.Errors, error => error.Code.Contains("Concurrency", StringComparison.OrdinalIgnoreCase));
    }

    private static ServiceProvider BuildIdentityProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<MySqlApplicationDbContext>(options => options.UseMySQL(connectionString));
        services.AddIdentityCore<IdentityUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Lockout.MaxFailedAccessAttempts = 5;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<MySqlApplicationDbContext>()
            .AddDefaultTokenProviders();
        return services.BuildServiceProvider();
    }

    private static string RequireConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("CONTINUUMWIKI_MYSQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("MySQL test connection is missing.");
        return value;
    }
}
