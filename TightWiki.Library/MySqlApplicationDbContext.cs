using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TightWiki.Library;

/// <summary>
/// ASP.NET Core Identity context for the explicitly managed MySQL security slice.
/// Application startup remains SQLite-only until all repositories can participate
/// in the selected provider.
/// </summary>
public sealed class MySqlApplicationDbContext(DbContextOptions<MySqlApplicationDbContext> options)
    : IdentityDbContext<IdentityUser>(options);
