using Autofac;
using Autofac.Extensions.DependencyInjection;
using Dapper;
using DiffPlex;
using DiffPlex.DiffBuilder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using TightWiki.Engine;
using TightWiki.Engine.MediaWiki;
using TightWiki.Library;
using TightWiki.Library.Database;
using TightWiki.Plugin;
using TightWiki.Plugin.Interfaces;
using TightWiki.Plugin.Interfaces.Persistence;
using TightWiki.Plugin.Interfaces.Repository;
using TightWiki.Plugin.Models;
using TightWiki.Repository.Helpers;
using TightWiki.Translations;
using static TightWiki.Plugin.TwConstants;

namespace TightWiki;

/// <summary>MariaDB-native application startup. SQLite is never constructed on this path.</summary>
internal static class MySqlDevelopmentRuntime
{
    public static async Task RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var configuration = builder.Configuration;
        if (DatabaseProviderConfiguration.GetProvider(configuration) != DatabaseProviderConfiguration.MySQL)
            throw new InvalidOperationException("The MySQL development runtime can only be selected with Database:Provider=mysql.");
        if (!builder.Environment.IsDevelopment() && !configuration.GetValue<bool>("Database:MySql:ProductionReady"))
            throw new InvalidOperationException("MariaDB production startup is not authorized. Set Database:MySql:ProductionReady=true only after the disposable readiness checklist and explicit operations approval are complete.");
        var connectionString = configuration.GetConnectionString("MySql")
            ?? configuration["Database:MySql:ConnectionString"]
            ?? throw new InvalidOperationException("A MariaDB connection string is required when Database:Provider=mysql.");
        var factory = new MySqlDatabaseConnectionFactory(connectionString);
        builder.Services.AddSingleton(factory);
        await MySqlRuntimeReadiness.ValidateServerAsync(factory);
        await new MySqlSchemaInitializer().InitializeAsync(factory);
        var seeder = new MySqlDefaultContentSeeder(factory);
        await seeder.SeedAsync([
            TwDefaultDataType.Configurations,
            TwDefaultDataType.Themes,
            TwDefaultDataType.FeatureTemplates,
            TwDefaultDataType.Translations,
            TwDefaultDataType.Menus]);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        ConfigureDataProtection(builder);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMySqlApplicationPersistence(configuration);
        builder.Services.AddSingleton<ITwMediaWikiImportRepository, MySqlMediaWikiImportRepository>();
        builder.Services.AddSingleton<MediaWikiWikitextRenderer>();
        builder.Services.AddMySqlIdentity(connectionString, options =>
        {
            options.SignIn.RequireConfirmedAccount = false;
            options.User.RequireUniqueEmail = false;
        }).AddDefaultUI();

        builder.Services.AddControllersWithViews(options =>
            options.ModelBinderProviders.Insert(0, new TwInvariantDecimalModelBinderProvider()))
            .AddDataAnnotationsLocalization()
            .AddXmlSerializerFormatters()
            .AddXmlDataContractSerializerFormatters();
        builder.Services.AddRazorPages();
        builder.Services.AddLocalization();
        builder.Services.AddSingleton<IStringLocalizerFactory, TranslationStringLocalizerFactory>();
        builder.Services.AddScoped<ITwSharedLocalizationText, SharedLocalizationText>();
        builder.Services.AddSingleton(new SupportedCultures());
        builder.Services.Configure<RequestLocalizationOptions>(options =>
        {
            var cultures = new SupportedCultures();
            options.DefaultRequestCulture = new RequestCulture("en");
            options.SupportedCultures = cultures.UICompleteCultures;
            options.SupportedUICultures = cultures.UICompleteCultures;
            options.RequestCultureProviders =
            [new QueryStringRequestCultureProvider(), new CookieRequestCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
        });
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);
        builder.Services.AddScoped<IDiffer, Differ>();
        builder.Services.AddScoped<ISideBySideDiffBuilder>(sp => new SideBySideDiffBuilder(sp.GetRequiredService<IDiffer>()));

        var applicationConfiguration = new MySqlConfigurationRepository(factory);
        var configurationManager = new WikiConfigurationManager(configuration, applicationConfiguration, (_, _) => Task.FromResult(new List<TwEmoji>()));
        builder.Services.AddSingleton(configurationManager);
        builder.Services.AddSingleton(configurationManager.WikiConfiguration);
        builder.Services.AddSingleton<ITwEmailSender, EmailSender>();

        RegisterLegacyGates(builder.Services);
        builder.Services.AddSingleton<PageMetadataRebuilder>();
        builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
        builder.Host.ConfigureContainer<ContainerBuilder>(container => container.RegisterType<WikiEngine>().As<ITwEngine>().SingleInstance());

        var app = builder.Build();
        await EnsureDevelopmentAdministratorAsync(app.Services, factory, seeder);
        await MySqlRuntimeReadiness.ValidateInstallationAsync(factory);
        TwSignedUrl.Initialize(app.Services.GetRequiredService<IDataProtectionProvider>());
        LocalizerFactory.Initialize(app.Services);
        PluginLoader.LoadPlugins(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PluginLoader"), Path.Combine(Environment.CurrentDirectory, "Plugins"), TwPersistenceProvider.MySQL);

        if (app.Environment.IsDevelopment()) app.UseDeveloperExceptionPage();
        else app.UseExceptionHandler("/Error");
        app.UseStaticFiles();
        app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();
        app.MapControllerRoute("default", "{controller=Page}/{action=Display}");
        app.MapControllerRoute("Page_Edit", "Page/{givenCanonical}/Edit");
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (MySqlDatabaseConnectionFactory readinessFactory, CancellationToken cancellationToken) =>
        {
            try
            {
                await MySqlRuntimeReadiness.ValidateServerAsync(readinessFactory, cancellationToken);
                await MySqlSchemaCompatibility.ValidateSupportedVersionsAsync(readinessFactory, cancellationToken);
                return Results.Ok(new { status = "ready", provider = "mariadb", target = "11.8" });
            }
            catch (Exception exception)
            {
                return Results.Json(new { status = "not-ready", error = exception.GetBaseException().Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        await app.RunAsync();
    }

    private static void RegisterLegacyGates(IServiceCollection services)
    {
        services.AddSingleton<ITwStatisticsRepository>(provider => new MySqlStatisticsLegacyAdapter(provider.GetRequiredService<ITwApplicationOperationsRepository>()));
        services.AddSingleton<ITwDatabaseManager>(provider => MySqlLegacyDependencyGate.Create<ITwDatabaseManager>(method =>
            method.Name == "get_StatisticsRepository" ? provider.GetRequiredService<ITwStatisticsRepository>() : null));
        services.AddSingleton<ITwConfigurationRepository>(_ => MySqlLegacyDependencyGate.Create<ITwConfigurationRepository>());
        services.AddSingleton<ITwUsersRepository>(_ => MySqlLegacyDependencyGate.Create<ITwUsersRepository>());
        services.AddSingleton<ITwPageRepository>(_ => MySqlLegacyDependencyGate.Create<ITwPageRepository>());
        services.AddSingleton<ITwEmojiRepository>(_ => MySqlLegacyDependencyGate.Create<ITwEmojiRepository>());
        services.AddSingleton<ITwLoggingRepository>(_ => MySqlLegacyDependencyGate.Create<ITwLoggingRepository>());
    }

    private static void ConfigureDataProtection(WebApplicationBuilder builder)
    {
        var keysPath = builder.Configuration["DataProtection:KeysPath"];
        if (string.IsNullOrWhiteSpace(keysPath))
        {
            if (!builder.Environment.IsDevelopment())
                throw new InvalidOperationException("MariaDB production startup requires DataProtection:KeysPath for persistent authentication and signed-link keys.");
            return;
        }

        var directory = new DirectoryInfo(keysPath);
        if (!directory.Exists)
            directory.Create();
        var probe = Path.Combine(directory.FullName, $".continuumwiki-write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"MariaDB startup cannot write DataProtection:KeysPath '{directory.FullName}'.", exception);
        }

        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(directory)
            .SetApplicationName("ContinuumWiki.NET");
    }

    private static async Task EnsureDevelopmentAdministratorAsync(IServiceProvider services, MySqlDatabaseConnectionFactory factory, MySqlDefaultContentSeeder seeder)
    {
        await using var session = await factory.OpenSessionAsync();
        var administratorCount = await session.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AccountRole AR INNER JOIN Role R ON R.Id = AR.RoleId WHERE R.Name = 'Administrator';");
        if (administratorCount == 0)
        {
            var configuration = services.GetRequiredService<IConfiguration>();
            var password = configuration["Database:MySql:DevelopmentAdminPassword"];
            if (string.IsNullOrWhiteSpace(password))
                throw new InvalidOperationException("MySQL development readiness requires an Administrator account. Set Database:MySql:DevelopmentAdminPassword only for the disposable development database; no account is created automatically without it.");

            var userManager = services.GetRequiredService<UserManager<IdentityUser>>();
            var userName = configuration["Database:MySql:DevelopmentAdminUser"] ?? "mysql-dev-admin";
            var user = await userManager.FindByNameAsync(userName);
            if (user == null)
            {
                user = new IdentityUser { UserName = userName, Email = configuration["Database:MySql:DevelopmentAdminEmail"] };
                var result = await userManager.CreateAsync(user, password);
                if (!result.Succeeded)
                    throw new InvalidOperationException($"MySQL development Administrator creation failed: {string.Join("; ", result.Errors.Select(error => error.Description))}");
            }

            var userId = Guid.Parse(user.Id);
            var identity = services.GetRequiredService<ITwApplicationIdentityRepository>();
            if (await identity.GetBasicProfileByUserIdAsync(userId) == null)
                await identity.CreateProfileAsync(userId, userName);
            var roleId = await session.Connection.ExecuteScalarAsync<int>("SELECT Id FROM Role WHERE Name = 'Administrator';");
            await session.Connection.ExecuteAsync("INSERT IGNORE INTO AccountRole (UserId, RoleId) VALUES (@UserId, @RoleId);", new { UserId = user.Id, RoleId = roleId });
            await session.Connection.ExecuteAsync("""
                INSERT IGNORE INTO RolePermission (RoleId, PermissionId, Namespace, PageId, PermissionDispositionId)
                SELECT @RoleId, P.Id, NULL, NULL, PD.Id
                FROM Permission P
                CROSS JOIN PermissionDisposition PD
                WHERE PD.Name = 'Allow';
                """, new { RoleId = roleId });
            await session.Connection.ExecuteAsync("""
                INSERT IGNORE INTO AccountPermission (UserId, PermissionId, Namespace, PageId, PermissionDispositionId)
                SELECT @UserId, P.Id, NULL, NULL, PD.Id
                FROM Permission P
                CROSS JOIN PermissionDisposition PD
                WHERE PD.Name = 'Allow';
                """, new { UserId = user.Id });
        }

        await seeder.SeedAsync([
            TwDefaultDataType.HelpPages,
            TwDefaultDataType.IncludePages,
            TwDefaultDataType.BuiltinPages]);
    }
}
